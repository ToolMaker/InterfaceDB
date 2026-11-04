// Regression copied from experiments/Atlas.RevisionTests/AtlasCoreProviderTests.cs; only provider configuration is adapted for the promoted hybrid.
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Storage.SegmentCore.ObjectLog;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    public async Task BatchedRead_MetadataOnly_UsesFacadeCoalescingAndMaterializesEachRequestedKeyOnce()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        string[] keys;
        await using (var writer = BuildProvider(config, options))
        {
            var saved = await writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteManyAsync(CreateRecords(12));
            keys = saved.Select(record => record.Envelope!.EnvelopeCacheKey).ToArray();
        }

        var serializer = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, options, serializer: serializer);
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        Assert.IsInstanceOfType<IBatchedEnvelopeRepository<IAtlasProviderRecord>>(envelopes);
        var batched = (IBatchedEnvelopeRepository<IAtlasProviderRecord>)envelopes;
        var result = await batched.GetPhysicalManyAsync(new[] { keys[8], "missing-0", keys[2], keys[8] });
        CollectionAssert.AreEqual(new[] { keys[8], keys[2], keys[8] }, result.Select(envelope => envelope.EnvelopeCacheKey).ToArray());
        Assert.AreEqual(2, serializer.DeserializeCount);
        Assert.AreSame(result[0], result[2]);
        Assert.AreEqual(8, result[0].ObjectInstance!.Score);
        Assert.AreEqual(2, result[1].ObjectInstance!.Score);
        _ = await batched.GetPhysicalManyAsync(new[] { keys[2] });
        Assert.AreEqual(3, serializer.DeserializeCount, "Metadata-only caching must not retain the previously materialized entity.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => batched.GetPhysicalManyAsync(new[] { keys[2] }, cancelled.Token).AsTask());
        Assert.AreEqual(3, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task BatchedRead_WeakCache_PinsLiveHitsAndDiskMissesOnlyForReturnedBatch()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.WeakObject,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();
        string[] keys;
        await using (var writer = BuildProvider(config, options))
        {
            var records = await writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteManyAsync(CreateRecords(3));
            keys = records.Select(record => record.Envelope!.EnvelopeCacheKey).ToArray();
        }

        var serializer = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, options, serializer: serializer);
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var batched = (IBatchedEnvelopeRepository<IAtlasProviderRecord>)envelopes;
        var hit = await envelopes.GetPhysicalAsync(keys[0]);
        var liveObject = hit!.ObjectInstance!;
        var result = await batched.GetPhysicalManyAsync(new[] { keys[0], keys[1], keys[0] });
        Assert.AreNotSame(liveObject, result[0].ObjectInstance);
        Assert.AreEqual(liveObject.Score, result[0].ObjectInstance!.Score);
        Assert.AreSame(result[0], result[2]);
        Assert.AreEqual(5, serializer.DeserializeCount, "Two cold reads create private cache copies; a hit creates its caller copy.");
        hit = null;
        liveObject = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsNotNull(result[0].ObjectInstance);
        Assert.IsNotNull(result[1].ObjectInstance);
        Assert.AreEqual(0, result[0].ObjectInstance!.Score);
        Assert.AreEqual(1, result[1].ObjectInstance!.Score);
        Assert.IsTrue(envelopes.TryGet(keys[0][..^2], out var cached));
        Assert.IsNotNull(cached[0].ObjectInstance, "Public envelope lookups return caller-owned objects.");
        Assert.AreNotSame(result[0].ObjectInstance, cached[0].ObjectInstance);
        GC.KeepAlive(result);
    }

    [TestMethod]
    public async Task HydrationUpgrade_ReadsOnlyMissingObjectsAndIsolatesExistingStrongCopies()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();
        string key;
        await using (var writer = BuildProvider(config, options))
        {
            var records = await writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>().WriteManyAsync(CreateRecords(8));
            key = records[0].Envelope!.EnvelopeCacheKey;
        }

        var serializer = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, options, serializer: serializer);
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        await ((IPersistentSearchIndexCatalogRepository)envelopes).InitializeForSearchIndexCatalogAsync();
        var hit = await envelopes.GetPhysicalAsync(key);
        await envelopes.InitializeAsync();
        Assert.AreEqual(9, serializer.DeserializeCount, "Hydration reads the seven missing objects; the earlier cold read also made a caller copy.");
        Assert.AreNotSame(hit!.ObjectInstance, (await envelopes.GetPhysicalAsync(key))!.ObjectInstance);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HydrationUpgrade_ConcurrentUpdateOrDeleteCannotBeOverwrittenByStalePayload(bool delete)
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            HydrateOnInitialize = true,
            RaiseNotifications = false,
            MaxConcurrency = 1
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();
        IAtlasProviderRecord saved;
        await using (var writer = BuildProvider(config, options))
        {
            saved = await writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteAsync(new AtlasProviderRecord { Id = "concurrent", Name = "Old", Score = 1 });
        }

        var serializer = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, options, serializer: serializer);
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        await ((IPersistentSearchIndexCatalogRepository)envelopes).InitializeForSearchIndexCatalogAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        serializer.BeforeDeserialize = () =>
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException("The concurrent hydration test did not release the serializer.");
                }
            }
        };
        var hydration = Task.Run(async () => await envelopes.InitializeAsync());
        try
        {
            Assert.IsTrue(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(20))));
            if (delete)
            {
                Assert.IsTrue(await envelopes.DeleteAsync(saved));
            }
            else
            {
                saved.Score = 99;
                saved = await envelopes.WriteAsync(saved);
            }
        }
        finally
        {
            release.Set();
        }

        await hydration.WaitAsync(TimeSpan.FromSeconds(20));
        if (delete)
        {
            Assert.IsFalse(envelopes.TryGet("concurrent", out _));
        }
        else
        {
            var current = await envelopes.GetPhysicalAsync(saved.Envelope!.EnvelopeCacheKey);
            Assert.AreNotSame(saved, current!.ObjectInstance);
            Assert.AreEqual(99, current.ObjectInstance!.Score);
        }
    }

    [TestMethod]
    public async Task MetadataRepository_FirstInsertAndLocatedUpdatePersistMetadataAcrossReopen()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        string persistedKey;

        await using (var provider = BuildProvider(config, options))
        {
            var metadataRepository = provider
                .GetRequiredService<IEnvelopeMetadataRepository<IAtlasProviderRecord>>();
            Assert.AreEqual(StorageDurabilityMode.Durable, metadataRepository.CommitDurability);
            Assert.AreSame(metadataRepository, provider.GetRequiredService<IRepository<IAtlasProviderRecord>>());

            var saved = await metadataRepository.SaveWithExtensionMetadataAsync(
                new AtlasProviderRecord { Id = "metadata", Name = "First", Score = 1 },
                existingEnvelopeCacheKey: null,
                new byte[] { 1, 2, 3, 4 });
            persistedKey = saved.Envelope?.EnvelopeCacheKey
                ?? throw new AssertFailedException("The saved envelope requires a provider key.");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, saved.Envelope.ExtensionMetadata);

            var oversizedCandidate = new AtlasProviderRecord { Id = "oversized", Name = "Too large", Score = 0 };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                metadataRepository.SaveWithExtensionMetadataAsync(
                    oversizedCandidate,
                    existingEnvelopeCacheKey: null,
                    new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]).AsTask());
            Assert.IsNull(oversizedCandidate.Envelope);

            var failedCandidate = new AtlasProviderRecord { Id = "metadata", Name = "Failed", Score = 2 };
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    metadataRepository.SaveWithExtensionMetadataAsync(
                        failedCandidate,
                        persistedKey,
                        new byte[] { 9, 9, 9 }, new EnvelopeWriteCondition(saved.Envelope!.RevisionTimestampUtcMicroseconds),
                        canceled.Token).AsTask());
            }

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, saved.Envelope.ExtensionMetadata);
            var unchanged = await metadataRepository.QueryFirstAsync(record => record.Id == "metadata");
            Assert.IsNotNull(unchanged);
            Assert.AreEqual("First", unchanged.Name);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, unchanged.Envelope?.ExtensionMetadata);

            var updated = await metadataRepository.SaveWithExtensionMetadataAsync(
                new AtlasProviderRecord { Id = "metadata", Name = "Updated", Score = 1 },
                persistedKey,
                new byte[] { 5, 6, 7 }, new EnvelopeWriteCondition(saved.Envelope!.RevisionTimestampUtcMicroseconds));
            Assert.AreEqual(persistedKey, updated.Envelope?.EnvelopeCacheKey);
            var integrity = await provider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .VerifyIntegrityAsync();
            Assert.AreEqual(1, integrity.LiveEntriesChecked);
        }

        await using var reopenedProvider = BuildProvider(config, options);
        var reopened = reopenedProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await reopened.InitializeAsync();
        var loaded = await reopened.QueryFirstAsync(record => record.Id == "metadata");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("Updated", loaded.Name);
        Assert.AreEqual(persistedKey, loaded.Envelope?.EnvelopeCacheKey);
        CollectionAssert.AreEqual(new byte[] { 5, 6, 7 }, loaded.Envelope?.ExtensionMetadata);
    }

    [TestMethod]
    public async Task Registration_MapsHardwarePolicyWithoutChangingRepositoryAbstractions()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
        {
            MaxReadConcurrency = 6
        };
        var config = new ProviderConfig(root.Path, policy);
        var services = new ServiceCollection();
        services.AddAtlasCoreRepository<IAtlasProviderRecord>(
            config,
            storagePolicy: policy,
            storageIdentityFactory: new ProviderIdentityFactory());

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AtlasCoreRepositoryOptions>();
        var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();

        Assert.AreEqual(StorageHardwareProfile.WindowsNvme, options.HardwareProfile);
        Assert.AreEqual(AtlasCoreDurabilityStrategy.WriteThrough, options.StoreOptions.DurabilityStrategy);
        Assert.AreEqual(8, options.StoreOptions.MaximumConcurrentPartitionWrites);
        Assert.AreEqual(6, options.StoreOptions.MaximumParallelReads);
        Assert.AreSame(policy, maintenance.StoragePolicy);
        Assert.AreSame(options, maintenance.Options);
        Assert.AreEqual(StorageHardwareProfile.WindowsNvme, maintenance.Status.HardwareProfile);
        Assert.IsInstanceOfType(
            provider.GetRequiredService<IRepository<IAtlasProviderRecord>>(),
            typeof(IRepository<IAtlasProviderRecord>));
    }

    [TestMethod]
    public async Task Repository_RoundTripIndexesDeleteCheckpointAndReopen_PreservesObjects()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        string deletedKey;

        await using (var provider = BuildProvider(config, options))
        {
            var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
            await repository.InitializeAsync();
            var saved = await repository.SaveManyAsync(
                Enumerable.Range(0, 100).Select(index => (IAtlasProviderRecord)new AtlasProviderRecord
                {
                    Id = $"record-{index:D3}",
                    Name = index % 2 == 0 ? $"Alpha {index}" : $"Beta {index}",
                    Score = index
                }));

            Assert.HasCount(100, saved);
            saved[0].Name = "Alpha updated";
            _ = await repository.SaveAsync(saved[0]);
            var match = await repository.SearchFirstAsync(
                item => item.Name.StartsWith("Alpha") && item.Score >= 90);
            Assert.IsNotNull(match);
            Assert.IsGreaterThanOrEqualTo(90, match.Score);

            deletedKey = saved[10].Envelope!.EnvelopeCacheKey;
            Assert.IsTrue(await repository.DeleteAsync(saved[10]));

            var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
            var checkpoint = await maintenance.CheckpointAsync();
            Assert.AreEqual(1, checkpoint.SegmentGroupsCheckpointed, "The common key prefix lazily owns one group, not the maximum partition capacity.");
            var integrity = await maintenance.VerifyIntegrityAsync();
            Assert.IsTrue(integrity.IsValid, string.Join(Environment.NewLine, integrity.Failures));
            Assert.AreEqual(99, integrity.LiveEntriesChecked);
        }

        await using var reopenedProvider = BuildProvider(config, options);
        var reopened = reopenedProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await reopened.InitializeAsync();
        var records = new List<IAtlasProviderRecord>();
        await foreach (var record in reopened.QueryManyAsync(static _ => true))
        {
            records.Add(record);
        }

        Assert.HasCount(99, records);
        Assert.IsFalse(records.Any(record => record.Envelope?.EnvelopeCacheKey == deletedKey));
        Assert.AreEqual("Alpha updated", records.Single(record => record.Id == "record-000").Name);
        Assert.AreEqual(ContractRepositoryType.AtlasCore, reopened.RepositoryType);
    }

    [TestMethod]
    public async Task MultipleCardinality_ReopenContinuesPhysicalSuffixAllocation()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();

        await using (var provider = BuildProvider(config, options, new SharedIdentityFactory()))
        {
            var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
            var saved = await repository.SaveManyAsync(
            [
                new AtlasProviderRecord { Id = "one", Name = "One", Score = 1 },
                new AtlasProviderRecord { Id = "two", Name = "Two", Score = 2 }
            ]);
            CollectionAssert.AreEquivalent(
                new[] { "shared-0", "shared-1" },
                saved.Select(static item => item.Envelope!.EnvelopeCacheKey).ToArray());
        }

        await using var reopenedProvider = BuildProvider(config, options, new SharedIdentityFactory());
        var reopened = reopenedProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        var savedAfterReopen = await reopened.SaveAsync(
            new AtlasProviderRecord { Id = "three", Name = "Three", Score = 3 });

        Assert.AreEqual("shared-2", savedAfterReopen.Envelope!.EnvelopeCacheKey);
    }

    [TestMethod]
    public async Task Repository_ConcurrentAndRepeatedInitialization_ActivatesAndRebuildsIndexesOnce()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var envelopes = writerProvider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
            _ = await envelopes.WriteAsync(new AtlasProviderRecord
            {
                Id = "record-001",
                Name = "Alpha",
                Score = 1
            });
            _ = await writerProvider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .CheckpointAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var repository = readerProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        var envelopeRepository = readerProvider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();

        await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => repository.InitializeAsync().AsTask()));
        await repository.InitializeAsync();
        await envelopeRepository.InitializeAsync();

        Assert.AreEqual(
            1,
            serializer.DeserializeCount,
            "Concurrent repository initialization must materialize the stored record for index rebuild only once.");
        Assert.IsNotNull(await repository.SearchFirstAsync(record => record.Name == "Alpha"));
        Assert.IsTrue(
            readerProvider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .Status.IsInitialized);
    }

    [TestMethod]
    public async Task EnvelopeRepository_CatalogInitializationCanUpgradeToRequestedHydration()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            _ = await writerProvider
                .GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteManyAsync(CreateRecords(12));
            _ = await writerProvider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .CheckpointAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var envelopes = readerProvider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var catalog = (IPersistentSearchIndexCatalogRepository)envelopes;

        await catalog.InitializeForSearchIndexCatalogAsync();
        Assert.AreEqual(0, serializer.DeserializeCount);

        await envelopes.InitializeAsync();
        Assert.AreEqual(12, serializer.DeserializeCount);

        await envelopes.InitializeAsync();
        Assert.AreEqual(12, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_IndexedMetadataStartup_DeserializesEachPayloadOnlyOnce()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            _ = await writerProvider
                .GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteManyAsync(CreateRecords(36));
            _ = await writerProvider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .CheckpointAsync();
        }

        var firstSerializer = new CountingObjectSerializer();
        await using (var firstReaderProvider = BuildProvider(config, options, serializer: firstSerializer))
        {
            await firstReaderProvider
                .GetRequiredService<IRepository<IAtlasProviderRecord>>()
                .InitializeAsync();
            Assert.AreEqual(36, firstSerializer.DeserializeCount);
        }

        var secondSerializer = new CountingObjectSerializer();
        await using var secondReaderProvider = BuildProvider(config, options, serializer: secondSerializer);
        await secondReaderProvider
            .GetRequiredService<IRepository<IAtlasProviderRecord>>()
            .InitializeAsync();
        Assert.AreEqual(0, secondSerializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_WarmCatalogQuery_DeserializesOnlyMatchingPayloads()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            _ = await writerProvider
                .GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>()
                .WriteManyAsync(CreateRecords(42));
            _ = await writerProvider
                .GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>()
                .CheckpointAsync();
        }

        await using (var catalogProvider = BuildProvider(config, options))
        {
            await catalogProvider
                .GetRequiredService<IRepository<IAtlasProviderRecord>>()
                .InitializeAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var repository = readerProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repository.InitializeAsync();
        Assert.AreEqual(0, serializer.DeserializeCount);

        var matches = new List<IAtlasProviderRecord>();
        await foreach (var record in repository.SearchManyAsync(record => record.Score >= 36))
        {
            matches.Add(record);
        }

        Assert.HasCount(6, matches);
        Assert.AreEqual(6, serializer.DeserializeCount);
    }

    [TestMethod]
    [DataRow("repository-maintenance-envelope")]
    [DataRow("maintenance-envelope-repository")]
    [DataRow("envelope-repository-maintenance")]
    public async Task Repository_IndexedCatalog_PersistsWritesMadeAfterEmptyInitialization(
        string resolutionOrder)
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var services = ResolveProviderServices(writerProvider, resolutionOrder);
            await services.Repository.InitializeAsync();
            _ = await services.Repository.SaveManyAsync(CreateRecords(36));

            Assert.AreSame<object>(services.Maintenance, services.Envelope);
            Assert.IsFalse(services.Maintenance is IDisposable or IAsyncDisposable);
            Assert.IsFalse(services.Envelope is IDisposable or IAsyncDisposable);
            Assert.IsTrue(
                writerProvider.GetRequiredKeyedService<IEnvelopeRepository<IAtlasProviderRecord>>(
                    config.EnvelopeDICacheKey) is IAsyncDisposable);
            _ = await services.Maintenance.CheckpointAsync();
        }

        var catalog = Directory.EnumerateFiles(
            Path.Combine(root.Path, options.DirectoryName, "1.0.0", "search-indexes"),
            "*.idbsidx").Single();
        Assert.IsGreaterThan(128L, new FileInfo(catalog).Length);

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        await readerProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>().InitializeAsync();

        Assert.AreEqual(0, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task EnvelopeRepository_ConcurrentDisposeCheckpointFailure_ReleasesStoreOwnership()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var durableFileSystem = new CheckpointFailingDurableFileSystem();
        var defaults = ProviderOptions();
        var options = defaults with
        {
            StoreOptions = defaults.StoreOptions with
            {
                DurableFileSystem = durableFileSystem
            }
        };
        var provider = BuildProvider(config, options);
        var repository = provider.GetRequiredKeyedService<IEnvelopeRepository<IAtlasProviderRecord>>(
            config.EnvelopeDICacheKey);
        _ = await repository.WriteAsync(new AtlasProviderRecord
        {
            Id = "record-001",
            Name = "Alpha",
            Score = 1
        });

        durableFileSystem.FailCheckpointPublication = true;
        var firstDisposal = ((IAsyncDisposable)repository).DisposeAsync().AsTask();
        var concurrentDisposal = ((IAsyncDisposable)repository).DisposeAsync().AsTask();

        Assert.AreSame(firstDisposal, concurrentDisposal);
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => firstDisposal);
        Assert.IsTrue(failure.InnerExceptions.Any(exception => exception is IOException));
        _ = await Assert.ThrowsExactlyAsync<AggregateException>(() => provider.DisposeAsync().AsTask());

        durableFileSystem.FailCheckpointPublication = false;
        await using var reopenedProvider = BuildProvider(config, options);
        var reopened = reopenedProvider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await reopened.InitializeAsync();
        var record = await reopened.SearchFirstAsync(item => item.Id == "record-001");

        Assert.IsNotNull(record);
        Assert.AreEqual("Alpha", record.Name);
    }

    [TestMethod]
    public async Task SearchIndexStorageIdentity_IsStableAcrossReopenAndChangesOnUpdate()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        byte[] firstFingerprint;

        await using (var provider = BuildProvider(config, options))
        {
            var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
            var saved = await repository.SaveAsync(
                new AtlasProviderRecord { Id = "identity", Name = "Original", Score = 1 });
            var catalog = (IPersistentSearchIndexCatalogRepository)provider
                .GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
            var first = await catalog.GetSearchIndexStorageIdentityAsync();
            Assert.AreEqual(1, first.EntryCount);
            firstFingerprint = first.Fingerprint.ToArray();

            saved.Name = "Updated";
            _ = await repository.SaveAsync(saved);
            var updated = await catalog.GetSearchIndexStorageIdentityAsync();
            CollectionAssert.AreNotEqual(firstFingerprint, updated.Fingerprint.ToArray());
            firstFingerprint = updated.Fingerprint.ToArray();
        }

        await using var reopenedProvider = BuildProvider(config, options);
        var reopenedCatalog = (IPersistentSearchIndexCatalogRepository)reopenedProvider
            .GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var reopenedIdentity = await reopenedCatalog.GetSearchIndexStorageIdentityAsync();
        CollectionAssert.AreEqual(firstFingerprint, reopenedIdentity.Fingerprint.ToArray());
    }

    [TestMethod]
    public async Task ProviderNativeMigration_IsTransactionalResumableAndKeepsSource()
    {
        using var root = new ProviderTemporaryDirectory();
        var options = ProviderOptions();
        var sourceConfig = new ProviderConfig(root.Path, activeVersion: "1.0.0");
        var sourceRoot = Path.Combine(root.Path, options.DirectoryName, "1.0.0");
        var serializer = new BinaryObjectSerializer<IAtlasProviderRecord>();
        await using (var sourceStore = await AtlasCoreStore.OpenAsync(sourceRoot, options.StoreOptions))
        {
            var legacy = new AtlasProviderRecordMigration
            {
                Id = "legacy-001",
                Name = "Legacy",
                Score = 7
            };
            var envelope = new FileObjectEnvelope<IAtlasProviderRecord>(sourceConfig)
            {
                EnvelopeCacheKey = "legacy-001-0",
                VersionTag = "1.0.0",
                Location = "legacy-001-0",
                ExtensionMetadata = new byte[] { 8, 9, 10 },
                ObjectInstance = legacy
            };
            await sourceStore.SaveAsync(envelope.EnvelopeCacheKey, serializer.Serialize(envelope));
            _ = await sourceStore.CheckpointAsync();
        }

        var targetConfig = new ProviderConfig(root.Path, activeVersion: "2.0.0");
        var services = new ServiceCollection();
        services.AddSingleton<IMigration, AtlasProviderRecordMigration>();
        services.AddAtlasCoreRepository<IAtlasProviderRecord>(
            targetConfig,
            options,
            targetConfig.StoragePolicy,
            new ProviderIdentityFactory());
        await using var provider = services.BuildServiceProvider();
        var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();

        var first = await maintenance.MigrateFromAsync(sourceRoot, "1.0.0");
        Assert.AreEqual(1, first.Examined);
        Assert.AreEqual(1, first.Migrated);
        Assert.AreEqual(0, first.AlreadyPresent);
        Assert.IsEmpty(first.Failures);
        Assert.IsTrue(first.Checkpoint.HasValue);

        var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repository.InitializeAsync();
        var migrated = await repository.SearchFirstAsync(record => record.Id == "legacy-001");
        Assert.IsNotNull(migrated);
        Assert.AreEqual("Legacy migrated", migrated.Name);
        Assert.AreEqual(107, migrated.Score);
        CollectionAssert.AreEqual(new byte[] { 8, 9, 10 }, migrated.Envelope?.ExtensionMetadata);

        var second = await maintenance.MigrateFromAsync(sourceRoot, "1.0.0");
        Assert.AreEqual(0, second.Migrated);
        Assert.AreEqual(1, second.AlreadyPresent);
        Assert.IsTrue(Directory.Exists(sourceRoot));
    }

    [TestMethod]
    public void ProviderNativeMigration_RejectsOversizedExtensionMetadata()
    {
        var engine = new AtlasCoreMigrationEngine<IAtlasProviderRecord>(
            "2.0.0",
            new EncryptionNone(),
            [new AtlasProviderRecordMigration()]);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            engine.Migrate(
                "legacy-oversized",
                "1.0.0",
                CreateOversizedIdb5MigrationPayload("legacy-oversized")));

        StringAssert.Contains(exception.Message, EnvelopeExtensionMetadataLimits.MaximumByteLength.ToString());
    }

    private static byte[] CreateOversizedIdb5MigrationPayload(string cacheKey)
    {
        using var stream = new MemoryStream();
        stream.Write("IDB5"u8);
        MessagePackSerializer.Serialize(stream, "1.0.0");
        MessagePackSerializer.Serialize(stream, cacheKey);
        MessagePackSerializer.Serialize(
            stream,
            typeof(AtlasProviderRecordMigration).AssemblyQualifiedName!);
        MessagePackSerializer.Serialize(stream, false);
        stream.WriteByte(MessagePackCode.MinFixMap);
        MessagePackSerializer.Serialize(
            stream,
            new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]);
        stream.WriteByte(MessagePackCode.Nil);
        return stream.ToArray();
    }

    private static ServiceProvider BuildProvider(
        ProviderConfig config,
        AtlasCoreRepositoryOptions options,
        IEnvelopeStorageIdentityFactory<IAtlasProviderRecord>? identityFactory = null,
        IObjectSerializer<IAtlasProviderRecord>? serializer = null)
    {
        var services = new ServiceCollection();
        if (serializer is not null)
        {
            services.AddKeyedSingleton(config.EnvelopeDICacheKey, serializer);
        }

        services.AddAtlasCoreRepository<IAtlasProviderRecord>(
            config,
            options,
            config.StoragePolicy,
            identityFactory ?? new ProviderIdentityFactory());
        return services.BuildServiceProvider();
    }

    private static (
        IRepository<IAtlasProviderRecord> Repository,
        IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord> Maintenance,
        IEnvelopeRepository<IAtlasProviderRecord> Envelope) ResolveProviderServices(
            ServiceProvider provider,
            string resolutionOrder)
    {
        switch (resolutionOrder)
        {
            case "repository-maintenance-envelope":
                {
                    var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
                    var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
                    var envelope = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
                    return (repository, maintenance, envelope);
                }
            case "maintenance-envelope-repository":
                {
                    var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
                    var envelope = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
                    var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
                    return (repository, maintenance, envelope);
                }
            case "envelope-repository-maintenance":
                {
                    var envelope = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
                    var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
                    var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
                    return (repository, maintenance, envelope);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(resolutionOrder));
        }
    }

    private static IEnumerable<IAtlasProviderRecord> CreateRecords(int count) =>
        Enumerable.Range(0, count).Select(index => (IAtlasProviderRecord)new AtlasProviderRecord
        {
            Id = $"record-{index:D6}",
            Name = $"Record {index}",
            Score = index
        });

    private static AtlasCoreRepositoryOptions ProviderOptions() => new()
    {
        HardwareProfile = StorageHardwareProfile.Custom,
        EnableBackgroundMaintenance = false,
        CheckpointOnDispose = true,
        StoreOptions = new AtlasCoreOptions
        {
            MaximumConcurrentPartitionWrites = 4,
            MaximumBatchRecords = 64,
            MaximumSegmentBytes = 16 * 1024,
            DurabilityStrategy = AtlasCoreDurabilityStrategy.ExplicitFlush,
            CheckpointGenerationsToKeep = 2
        },
        CompactionOptions = new AtlasCoreCompactionOptions
        {
            RequireFreeSpaceAdmission = false
        }
    };

    private sealed class ProviderConfig(
        string root,
        StoragePolicy? storagePolicy = null,
        string activeVersion = "1.0.0") : IDbContractConfigDto<IAtlasProviderRecord>
    {
        public string EnvelopeDICacheKey => "atlas-provider-test";
        public string ContractName => "AtlasProviderRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => activeVersion;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.AtlasCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy { get; } = storagePolicy
            ?? Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
            {
                ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
                RaiseNotifications = true,
                HydrateOnInitialize = false
            };
    }

    private sealed class ProviderIdentityFactory : IEnvelopeStorageIdentityFactory<IAtlasProviderRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

        public void InitEnvelope(IObjectEnvelope<IAtlasProviderRecord> envelope)
        {
            envelope.EnvelopeCacheKey = envelope.ObjectInstance?.Id
                ?? throw new InvalidOperationException("Record id is required.");
            envelope.Location ??= envelope.EnvelopeCacheKey;
        }
    }

    private sealed class SharedIdentityFactory : IEnvelopeStorageIdentityFactory<IAtlasProviderRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

        public void InitEnvelope(IObjectEnvelope<IAtlasProviderRecord> envelope)
        {
            envelope.EnvelopeCacheKey = "shared";
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }

    private sealed class CountingObjectSerializer : IRevisionObjectSerializer<IAtlasProviderRecord>
    {
        private readonly BinaryObjectSerializer<IAtlasProviderRecord> _inner = new();
        private int _readRevisionCount;
        public int ReadRevisionCount => Volatile.Read(ref _readRevisionCount);
        public long ReadRevision(ReadOnlyMemory<byte> payload)
        {
            Interlocked.Increment(ref _readRevisionCount);
            return _inner.ReadRevision(payload);
        }
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => _inner.ReadRevisionAsync(source, cancellationToken);
        private int _deserializeCount;

        public int DeserializeCount => Volatile.Read(ref _deserializeCount);

        public Action? BeforeDeserialize { get; set; }

        public byte[] Serialize(IObjectEnvelope<IAtlasProviderRecord> envalope) => _inner.Serialize(envalope);

        public IObjectEnvelope<IAtlasProviderRecord> Deserialize(byte[] data)
        {
            BeforeDeserialize?.Invoke();
            Interlocked.Increment(ref _deserializeCount);
            return _inner.Deserialize(data);
        }

        public ValueTask SerializeAsync(
            IObjectEnvelope<IAtlasProviderRecord> envalope,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            _inner.SerializeAsync(envalope, destination, cancellationToken);

        public async ValueTask<IObjectEnvelope<IAtlasProviderRecord>> DeserializeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deserializeCount);
            return await _inner.DeserializeAsync(source, cancellationToken);
        }
    }

    private sealed class CheckpointFailingDurableFileSystem : IDurableFileSystem
    {
        public bool FailCheckpointPublication { get; set; }

        public string DirectoryFlushStrategy => "checkpoint-failure test wrapper";

        public void FlushFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
            PlatformDurableFileSystem.Instance.FlushFile(handle);

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath)
        {
            if (FailCheckpointPublication
                && string.Equals(Path.GetExtension(finalPath), ".idbagi", StringComparison.Ordinal))
            {
                throw new IOException("Injected dispose-time checkpoint publication failure.");
            }

            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);
        }

        public void ReplaceFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
    }

    private sealed class ProviderTemporaryDirectory : IDisposable
    {
        public ProviderTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "interface-db-atlas-provider-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IAtlasProviderRecord : IStoredContract<IAtlasProviderRecord>
{
    string Id { get; set; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix)]
    string Name { get; set; }

    [SearchIndex]
    int Score { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class AtlasProviderRecord : IAtlasProviderRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<IAtlasProviderRecord>? Envelope { get; private set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class AtlasProviderRecordMigration : IAtlasProviderRecord, IMigration
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<IAtlasProviderRecord>? Envelope { get; private set; }

    [IgnoreMember]
    public string SourceVersion => "1.0.0";

    [IgnoreMember]
    public string TargetVersion => "2.0.0";

    public bool Up()
    {
        Name += " migrated";
        Score += 100;
        return true;
    }

    public bool Down()
    {
        Name = Name.EndsWith(" migrated", StringComparison.Ordinal)
            ? Name[..^" migrated".Length]
            : Name;
        Score -= 100;
        return true;
    }
}
