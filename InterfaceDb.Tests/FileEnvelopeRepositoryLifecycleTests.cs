using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FileEnvelopeRepositoryLifecycleTests
{
    [TestMethod]
    public async Task InitializeAsync_ConcurrentWaitersShareActivationAndDisposalWaitsForWatcherStart()
    {
        var root = CreateTemporaryDirectory();
        var source = new BlockingChangeSource<ITestRecord>();
        var repository = CreateRepository(root, source, hydrateOnInitialize: false);
        try
        {
            var first = repository.InitializeAsync().AsTask();
            await source.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var concurrent = Enumerable.Range(0, 8)
                .Select(_ => repository.InitializeAsync().AsTask())
                .ToArray();
            using var canceledWait = new CancellationTokenSource();
            canceledWait.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => repository.InitializeAsync(canceledWait.Token).AsTask());

            var firstDisposal = repository.DisposeAsync().AsTask();
            var concurrentDisposal = repository.DisposeAsync().AsTask();
            Assert.AreSame(firstDisposal, concurrentDisposal);
            Assert.IsFalse(firstDisposal.IsCompleted);
            Assert.AreEqual(1, source.StartCount);

            source.ReleaseStart.TrySetResult();
            await first;
            await Task.WhenAll(concurrent);
            await firstDisposal;

            Assert.AreEqual(1, source.StartCount);
            Assert.AreEqual(1, source.EffectiveDisposeCount);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => repository.InitializeAsync().AsTask());
        }
        finally
        {
            source.ReleaseStart.TrySetResult();
            await repository.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_BuffersStorageChangesUntilLoadedCacheIsInstalled()
    {
        var root = CreateTemporaryDirectory();
        var payload = Path.Combine(root, "1.0.0", "stale.mpk");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        await File.WriteAllBytesAsync(payload, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "bootstrap-handoff", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<ITestRecord>>();
        var source = new BufferedChangeSource<ITestRecord>();
        var coordinator = new CoalescingFileWriteCoordinator<ITestRecord>(
            new SidecarFileWriteLock<ITestRecord>(policy),
            config,
            policy);
        var factoryCalls = 0;
        var repository = new FileEnvelopeRepository<ITestRecord>(
            config,
            new ObjectReaderAndWriter<ITestRecord>(
                new RejectingReader<ITestRecord>(),
                new RejectingWriter<ITestRecord>()),
            cache,
            coordinator,
            policy,
            () =>
            {
                if (Interlocked.Increment(ref factoryCalls) == 1)
                {
                    // Model an external delete arriving while the disk walk is installing the
                    // corresponding skeleton. It must be replayed after that install.
                    source.PublishDuringBootstrap(new EnvelopeStorageChange(
                        EnvelopeStorageChangeType.Deleted,
                        payload,
                        null));
                }

                return new FileObjectEnvelope<ITestRecord>
                {
                    EnvelopeCacheKey = string.Empty,
                    VersionTag = "1.0.0"
                };
            },
            storageChangeSource: source,
            storageChangeHandler: new RemovingChangeHandler<ITestRecord>(cache));

        try
        {
            await repository.InitializeAsync();

            Assert.AreEqual(1, source.BufferedStartCount);
            Assert.AreEqual(1, source.CompleteCount);
            Assert.IsFalse(repository.Exists("stale"));
        }
        finally
        {
            await repository.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IndexedInitialization_UsesOneMaterializationPassAndRepeatedCallIsReadyFastPath()
    {
        const int recordCount = 128;
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        for (var index = 0; index < recordCount; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(versionRoot, $"record-{index:D4}.mpk"), [0x01]);
        }

        var reader = new CountingReader();
        var repository = CreateRepository(
            root,
            storageChangeSource: null,
            hydrateOnInitialize: true,
            reader);
        await using var manager = new SearchIndexManager<IIndexedTestRecord>(
            repository,
            TestStoragePolicy(hydrateOnInitialize: true));
        try
        {
            await manager.InitializeAsync();
            Assert.AreEqual(
                recordCount,
                reader.ReadCount,
                "Index initialization must materialize each file once, not once for hydration and again for indexing.");

            var repeated = manager.InitializeAsync();
            Assert.IsTrue(repeated.IsCompletedSuccessfully);
            await repeated;
            Assert.AreEqual(recordCount, reader.ReadCount);
        }
        finally
        {
            await repository.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DependencyInjection_UsesOneDisposableOwnerAndNonOwningUnkeyedFacade()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = new TestConfig<ITestRecord>(root, "file-owner", TestStoragePolicy(false));
            var services = new ServiceCollection();
            services.AddFileRepository(config);
            await using var provider = services.BuildServiceProvider();

            var owner = provider.GetRequiredKeyedService<IEnvelopeRepository<ITestRecord>>("file-owner");
            var compatibilityView = provider.GetRequiredService<IEnvelopeRepository<ITestRecord>>();
            var keyedRepository = provider.GetRequiredKeyedService<IRepository<ITestRecord>>("file-owner");
            var unkeyedRepository = provider.GetRequiredService<IRepository<ITestRecord>>();

            Assert.IsInstanceOfType<IAsyncDisposable>(owner);
            Assert.IsFalse(compatibilityView is IAsyncDisposable);
            Assert.AreNotSame(owner, compatibilityView);
            Assert.AreSame(keyedRepository, unkeyedRepository);

            await keyedRepository.InitializeAsync();
            Assert.IsTrue(keyedRepository.InitializeAsync().IsCompletedSuccessfully);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteManyAsync_NotificationsDisabled_PreservesBulkFailureWhenWriterCancelsToken()
    {
        var root = CreateTemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        var policy = TestStoragePolicy(hydrateOnInitialize: false) with
        {
            RaiseNotifications = false
        };
        var config = new TestConfig<IIndexedTestRecord>(root, "partial-failure-no-notifications", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<IIndexedTestRecord>>();
        var coordinator = new CoalescingFileWriteCoordinator<IIndexedTestRecord>(
            new SidecarFileWriteLock<IIndexedTestRecord>(policy),
            config,
            policy);
        var writer = new CancelingPartialFailureWriter(root, cancellationSource);
        var repository = new FileEnvelopeRepository<IIndexedTestRecord>(
            config,
            new ObjectReaderAndWriter<IIndexedTestRecord>(
                new RejectingReader<IIndexedTestRecord>(),
                writer),
            cache,
            coordinator,
            policy,
            () => new FileObjectEnvelope<IIndexedTestRecord>
            {
                EnvelopeCacheKey = string.Empty,
                VersionTag = "1.0.0"
            });

        try
        {
            IIndexedTestRecord[] attempted =
            [
                new IndexedTestRecord { Id = "committed" },
                new IndexedTestRecord { Id = "failed" }
            ];

            var exception = await Assert.ThrowsExactlyAsync<StorageBulkOperationException>(
                () => repository.WriteManyAsync(attempted, cancellationSource.Token).AsTask());

            Assert.AreSame(writer.Failure, exception);
            Assert.IsTrue(cancellationSource.IsCancellationRequested);
            CollectionAssert.AreEqual(new[] { 0 }, exception.SucceededIndexes.ToArray());
        }
        finally
        {
            await repository.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_DisposalCanRaceActiveRescanAndIsIdempotent()
    {
        var root = CreateTemporaryDirectory();
        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "change-source-race", policy);
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(config, policy);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += async _ =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
        };

        try
        {
            await source.StartAsync();
            await source.StartAsync();
            var payload = Path.Combine(root, "1.0.0", "external.mpk");
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            await File.WriteAllBytesAsync(payload, [0x01]);

            var rescan = source.RescanAsync().AsTask();
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = source.DisposeAsync().AsTask();
            var repeatedDisposal = source.DisposeAsync().AsTask();
            Assert.AreSame(disposal, repeatedDisposal);
            await disposal;

            releaseHandler.TrySetResult();
            await rescan;

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => source.StartAsync().AsTask());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => source.RescanAsync().AsTask());
        }
        finally
        {
            releaseHandler.TrySetResult();
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_BufferedRescanPublishesOnlyAtAtomicBootstrapHandoff()
    {
        var root = CreateTemporaryDirectory();
        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "buffered-rescan", policy);
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(config, policy);
        var observed = new List<EnvelopeStorageChange>();
        source.Changed += change =>
        {
            observed.Add(change);
            return ValueTask.CompletedTask;
        };

        try
        {
            var bootstrap = (IFileEnvelopeStorageChangeBootstrap)source;
            await bootstrap.StartBufferingAsync();
            var payload = Path.Combine(root, "1.0.0", "during-bootstrap.mpk");
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            await File.WriteAllBytesAsync(payload, [0x01]);

            await source.RescanAsync();
            Assert.IsEmpty(observed);

            await bootstrap.CompleteBootstrapAsync(Array.Empty<string>());
            Assert.HasCount(1, observed);
            Assert.AreEqual(EnvelopeStorageChangeType.Created, observed[0].ChangeType);
            Assert.AreEqual(Path.GetFullPath(payload), observed[0].NewPath);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_AcceptsCaseDistinctPathsOnCaseSensitiveVolumes()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var lower = Path.Combine(versionRoot, "case.mpk");
        var upper = Path.Combine(versionRoot, "CASE.mpk");
        await File.WriteAllBytesAsync(lower, [0x01]);
        await File.WriteAllBytesAsync(upper, [0x02]);
        if (Directory.EnumerateFiles(versionRoot, "*.mpk").Count() != 2)
        {
            Assert.Inconclusive("The test volume is not case-sensitive.");
        }

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(
            new TestConfig<ITestRecord>(root, "case-distinct-source", policy),
            policy);
        try
        {
            await source.StartAsync();
            await source.RescanAsync();
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_PathOnlyAcknowledgementCannotSuppressAnExternalRevision()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var payloadPath = Path.Combine(versionRoot, "shared.mpk");
        await File.WriteAllBytesAsync(payloadPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false) with
        {
            WatchStorageChanges = true,
            StorageChangeDebounce = TimeSpan.FromSeconds(5)
        };
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(
            new TestConfig<ITestRecord>(root, "path-only-ack", policy),
            policy);
        EnvelopeStorageChange? observed = null;
        source.Changed += change =>
        {
            if (change.ChangeType is not EnvelopeStorageChangeType.Error)
            {
                observed = change;
            }

            return ValueTask.CompletedTask;
        };

        try
        {
            await source.StartAsync();
            await File.WriteAllBytesAsync(payloadPath, [0x02, 0x03]);

            // The caller supplies only a path, so the source cannot prove that these bytes are
            // the caller's revision rather than a later commit from another writer.
            source.AcknowledgeLocalChange(payloadPath);
            await source.RescanAsync();

            Assert.IsNotNull(observed);
            Assert.AreEqual(EnvelopeStorageChangeType.Changed, observed.ChangeType);
            Assert.AreEqual(payloadPath, observed.NewPath);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_WatcherSignalPublishesEqualStampRewriteWhileRescanStaysQuiet()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var payloadPath = Path.Combine(versionRoot, "equal-stamp.mpk");
        await File.WriteAllBytesAsync(payloadPath, [0x01, 0x02, 0x03, 0x04]);
        var originalLastWriteUtc = File.GetLastWriteTimeUtc(payloadPath);

        var policy = TestStoragePolicy(hydrateOnInitialize: false) with
        {
            StorageChangeDebounce = TimeSpan.FromMilliseconds(10)
        };
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(
            new TestConfig<ITestRecord>(root, "equal-stamp-watcher", policy),
            policy);
        var completion = new TaskCompletionSource<EnvelopeStorageChange>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCount = 0;
        source.Changed += change =>
        {
            Interlocked.Increment(ref observedCount);
            completion.TrySetResult(change);
            return ValueTask.CompletedTask;
        };

        try
        {
            await source.StartAsync();
            await File.WriteAllBytesAsync(payloadPath, [0x04, 0x03, 0x02, 0x01]);
            File.SetLastWriteTimeUtc(payloadPath, originalLastWriteUtc);

            var rewritten = new FileInfo(payloadPath);
            rewritten.Refresh();
            Assert.AreEqual(4, rewritten.Length);
            Assert.AreEqual(originalLastWriteUtc, rewritten.LastWriteTimeUtc);

            // A metadata-only rescan cannot distinguish this revision and must remain bounded.
            await source.RescanAsync();
            Assert.AreEqual(0, Volatile.Read(ref observedCount));

            // This is the same path used by Created/Changed watcher callbacks, invoked directly
            // so the regression remains deterministic on every supported test platform.
            await source.ObservePath(payloadPath).WaitAsync(TimeSpan.FromSeconds(10));
            var observed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(EnvelopeStorageChangeType.Changed, observed.ChangeType);
            Assert.AreEqual(payloadPath, observed.NewPath);
            await source.RescanAsync();
            Assert.AreEqual(1, Volatile.Read(ref observedCount));
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_WatcherRenamePublishesOnePairedRenameAndAdvancesBothSnapshots()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var originalPath = Path.Combine(versionRoot, "before.mpk");
        var renamedPath = Path.Combine(versionRoot, "after.mpk");
        await File.WriteAllBytesAsync(originalPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false) with
        {
            StorageChangeDebounce = TimeSpan.FromMilliseconds(10)
        };
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(
            new TestConfig<ITestRecord>(root, "paired-rename-watcher", policy),
            policy);
        var completion = new TaskCompletionSource<EnvelopeStorageChange>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCount = 0;
        source.Changed += change =>
        {
            Interlocked.Increment(ref observedCount);
            completion.TrySetResult(change);
            return ValueTask.CompletedTask;
        };

        try
        {
            await source.StartAsync();
            File.Move(originalPath, renamedPath);

            // Invoke the exact paired path used by FileSystemWatcher.Renamed without depending on
            // the host filesystem's event timing or event-shape conventions.
            await source.ObserveRename(originalPath, renamedPath).WaitAsync(TimeSpan.FromSeconds(10));
            var observed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(EnvelopeStorageChangeType.Renamed, observed.ChangeType);
            Assert.AreEqual(originalPath, observed.OldPath);
            Assert.AreEqual(renamedPath, observed.NewPath);

            // A successful paired refresh removes the source snapshot and installs the target;
            // an immediate rescan therefore must not emit follow-up Deleted/Created events.
            await source.RescanAsync();
            Assert.AreEqual(1, Volatile.Read(ref observedCount));
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeSource_RescanPublishesCaseOnlyRenameOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test targets case-insensitive Windows path identity.");
        }

        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var originalPath = Path.Combine(versionRoot, "CaseOnly.mpk");
        var intermediatePath = Path.Combine(versionRoot, "rename-intermediate.mpk");
        var renamedPath = Path.Combine(versionRoot, "caseonly.mpk");
        await File.WriteAllBytesAsync(originalPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "case-only-rename", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<ITestRecord>>();
        var handler = new FileEnvelopeStorageChangeHandler<ITestRecord>(
            config,
            policy,
            new FixedEnvelopeSerializer<ITestRecord>(new FileObjectEnvelope<ITestRecord>
            {
                EnvelopeCacheKey = "CaseOnly",
                VersionTag = "1.0.0",
                State = StorageObjectState.Active
            }),
            new RejectingEnvelopeLifecycle<ITestRecord>(),
            cache);
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(
            config,
            policy);
        var observed = new List<EnvelopeStorageChange>();
        source.Changed += async change =>
        {
            _ = await handler.HandleAsync(change);
            observed.Add(change);
        };

        try
        {
            _ = await handler.HandleAsync(new EnvelopeStorageChange(
                EnvelopeStorageChangeType.Created,
                null,
                originalPath));
            Assert.IsTrue(cache.ContainsKey("CaseOnly"));
            await source.StartAsync();
            File.Move(originalPath, intermediatePath);
            File.Move(intermediatePath, renamedPath);
            var actualPath = Directory.EnumerateFiles(versionRoot, "*.mpk").Single();
            if (!StringComparer.Ordinal.Equals(actualPath, renamedPath))
            {
                Assert.Inconclusive("The test volume did not preserve the requested file-name casing.");
            }

            await source.RescanAsync();

            Assert.HasCount(1, observed);
            Assert.AreEqual(EnvelopeStorageChangeType.Renamed, observed[0].ChangeType);
            Assert.AreEqual(originalPath, observed[0].OldPath);
            Assert.AreEqual(renamedPath, observed[0].NewPath);
            Assert.IsFalse(cache.ContainsKey("CaseOnly"));
            Assert.IsTrue(cache.ContainsKey("caseonly"));

            observed.Clear();
            File.Delete(renamedPath);
            await source.RescanAsync();

            Assert.HasCount(1, observed);
            Assert.AreEqual(EnvelopeStorageChangeType.Deleted, observed[0].ChangeType);
            Assert.AreEqual(renamedPath, observed[0].OldPath);
            Assert.IsFalse(cache.ContainsKey("caseonly"));
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PendingWriteRecovery_DoesNotCollapseCaseDistinctPayloads()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var lowerProbe = Path.Combine(versionRoot, "case-probe");
        var upperProbe = Path.Combine(versionRoot, "CASE-PROBE");
        await File.WriteAllBytesAsync(lowerProbe, [0x01]);
        await File.WriteAllBytesAsync(upperProbe, [0x02]);
        if (Directory.EnumerateFiles(versionRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Distinct(StringComparer.Ordinal)
            .Count() != 2)
        {
            Assert.Inconclusive("The test volume is not case-sensitive.");
        }

        File.Delete(lowerProbe);
        File.Delete(upperProbe);
        var lowerPayload = Path.Combine(versionRoot, "case.mpk");
        var upperPayload = Path.Combine(versionRoot, "CASE.mpk");
        var lowerReady = lowerPayload + ".pending.0000000000000000001.lower.ready";
        var upperReady = upperPayload + ".pending.0000000000000000002.upper.ready";
        await File.WriteAllBytesAsync(lowerReady, [0x11]);
        await File.WriteAllBytesAsync(upperReady, [0x22]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "case-distinct-pending", policy);
        var coordinator = new CoalescingFileWriteCoordinator<ITestRecord>(
            new SidecarFileWriteLock<ITestRecord>(policy),
            config,
            policy);
        try
        {
            await coordinator.DrainPendingWritesAsync(root, ".mpk");

            CollectionAssert.AreEqual(new byte[] { 0x11 }, await File.ReadAllBytesAsync(lowerPayload));
            CollectionAssert.AreEqual(new byte[] { 0x22 }, await File.ReadAllBytesAsync(upperPayload));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangeHandler_UsesRenamedFileNameAsPhysicalCacheIdentity()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var originalPath = Path.Combine(versionRoot, "serialized-old-physical.mpk");
        var renamedPath = Path.Combine(versionRoot, "renamed-physical.mpk");
        await File.WriteAllBytesAsync(originalPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<ITestRecord>(root, "renamed-identity", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<ITestRecord>>();
        var handler = new FileEnvelopeStorageChangeHandler<ITestRecord>(
            config,
            policy,
            new FixedEnvelopeSerializer<ITestRecord>(new FileObjectEnvelope<ITestRecord>
            {
                EnvelopeCacheKey = "serialized-old-physical",
                VersionTag = "1.0.0",
                State = StorageObjectState.Active
            }),
            new RejectingEnvelopeLifecycle<ITestRecord>(),
            cache);
        var source = new FileEnvelopeStorageChangeSource<ITestRecord>(config, policy);
        source.Changed += async change => _ = await handler.HandleAsync(change);
        try
        {
            await source.StartAsync();
            await handler.HandleAsync(new EnvelopeStorageChange(
                EnvelopeStorageChangeType.Created,
                null,
                originalPath));
            Assert.IsTrue(cache.ContainsKey("serialized-old-physical"));

            File.Move(originalPath, renamedPath);
            await source.RescanAsync();

            Assert.IsTrue(cache.ContainsKey("renamed-physical"));
            Assert.IsFalse(cache.ContainsKey("serialized-old-physical"));

            File.Delete(renamedPath);
            await source.RescanAsync();
            Assert.IsFalse(cache.ContainsKey("renamed-physical"));
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileObjectReader_UsesFileNameAsPhysicalIdentityWhenSerializedKeyIsStale(bool seedCache)
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var renamedPath = Path.Combine(versionRoot, "renamed-physical.mpk");
        await File.WriteAllBytesAsync(renamedPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false);
        var config = new TestConfig<IIndexedTestRecord>(root, "renamed-reader-identity", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<IIndexedTestRecord>>();
        if (seedCache)
        {
            cache.AddOrUpdate(
                "renamed-physical",
                "renamed-physical",
                new FileObjectEnvelope<IIndexedTestRecord>
                {
                    EnvelopeCacheKey = "renamed-physical",
                    VersionTag = "1.0.0",
                    Location = renamedPath,
                    State = StorageObjectState.Active
                });
        }

        var source = new FileEnvelopeStorageChangeSource<IIndexedTestRecord>(config, policy);
        var coordinator = new CoalescingFileWriteCoordinator<IIndexedTestRecord>(
            new SidecarFileWriteLock<IIndexedTestRecord>(policy),
            config,
            policy);
        var serializedEnvelope = new FileObjectEnvelope<IIndexedTestRecord>
        {
            EnvelopeCacheKey = "serialized-old-physical",
            VersionTag = "1.0.0",
            Location = renamedPath,
            State = StorageObjectState.Active,
            ObjectInstance = new IndexedTestRecord { Id = "record-1" }
        };
        var reader = new FileObjectReader<IIndexedTestRecord>(
            new FixedEnvelopeSerializer<IIndexedTestRecord>(serializedEnvelope),
            new IndexedEnvelopeLifecycle(),
            cache,
            config,
            policy,
            coordinator,
            source);

        try
        {
            var entity = await reader.ReadAsync(new FileInfo(renamedPath));

            Assert.IsNotNull(entity.Envelope);
            Assert.AreEqual("renamed-physical", entity.Envelope.EnvelopeCacheKey);
            Assert.AreEqual(renamedPath, entity.Envelope.Location);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileObjectReader_NormalizesPhysicalIdentityOnStrongCacheFastPath()
    {
        var root = CreateTemporaryDirectory();
        var versionRoot = Path.Combine(root, "1.0.0");
        Directory.CreateDirectory(versionRoot);
        var renamedPath = Path.Combine(versionRoot, "renamed-physical.mpk");
        await File.WriteAllBytesAsync(renamedPath, [0x01]);

        var policy = TestStoragePolicy(hydrateOnInitialize: false) with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject
        };
        var config = new TestConfig<IIndexedTestRecord>(root, "renamed-reader-strong-cache", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<IIndexedTestRecord>>();
        var cachedEntity = new IndexedTestRecord { Id = "record-1" };
        var cachedEnvelope = new FileObjectEnvelope<IIndexedTestRecord>
        {
            EnvelopeCacheKey = "serialized-old-physical",
            VersionTag = "1.0.0",
            Location = Path.Combine(versionRoot, "serialized-old-physical.mpk"),
            State = StorageObjectState.Active,
            ObjectInstance = cachedEntity
        };
        cachedEntity.Envelope = cachedEnvelope;
        cache.AddOrUpdate("renamed-physical", "renamed-physical", cachedEnvelope);

        var source = new FileEnvelopeStorageChangeSource<IIndexedTestRecord>(config, policy);
        var coordinator = new CoalescingFileWriteCoordinator<IIndexedTestRecord>(
            new SidecarFileWriteLock<IIndexedTestRecord>(policy),
            config,
            policy);
        var reader = new FileObjectReader<IIndexedTestRecord>(
            new FixedEnvelopeSerializer<IIndexedTestRecord>(cachedEnvelope),
            new IndexedEnvelopeLifecycle(),
            cache,
            config,
            policy,
            coordinator,
            source);

        try
        {
            var entity = await reader.ReadAsync(new FileInfo(renamedPath));

            Assert.AreSame(cachedEntity, entity);
            var returnedEnvelope = entity.Envelope
                ?? throw new AssertFailedException("Cached entity should retain its envelope.");
            Assert.AreSame(cachedEnvelope, returnedEnvelope);
            Assert.AreEqual("renamed-physical", returnedEnvelope.EnvelopeCacheKey);
            Assert.AreEqual(renamedPath, returnedEnvelope.Location);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static FileEnvelopeRepository<TContract> CreateRepository<TContract>(
        string root,
        IEnvelopeStorageChangeSource<TContract>? storageChangeSource,
        bool hydrateOnInitialize,
        IObjectReader<TContract>? reader = null)
        where TContract : class, IStoredContract<TContract>
    {
        var policy = TestStoragePolicy(hydrateOnInitialize);
        var config = new TestConfig<TContract>(root, $"test-{Guid.NewGuid():N}", policy);
        var cache = new EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>>();
        var coordinator = new CoalescingFileWriteCoordinator<TContract>(
            new SidecarFileWriteLock<TContract>(policy),
            config,
            policy);
        var readerAndWriter = new ObjectReaderAndWriter<TContract>(
            reader ?? new RejectingReader<TContract>(),
            new RejectingWriter<TContract>());
        return new FileEnvelopeRepository<TContract>(
            config,
            readerAndWriter,
            cache,
            coordinator,
            policy,
            () => new FileObjectEnvelope<TContract>
            {
                EnvelopeCacheKey = string.Empty,
                VersionTag = "1.0.0"
            },
            storageChangeSource: storageChangeSource,
            storageChangeHandler: storageChangeSource is null
                ? null
                : new NoopChangeHandler<TContract>());
    }

    private static StoragePolicy TestStoragePolicy(bool hydrateOnInitialize) => StoragePolicy.Default with
    {
        HydrateOnInitialize = hydrateOnInitialize,
        ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
        WatchStorageChanges = false,
        UseSidecarWriteLocks = false
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "InterfaceDbFileLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestConfig<TContract>(string root, string key, StoragePolicy policy)
        : IDbContractConfigDto<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public string EnvelopeDICacheKey => key;
        public string ContractName => key;
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => policy;
    }

    private sealed class BlockingChangeSource<TContract> : IEnvelopeStorageChangeSource<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        private int _startCount;
        private int _disposed;

#pragma warning disable CS0067
        public event Func<EnvelopeStorageChange, ValueTask>? Changed;
#pragma warning restore CS0067

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount => Volatile.Read(ref _startCount);
        public int EffectiveDisposeCount => Volatile.Read(ref _disposed);

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            StartEntered.TrySetResult();
            await ReleaseStart.Task.WaitAsync(cancellationToken);
        }

        public void AcknowledgeLocalChange(string providerLocator)
        {
        }

        public ValueTask RescanAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BufferedChangeSource<TContract> :
        IEnvelopeStorageChangeSource<TContract>,
        IFileEnvelopeStorageChangeBootstrap
        where TContract : class, IStoredContract<TContract>
    {
        private readonly Queue<EnvelopeStorageChange> _changes = new();
        private int _buffering;
        private int _bufferedStartCount;
        private int _completeCount;

        public event Func<EnvelopeStorageChange, ValueTask>? Changed;

        public int BufferedStartCount => Volatile.Read(ref _bufferedStartCount);

        public int CompleteCount => Volatile.Read(ref _completeCount);

        public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Repository should use the buffered bootstrap path.");

        public ValueTask StartBufferingAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _bufferedStartCount);
            Volatile.Write(ref _buffering, 1);
            return ValueTask.CompletedTask;
        }

        public async ValueTask CompleteBootstrapAsync(
            IReadOnlyCollection<string> loadedProviderLocators,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(loadedProviderLocators);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _completeCount);
            Volatile.Write(ref _buffering, 0);
            while (_changes.Count != 0)
            {
                var change = _changes.Dequeue();
                var handlers = Changed;
                if (handlers is null)
                {
                    continue;
                }

                foreach (Func<EnvelopeStorageChange, ValueTask> handler in handlers.GetInvocationList())
                {
                    await handler(change);
                }
            }
        }

        public void PublishDuringBootstrap(EnvelopeStorageChange change)
        {
            if (Volatile.Read(ref _buffering) != 0)
            {
                _changes.Enqueue(change);
            }
        }

        public void AcknowledgeLocalChange(string providerLocator)
        {
        }

        public ValueTask RescanAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RemovingChangeHandler<TContract>(
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> cache)
        : IEnvelopeStorageChangeHandler<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public ValueTask<EnvelopeStorageChangeResult<TContract>> HandleAsync(
            EnvelopeStorageChange change,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalKey = Path.GetFileNameWithoutExtension(change.OldPath ?? change.NewPath)
                ?? throw new InvalidOperationException("Test change requires a physical path.");
            cache.TryGetValue(physicalKey, out var oldEnvelope);
            cache.Remove(physicalKey);
            return ValueTask.FromResult(new EnvelopeStorageChangeResult<TContract>(
                change.ChangeType,
                oldEnvelope,
                null));
        }
    }

    private sealed class NoopChangeHandler<TContract> : IEnvelopeStorageChangeHandler<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public ValueTask<EnvelopeStorageChangeResult<TContract>> HandleAsync(
            EnvelopeStorageChange change,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle test does not publish storage changes.");
    }

    private sealed class RejectingReader<TContract> : IObjectReader<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This lifecycle test does not read payloads.");
    }

    private sealed class RejectingWriter<TContract> : IObjectWriter<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public ValueTask<TContract> WriteAsync(
            TContract envalope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This lifecycle test does not write payloads.");
    }

    private sealed class CancelingPartialFailureWriter(
        string root,
        CancellationTokenSource cancellationSource) : IObjectWriter<IIndexedTestRecord>
    {
        public StorageBulkOperationException? Failure { get; private set; }

        public ValueTask<IIndexedTestRecord> WriteAsync(
            IIndexedTestRecord envalope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This regression test exercises the bulk writer directly.");

        public ValueTask<IReadOnlyList<IIndexedTestRecord>> WriteManyAsync(
            IEnumerable<IIndexedTestRecord> envalopes,
            CancellationToken cancellationToken = default)
        {
            var materialized = envalopes.ToArray();
            if (materialized.Length != 2)
            {
                throw new InvalidOperationException("This regression writer requires exactly two objects.");
            }

            var committed = (IndexedTestRecord)materialized[0];
            var committedPath = Path.Combine(root, "1.0.0", "committed.mpk");
            committed.Envelope = new FileObjectEnvelope<IIndexedTestRecord>
            {
                EnvelopeCacheKey = committed.Id,
                VersionTag = "1.0.0",
                Location = committedPath,
                State = StorageObjectState.Active,
                ObjectInstance = committed
            };

            var failed = materialized[1];
            var failedPath = Path.Combine(root, "1.0.0", "failed.mpk");
            Failure = new StorageBulkOperationException(
                StorageOperation.Write,
                [0],
                [
                    new StorageBulkOperationFailure(
                        1,
                        failed.Id,
                        failedPath,
                        new IOException("Simulated durable write failure."))
                ]);

            cancellationSource.Cancel();
            throw Failure;
        }
    }

    private sealed class FixedEnvelopeSerializer<TContract>(IObjectEnvelope<TContract> envelope)
        : IObjectSerializer<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public byte[] Serialize(IObjectEnvelope<TContract> envalope) =>
            throw new InvalidOperationException("This lifecycle test does not serialize payloads.");

        public IObjectEnvelope<TContract> Deserialize(byte[] data) => envelope;
    }

    private sealed class RejectingEnvelopeLifecycle<TContract> : IEnvelopeLifecycle<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public ValueTask<IObjectEnvelope<TContract>> WrapAsync(TContract entity) =>
            throw new InvalidOperationException("This lifecycle test does not wrap objects.");

        public TContract Unwrap(IObjectEnvelope<TContract> envelope) =>
            throw new InvalidOperationException("The fixed test envelope has no object instance.");
    }

    private sealed class IndexedEnvelopeLifecycle : IEnvelopeLifecycle<IIndexedTestRecord>
    {
        public ValueTask<IObjectEnvelope<IIndexedTestRecord>> WrapAsync(IIndexedTestRecord entity) =>
            throw new InvalidOperationException("This lifecycle test does not wrap objects.");

        public IIndexedTestRecord Unwrap(IObjectEnvelope<IIndexedTestRecord> envelope)
        {
            var entity = envelope.ObjectInstance
                ?? throw new InvalidOperationException("The fixed test envelope has no object instance.");
            ((IndexedTestRecord)entity).Envelope = envelope;
            return entity;
        }
    }

    private sealed class CountingReader : IObjectReader<IIndexedTestRecord>
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<IIndexedTestRecord> ReadAsync(
            FileInfo file,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            var entity = new IndexedTestRecord
            {
                Id = Path.GetFileNameWithoutExtension(file.Name)
            };
            var envelope = new FileObjectEnvelope<IIndexedTestRecord>
            {
                EnvelopeCacheKey = entity.Id,
                VersionTag = "1.0.0",
                Location = file.FullName,
                ObjectInstance = entity
            };
            entity.Envelope = envelope;
            return Task.FromResult<IIndexedTestRecord>(entity);
        }
    }

    [StorageScope(VersionTag = "1.0.0")]
    public interface ITestRecord : IStoredContract<ITestRecord>
    {
        string Id { get; set; }
    }

    [StorageScope(VersionTag = "1.0.0")]
    [SearchIndex]
    public interface IIndexedTestRecord : IStoredContract<IIndexedTestRecord>
    {
        [SearchIndex(SearchIndexMode.Exact)]
        string Id { get; set; }
    }

    private sealed class IndexedTestRecord : IIndexedTestRecord
    {
        public string Id { get; set; } = string.Empty;
        public IObjectEnvelope<IIndexedTestRecord>? Envelope { get; set; }
    }
}
