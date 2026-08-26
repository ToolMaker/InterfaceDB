using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCoreStoreTests
{
    [TestMethod]
    public async Task SaveAndGet_MaterializesPayloadByDirectLocator()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        var payload = Payload(1, 2048);

        await store.SaveAsync("alpha", payload);

        CollectionAssert.AreEqual(payload, await store.GetAsync("alpha"));
        Assert.IsNull(await store.GetAsync("missing"));
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public async Task SaveAndGet_WhenSynchronousPointReadsAreDisabled_UsesAsyncReadPath()
    {
        using var root = new TemporaryDirectory();
        var options = Options(stripes: 8, segmentBytes: 1024 * 1024) with
        {
            ReadOptions = new SegmentCoreReadOptions
            {
                SynchronousPointReadMaximumBytes = 0
            }
        };
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, options);
        var payload = Payload(17, 128 * 1024);

        await store.SaveAsync("large-async", payload);

        CollectionAssert.AreEqual(payload, await store.GetAsync("large-async"));
    }

    [TestMethod]
    public async Task CheckpointReopen_PagedPointReadMatchesUnicodeKeyWithoutChangingKeySemantics()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
            {
                Mode = SegmentCorePrimaryIndexMode.Paged
            }
        };
        const string key = "café-東京-😀";
        var payload = Payload(23, 512);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync(key, payload);
            _ = await store.CheckpointAsync();
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual(payload, await reopened.GetAsync(key));
        Assert.IsNull(await reopened.GetAsync("cafe-東京-😀"));
    }

    [TestMethod]
    public async Task SaveExistingKey_PublishesNewestDurableAddress()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);

        await store.SaveAsync("alpha", Payload(1, 128));
        await store.SaveAsync("alpha", Payload(2, 256));

        CollectionAssert.AreEqual(Payload(2, 256), await store.GetAsync("alpha"));
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public async Task ReadAll_UsesPhysicalOrderAndMaterializesEveryLivePayload()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        for (var index = 0; index < 500; index++)
        {
            await store.SaveAsync($"key-{index:D4}", Payload(index, 192));
        }

        await store.SaveAsync("key-0042", Payload(9001, 256));
        Assert.IsTrue(await store.DeleteAsync("key-0043"));

        var records = await store.ReadAllAsync();
        Assert.HasCount(499, records);
        var byKey = records.ToDictionary(static record => record.Key, StringComparer.Ordinal);
        CollectionAssert.AreEqual(Payload(9001, 256), byKey["key-0042"].Payload);
        Assert.IsFalse(byKey.ContainsKey("key-0043"));
    }

    [TestMethod]
    public async Task ConcurrentSameKeySaves_NeverPublishAnOlderLsnOverANewerOne()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        var writes = Enumerable.Range(1, 128)
            .Select(version => store.SaveAsync("shared", BitConverter.GetBytes(version)).AsTask())
            .ToArray();

        await Task.WhenAll(writes);

        var payload = await store.GetAsync("shared");
        Assert.IsNotNull(payload);
        Assert.AreEqual(128, BitConverter.ToInt32(payload));
    }

    [TestMethod]
    public async Task Delete_WritesTombstoneAndRemovesPrimaryLocator()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        await store.SaveAsync("alpha", Payload(1, 64));

        Assert.IsTrue(await store.DeleteAsync("alpha"));
        Assert.IsFalse(await store.DeleteAsync("alpha"));
        Assert.IsNull(await store.GetAsync("alpha"));
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public async Task CheckpointReopen_ReplaysOnlyNewerPutUpdateAndDeleteFrames()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("alpha", Payload(1, 100));
            await store.SaveAsync("beta", Payload(2, 100));
            var checkpoint = await store.CheckpointAsync();
            Assert.AreEqual(2, checkpoint.Entries);

            await store.SaveAsync("alpha", Payload(3, 200));
            Assert.IsTrue(await store.DeleteAsync("beta"));
            await store.SaveAsync("gamma", Payload(4, 300));
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(2, reopened.Count);
        Assert.AreEqual(3, reopened.Statistics.TailRecordsReplayed);
        CollectionAssert.AreEqual(Payload(3, 200), await reopened.GetAsync("alpha"));
        Assert.IsNull(await reopened.GetAsync("beta"));
        CollectionAssert.AreEqual(Payload(4, 300), await reopened.GetAsync("gamma"));
    }

    [TestMethod]
    public async Task ReopenWithoutCheckpoint_FallsBackToFullRecovery()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("alpha", Payload(1, 128));
            await store.SaveAsync("beta", Payload(2, 128));
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(2, reopened.Count);
        Assert.AreEqual(2L, reopened.Statistics.FullRecoveryFramesScanned);
        CollectionAssert.AreEqual(Payload(1, 128), await reopened.GetAsync("alpha"));
        CollectionAssert.AreEqual(Payload(2, 128), await reopened.GetAsync("beta"));
    }

    [TestMethod]
    public async Task MultipleCheckpoints_AdvanceGenerationAndBoundTailReplay()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("alpha", Payload(1, 128));
            var first = await store.CheckpointAsync();
            await store.SaveAsync("beta", Payload(2, 128));
            var second = await store.CheckpointAsync();
            Assert.AreEqual(first.Generation + 1, second.Generation);
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(2, reopened.Count);
        Assert.AreEqual(0, reopened.Statistics.TailRecordsReplayed);
        Assert.AreEqual(2L, reopened.Statistics.CheckpointGeneration);
    }

    [TestMethod]
    public async Task CheckpointWithoutNewDurableState_ReusesPublishedGeneration()
    {
        using var root = new TemporaryDirectory();
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, WalOptions(stripes: 4));
        await store.SaveAsync("alpha", Payload(1, 128));
        var first = await store.CheckpointAsync();
        var checkpointDirectory = Path.Combine(root.Path, "checkpoints");
        var filesBefore = Directory.EnumerateFiles(checkpointDirectory, "checkpoint-*.*").ToArray();
        var walBytesBefore = new FileInfo(WalPath(root.Path)).Length;

        var second = await store.CheckpointAsync();

        Assert.AreEqual(first.Generation, second.Generation);
        Assert.AreEqual(first.Entries, second.Entries);
        Assert.AreEqual(first.Bytes, second.Bytes);
        Assert.AreEqual(walBytesBefore, new FileInfo(WalPath(root.Path)).Length);
        CollectionAssert.AreEquivalent(
            filesBefore,
            Directory.EnumerateFiles(checkpointDirectory, "checkpoint-*.*").ToArray());
    }

    [TestMethod]
    public async Task CheckpointRetention_KeepsOnlyConfiguredPublishedGenerations()
    {
        using var root = new TemporaryDirectory();
        var options = Options(stripes: 4, segmentBytes: 1024 * 1024) with
        {
            CheckpointGenerationsToKeep = 2
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var generation = 1; generation <= 4; generation++)
            {
                await store.SaveAsync($"key-{generation}", Payload(generation, 128));
                Assert.AreEqual(generation, (await store.CheckpointAsync()).Generation);
            }
        }

        var checkpoints = Directory.EnumerateFiles(
                Path.Combine(root.Path, "checkpoints"),
                "checkpoint-*.*")
            .Where(static path => path.EndsWith(".idbidx", StringComparison.Ordinal)
                                  || path.EndsWith(".idbdlt", StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.HasCount(3, checkpoints);
        StringAssert.Contains(Path.GetFileName(checkpoints[0]), "00000000000000000002");
        StringAssert.Contains(Path.GetFileName(checkpoints[1]), "00000000000000000003");
        StringAssert.Contains(Path.GetFileName(checkpoints[2]), "00000000000000000004");
        StringAssert.EndsWith(Path.GetFileName(checkpoints[1]), ".idbdlt");

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(4, reopened.Count);
        Assert.AreEqual(4L, reopened.Statistics.CheckpointGeneration);
        Assert.AreEqual(0, reopened.Statistics.TailRecordsReplayed);
    }

    [TestMethod]
    public async Task CancelledCheckpoint_RemovesTemporaryFileAndAllowsRetry()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        await store.SaveAsync("alpha", Payload(1, 128));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.CheckpointAsync(cancellation.Token).AsTask());

        var checkpointDirectory = Path.Combine(root.Path, "checkpoints");
        Assert.IsTrue(
            !Directory.Exists(checkpointDirectory)
            || !Directory.EnumerateFiles(
                checkpointDirectory,
                "*.creating-*",
                SearchOption.TopDirectoryOnly).Any());
        var retry = await store.CheckpointAsync();
        Assert.AreEqual(1L, retry.Generation);
    }

    [TestMethod]
    public async Task CorruptNewestCheckpoint_FallsBackToPriorGenerationAndReplaysTail()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("alpha", Payload(1, 128));
            _ = await store.CheckpointAsync();
            await store.SaveAsync("beta", Payload(2, 128));
            _ = await store.CheckpointAsync();
        }

        var newest = Directory.EnumerateFiles(
                Path.Combine(root.Path, "checkpoints"),
                "checkpoint-*.idbidx")
            .OrderByDescending(static path => path, StringComparer.Ordinal)
            .First();
        using (var stream = new FileStream(newest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var lastByte = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(lastByte ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(2, reopened.Count);
        Assert.IsTrue(reopened.Statistics.TailRecordsReplayed >= 1);
        CollectionAssert.AreEqual(Payload(2, 128), await reopened.GetAsync("beta"));
        var replacement = await reopened.CheckpointAsync();
        Assert.AreEqual(3L, replacement.Generation);
    }

    [TestMethod]
    public async Task SegmentRotation_DirectReadsOldAndActiveSegmentsAfterCheckpointReopen()
    {
        using var root = new TemporaryDirectory();
        var options = Options(stripes: 4, segmentBytes: 4 * 1024);
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 80; index++)
            {
                var key = $"key-{index:D4}";
                var payload = Payload(index, 512);
                expected.Add(key, payload);
                await store.SaveAsync(key, payload);
            }

            _ = await store.CheckpointAsync();
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        foreach (var pair in expected)
        {
            CollectionAssert.AreEqual(pair.Value, await reopened.GetAsync(pair.Key));
        }

        Assert.IsTrue(Directory.EnumerateFiles(root.Path, "*.idbseg", SearchOption.AllDirectories).Count() > 4);
    }

    [TestMethod]
    public async Task ConcurrentUniqueSavesAndReads_RemainConsistent()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        var expected = Enumerable.Range(0, 512)
            .ToDictionary(index => $"key-{index:D5}", index => Payload(index, 1024), StringComparer.Ordinal);

        await Task.WhenAll(expected.Select(pair => store.SaveAsync(pair.Key, pair.Value).AsTask()));
        var reads = await Task.WhenAll(expected.Select(async pair =>
            (pair.Key, Payload: await store.GetAsync(pair.Key))));

        Assert.AreEqual(expected.Count, store.Count);
        foreach (var read in reads)
        {
            CollectionAssert.AreEqual(expected[read.Key], read.Payload);
        }
    }

    [TestMethod]
    public async Task CheckpointDuringConcurrentSaves_ReopenContainsEveryAcknowledgedMutation()
    {
        using var root = new TemporaryDirectory();
        var expected = Enumerable.Range(0, 512)
            .ToDictionary(index => $"key-{index:D5}", index => Payload(index, 256), StringComparer.Ordinal);
        await using (var store = await OpenAsync(root.Path))
        {
            var writes = expected.Select(async pair =>
            {
                await Task.Yield();
                await store.SaveAsync(pair.Key, pair.Value);
            }).ToArray();
            var checkpoint = store.CheckpointAsync().AsTask();
            await Task.WhenAll(writes.Append(checkpoint));
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(expected.Count, reopened.Count);
        foreach (var pair in expected)
        {
            CollectionAssert.AreEqual(pair.Value, await reopened.GetAsync(pair.Key));
        }
    }

    [TestMethod]
    public async Task ExplicitTransaction_IsInvisibleUntilCommit_AndReadsItsOwnWrites()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.SaveAsync("alpha", Payload(1, 128));
        await transaction.SaveAsync("beta", Payload(2, 128));

        Assert.IsNull(await store.GetAsync("alpha"));
        CollectionAssert.AreEqual(Payload(1, 128), await transaction.GetAsync("alpha"));

        await transaction.CommitAsync();

        CollectionAssert.AreEqual(Payload(1, 128), await store.GetAsync("alpha"));
        CollectionAssert.AreEqual(Payload(2, 128), await store.GetAsync("beta"));
        Assert.AreEqual(SegmentCoreTransactionState.Committed, transaction.State);
        Assert.AreEqual(1L, store.Statistics.CommittedTransactions);
    }

    [TestMethod]
    public async Task ExplicitTransaction_RollbackAndDisposePublishNothing()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.SaveAsync("rolled-back", Payload(1, 64));
            await transaction.RollbackAsync();
            Assert.AreEqual(SegmentCoreTransactionState.RolledBack, transaction.State);
        }

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await transaction.SaveAsync("disposed", Payload(2, 64));
        }

        Assert.IsNull(await store.GetAsync("rolled-back"));
        Assert.IsNull(await store.GetAsync("disposed"));
        Assert.AreEqual(2L, store.Statistics.RolledBackTransactions);
    }

    [TestMethod]
    public async Task ExplicitTransaction_ReopenRecoversWholeCommittedMutationSet()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("delete-me", Payload(9, 64));
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("alpha", Payload(1, 128));
            await transaction.SaveAsync("beta", Payload(2, 256));
            Assert.IsTrue(await transaction.DeleteAsync("delete-me"));
            await transaction.CommitAsync();
        }

        await using var reopened = await OpenAsync(root.Path);
        CollectionAssert.AreEqual(Payload(1, 128), await reopened.GetAsync("alpha"));
        CollectionAssert.AreEqual(Payload(2, 256), await reopened.GetAsync("beta"));
        Assert.IsNull(await reopened.GetAsync("delete-me"));
        Assert.AreEqual(2, reopened.Count);
    }

    [TestMethod]
    public async Task ExplicitTransaction_AfterCheckpointReplaysAsOneValidatedTailCommit()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await OpenAsync(root.Path))
        {
            await store.SaveAsync("before", Payload(0, 64));
            _ = await store.CheckpointAsync();
            await using var transaction = await store.BeginTransactionAsync();
            for (var index = 0; index < 24; index++)
            {
                await transaction.SaveAsync($"tx-{index:D2}", Payload(index, 96));
            }

            await transaction.CommitAsync();
        }

        await using var reopened = await OpenAsync(root.Path);
        Assert.AreEqual(25, reopened.Count);
        Assert.AreEqual(24, reopened.Statistics.TailRecordsReplayed);
        for (var index = 0; index < 24; index++)
        {
            CollectionAssert.AreEqual(Payload(index, 96), await reopened.GetAsync($"tx-{index:D2}"));
        }
    }

    [TestMethod]
    public async Task PreparedFramesWithoutCommitRecord_AreIgnoredOnRecovery()
    {
        using var root = new TemporaryDirectory();
        var options = Options(stripes: 4, segmentBytes: 1024 * 1024);
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, options.LogOptions))
        {
            _ = await writer.AppendTransactionalAsync(
                ObjectLogRecordKind.Put,
                "abandoned",
                Payload(1, 128),
                transactionId: 42);
        }

        await using var store = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.IsNull(await store.GetAsync("abandoned"));
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public async Task CommitJournalFlushFailure_FaultsScopeAndRequiresReopenForOutcome()
    {
        using var root = new TemporaryDirectory();
        var fileSystem = new FailingDurableFileSystem();
        var options = Options(stripes: 1, segmentBytes: 1024 * 1024) with
        {
            DurableFileSystem = fileSystem,
            LogOptions = Options(stripes: 1, segmentBytes: 1024 * 1024).LogOptions with
            {
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            }
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("uncertain-a", Payload(1, 128));
            await transaction.SaveAsync("uncertain-b", Payload(2, 128));
            fileSystem.FailOnFileFlush = fileSystem.FileFlushCount + 2;

            _ = await Assert.ThrowsExactlyAsync<IOException>(
                () => transaction.CommitAsync().AsTask());
            Assert.AreEqual(SegmentCoreTransactionState.Faulted, transaction.State);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.GetAsync("uncertain-a").AsTask());
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(
            root.Path,
            options with { DurableFileSystem = PlatformDurableFileSystem.Instance });
        var recoveredA = await reopened.GetAsync("uncertain-a");
        var recoveredB = await reopened.GetAsync("uncertain-b");
        Assert.AreEqual(recoveredA is null, recoveredB is null);
        if (recoveredA is not null)
        {
            CollectionAssert.AreEqual(Payload(1, 128), recoveredA);
            CollectionAssert.AreEqual(Payload(2, 128), recoveredB);
        }
    }

    [TestMethod]
    public async Task MutationWal_TransactionIsAtomicAndSurvivesReopen()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 8);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync("delete-me", Payload(9, 64));
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("alpha", Payload(1, 128));
            await transaction.SaveAsync("beta", Payload(2, 256));
            Assert.IsTrue(await transaction.DeleteAsync("delete-me"));
            Assert.IsNull(await store.GetAsync("alpha"));

            await transaction.CommitAsync();
            CollectionAssert.AreEqual(Payload(1, 128), await store.GetAsync("alpha"));
            Assert.IsNull(await store.GetAsync("delete-me"));
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual(Payload(1, 128), await reopened.GetAsync("alpha"));
        CollectionAssert.AreEqual(Payload(2, 256), await reopened.GetAsync("beta"));
        Assert.IsNull(await reopened.GetAsync("delete-me"));
        Assert.AreEqual(2, reopened.Count);
    }

    [TestMethod]
    public async Task MutationWal_RebuildsMissingDerivedFramesFromDurablePayload()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            for (var index = 0; index < 32; index++)
            {
                await transaction.SaveAsync($"rebuild-{index:D2}", Payload(index, 96));
            }

            await transaction.CommitAsync();
        }

        foreach (var segmentPath in Directory.EnumerateFiles(
                     Path.Combine(root.Path, "stripes"),
                     "segment-*.idbseg",
                     SearchOption.AllDirectories))
        {
            using var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(ObjectLogFormat.SegmentHeaderSize);
            stream.Flush(flushToDisk: true);
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(32, reopened.Count);
        for (var index = 0; index < 32; index++)
        {
            CollectionAssert.AreEqual(Payload(index, 96), await reopened.GetAsync($"rebuild-{index:D2}"));
        }
    }

    [TestMethod]
    public async Task MutationWal_CheckpointFlushesDerivedFramesAndReplaysLaterTransactions()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using (var first = await store.BeginTransactionAsync())
            {
                await first.SaveAsync("before-checkpoint", Payload(1, 64));
                await first.CommitAsync();
            }

            _ = await store.CheckpointAsync();
            await using var second = await store.BeginTransactionAsync();
            await second.SaveAsync("after-checkpoint", Payload(2, 64));
            await second.CommitAsync();
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual(Payload(1, 64), await reopened.GetAsync("before-checkpoint"));
        CollectionAssert.AreEqual(Payload(2, 64), await reopened.GetAsync("after-checkpoint"));
        Assert.AreEqual(1, reopened.Statistics.TailRecordsReplayed);
    }

    [TestMethod]
    public async Task MutationWal_CheckpointPublication_DoesNotBlockNewWrites()
    {
        using var root = new TemporaryDirectory();
        var fileSystem = new FailingDurableFileSystem();
        var options = WalOptions(stripes: 4) with { DurableFileSystem = fileSystem };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using (var transaction = await store.BeginTransactionAsync())
            {
                await transaction.SaveAsync("before", Payload(1, 128));
                await transaction.CommitAsync();
            }

            fileSystem.BlockCheckpointPublication();
            var checkpoint = store.CheckpointAsync().AsTask();
            await fileSystem.WaitForCheckpointPublicationAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var concurrentWrite = store.SaveAsync("during", Payload(2, 128)).AsTask();
            var completed = await Task.WhenAny(concurrentWrite, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.AreSame(concurrentWrite, completed, "Checkpoint file publication held the mutation gate.");
            await concurrentWrite;

            fileSystem.ReleaseCheckpointPublication();
            _ = await checkpoint;
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(
            root.Path,
            options with { DurableFileSystem = PlatformDurableFileSystem.Instance });
        CollectionAssert.AreEqual(Payload(1, 128), await reopened.GetAsync("before"));
        CollectionAssert.AreEqual(Payload(2, 128), await reopened.GetAsync("during"));
    }

    [TestMethod]
    public async Task MutationWal_CheckpointRecyclesCommittedPayloadAndRemainsWritable()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 1,
            WalRecycleMinimumReclaimBytes = 0
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                for (var index = 0; index < 40; index++)
                {
                    await transaction.SaveAsync(
                        $"cycle-{cycle}-item-{index:D2}",
                        Payload((cycle * 100) + index, 96));
                }

                await transaction.CommitAsync();
                Assert.IsGreaterThan(
                    TransactionWriteAheadLog.FileHeaderSize,
                    new FileInfo(WalPath(root.Path)).Length);
                _ = await store.CheckpointAsync();
                Assert.AreEqual(
                    TransactionWriteAheadLog.FileHeaderSize,
                    new FileInfo(WalPath(root.Path)).Length);
            }
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(120, reopened.Count);
        CollectionAssert.AreEqual(Payload(239, 96), await reopened.GetAsync("cycle-2-item-39"));
    }

    [TestMethod]
    public async Task MutationWal_RetainedPrefixUsesChecksummedRecoveryBoundary()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 2,
            WalRecycleMinimumReclaimBytes = 16L * 1024 * 1024,
            WalMaximumBytesBeforeForcedRecycle = 32L * 1024 * 1024
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 100; index++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync($"seek-{index:D3}", Payload(index, 96));
                await transaction.CommitAsync();
            }

            _ = await store.CheckpointAsync();
        }

        var boundary = Directory.EnumerateFiles(
                Path.Combine(root.Path, "transactions"),
                "wal-boundary-*.idbwbd")
            .Single();
        Assert.IsGreaterThan(0L, new FileInfo(boundary).Length);

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.IsGreaterThan(0L, reopened.Statistics.WalRecoveryPrefixBytesSkipped);
        Assert.AreEqual(100, reopened.Count);
        CollectionAssert.AreEqual(Payload(99, 96), await reopened.GetAsync("seek-099"));
    }

    [TestMethod]
    public async Task MutationWal_CorruptRecoveryBoundaryFallsBackToCompleteWalValidation()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 2,
            WalRecycleMinimumReclaimBytes = 16L * 1024 * 1024,
            WalMaximumBytesBeforeForcedRecycle = 32L * 1024 * 1024
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 32; index++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync($"fallback-{index:D2}", Payload(index, 64));
                await transaction.CommitAsync();
            }

            _ = await store.CheckpointAsync();
        }

        var boundary = Directory.EnumerateFiles(
                Path.Combine(root.Path, "transactions"),
                "wal-boundary-*.idbwbd")
            .Single();
        CorruptLastByte(boundary);

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(0L, reopened.Statistics.WalRecoveryPrefixBytesSkipped);
        Assert.AreEqual(32, reopened.Count);
        CollectionAssert.AreEqual(Payload(31, 64), await reopened.GetAsync("fallback-31"));
    }

    [TestMethod]
    public async Task PagedPrimaryIndex_ReopensWithoutMaterializingCheckpointKeysAndSupportsMutations()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
            {
                Mode = SegmentCorePrimaryIndexMode.Paged
            }
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 500; index++)
            {
                await store.SaveAsync($"paged-{index:D4}", Payload(index, 80));
            }

            _ = await store.CheckpointAsync();
        }

        Assert.HasCount(
            1,
            Directory.EnumerateFiles(Path.Combine(root.Path, "checkpoints"), "checkpoint-*.idbmap"));
        await using (var reopened = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            Assert.IsTrue(reopened.Statistics.PrimaryIndexIsPaged);
            Assert.AreEqual(500, reopened.Count);
            CollectionAssert.AreEqual(Payload(499, 80), await reopened.GetAsync("paged-0499"));
            await reopened.SaveAsync("paged-0499", Payload(999, 80));
            Assert.IsTrue(await reopened.DeleteAsync("paged-0001"));
            _ = await reopened.CheckpointAsync();
        }

        await using var final = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.IsTrue(final.Statistics.PrimaryIndexIsPaged);
        Assert.AreEqual(499, final.Count);
        Assert.IsNull(await final.GetAsync("paged-0001"));
        CollectionAssert.AreEqual(Payload(999, 80), await final.GetAsync("paged-0499"));
    }

    [TestMethod]
    public async Task PagedPrimaryIndex_CorruptMapFallsBackToValidatedResidentCheckpoint()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
            {
                Mode = SegmentCorePrimaryIndexMode.Paged
            }
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync("mapped", Payload(7, 64));
            _ = await store.CheckpointAsync();
        }

        var map = Directory.EnumerateFiles(
                Path.Combine(root.Path, "checkpoints"),
                "checkpoint-*.idbmap")
            .Single();
        CorruptLastByte(map);

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.IsFalse(reopened.Statistics.PrimaryIndexIsPaged);
        CollectionAssert.AreEqual(Payload(7, 64), await reopened.GetAsync("mapped"));
    }

    [TestMethod]
    public async Task OnlineBackup_RestoresOneConsistentWalCheckpointAndSourceRemainsWritable()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        var backup = Path.Combine(root.Path, "backup");
        var restored = Path.Combine(root.Path, "restored");
        var options = WalOptions(stripes: 4) with
        {
            PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
            {
                Mode = SegmentCorePrimaryIndexMode.Paged
            }
        };
        SegmentCoreBackupResult backupResult;
        await using (var store = await SegmentCoreStore.OpenAsync(source, options))
        {
            for (var index = 0; index < 200; index++)
            {
                await store.SaveAsync($"backup-{index:D3}", Payload(index, 128));
            }

            await store.SaveAsync("backup-199", Payload(999, 128));
            Assert.IsTrue(await store.DeleteAsync("backup-001"));
            backupResult = await store.BackupAsync(backup);
            await store.SaveAsync("after-backup", Payload(2_000, 64));
        }

        Assert.AreEqual(199, backupResult.Entries);
        Assert.IsGreaterThan(0, backupResult.Files);
        var restoreResult = await SegmentCoreStore.RestoreBackupAsync(
            backup,
            restored,
            options);
        Assert.AreEqual(199, restoreResult.Entries);

        await using var reopened = await SegmentCoreStore.OpenAsync(restored, options);
        Assert.AreEqual(199, reopened.Count);
        Assert.IsTrue(reopened.Statistics.PrimaryIndexIsPaged);
        Assert.IsNull(await reopened.GetAsync("backup-001"));
        Assert.IsNull(await reopened.GetAsync("after-backup"));
        CollectionAssert.AreEqual(Payload(999, 128), await reopened.GetAsync("backup-199"));
        var integrity = await reopened.VerifyIntegrityAsync();
        Assert.IsTrue(integrity.IsValid, string.Join(Environment.NewLine, integrity.Failures));
    }

    [TestMethod]
    public async Task OnlineBackup_RestoresParticipantCommitJournalPrefix()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "participant-source");
        var backup = Path.Combine(root.Path, "participant-backup");
        var restored = Path.Combine(root.Path, "participant-restored");
        var options = Options(stripes: 2, segmentBytes: 64 * 1024) with
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.ParticipantCommit
        };
        await using (var store = await SegmentCoreStore.OpenAsync(source, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("left", Payload(1, 96));
            await transaction.SaveAsync("right", Payload(2, 96));
            await transaction.CommitAsync();
            _ = await store.BackupAsync(backup);
        }

        _ = await SegmentCoreStore.RestoreBackupAsync(backup, restored, options);
        await using var reopened = await SegmentCoreStore.OpenAsync(restored, options);
        Assert.AreEqual(2, reopened.Count);
        CollectionAssert.AreEqual(Payload(1, 96), await reopened.GetAsync("left"));
        CollectionAssert.AreEqual(Payload(2, 96), await reopened.GetAsync("right"));
    }

    [TestMethod]
    public async Task OnlineBackup_CancellationWhileWaitingForCheckpointCleansStagingAndReleasesCompactionGate()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        var cancelledBackup = Path.Combine(root.Path, "cancelled-backup");
        var successfulBackup = Path.Combine(root.Path, "successful-backup");
        var fileSystem = new FailingDurableFileSystem();
        var options = WalOptions(stripes: 2) with { DurableFileSystem = fileSystem };
        await using var store = await SegmentCoreStore.OpenAsync(source, options);
        await store.SaveAsync("durable", Payload(1, 128));

        fileSystem.BlockCheckpointPublication();
        var checkpoint = store.CheckpointAsync().AsTask();
        await fileSystem.WaitForCheckpointPublicationAsync();
        using var cancellation = new CancellationTokenSource();
        var interruptedBackup = store.BackupAsync(
            cancelledBackup,
            cancellationToken: cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            _ = await interruptedBackup;
            Assert.Fail("The blocked online backup should observe cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            fileSystem.ReleaseCheckpointPublication();
        }

        _ = await checkpoint;
        Assert.IsFalse(Directory.Exists(cancelledBackup));
        Assert.IsFalse(Directory.EnumerateDirectories(
            root.Path,
            ".cancelled-backup.backup-*",
            SearchOption.TopDirectoryOnly).Any());

        var completed = await store.BackupAsync(successfulBackup).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, completed.Entries);
        Assert.IsTrue(Directory.Exists(successfulBackup));
    }

    [TestMethod]
    public async Task RestoreBackup_RejectsChangedBackupFileWithoutPublishingDestination()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        var backup = Path.Combine(root.Path, "backup");
        var restored = Path.Combine(root.Path, "restored");
        var options = WalOptions(stripes: 2);
        await using (var store = await SegmentCoreStore.OpenAsync(source, options))
        {
            await store.SaveAsync("protected", Payload(5, 64));
            _ = await store.BackupAsync(backup);
        }

        var segment = Directory.EnumerateFiles(backup, "segment-*.idbseg", SearchOption.AllDirectories).First();
        CorruptLastByte(segment);
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => SegmentCoreStore.RestoreBackupAsync(backup, restored, options).AsTask());
        Assert.IsFalse(Directory.Exists(restored));
    }

    [TestMethod]
    public async Task RestoreBackup_RejectsUnsupportedRouterBeforePublishingDestination()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        var backup = Path.Combine(root.Path, "backup");
        var restored = Path.Combine(root.Path, "restored");
        var options = WalOptions(stripes: 2);
        await using (var store = await SegmentCoreStore.OpenAsync(source, options))
        {
            await store.SaveAsync("protected", Payload(6, 64));
            _ = await store.BackupAsync(backup);
        }

        var manifest = Path.Combine(backup, "segment-core-backup.json");
        var json = File.ReadAllText(manifest);
        var changed = json.Replace(
            SegmentCoreStorageFormat.RouterId,
            "unsupported-router-v9",
            StringComparison.Ordinal);
        Assert.AreNotEqual(json, changed);
        File.WriteAllText(manifest, changed);

        _ = await Assert.ThrowsExactlyAsync<SegmentCoreCompatibilityException>(
            () => SegmentCoreStore.RestoreBackupAsync(backup, restored, options).AsTask());
        Assert.IsFalse(Directory.Exists(restored));
    }

    [TestMethod]
    public async Task MutationWal_RecyclingPreservesPriorCheckpointFallback()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 2,
            WalRecycleMinimumReclaimBytes = 0
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using (var first = await store.BeginTransactionAsync())
            {
                await first.SaveAsync("first", Payload(1, 64));
                await first.CommitAsync();
            }

            _ = await store.CheckpointAsync();
            await using (var second = await store.BeginTransactionAsync())
            {
                await second.SaveAsync("second", Payload(2, 64));
                await second.CommitAsync();
            }

            _ = await store.CheckpointAsync();
            Assert.IsGreaterThan(
                TransactionWriteAheadLog.FileHeaderSize,
                new FileInfo(WalPath(root.Path)).Length);
        }

        var newest = Directory.EnumerateFiles(
                Path.Combine(root.Path, "checkpoints"),
                "checkpoint-*.idbidx")
            .OrderByDescending(static path => path, StringComparer.Ordinal)
            .First();
        CorruptLastByte(newest);

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(2, reopened.Count);
        Assert.AreEqual(1L, reopened.Statistics.CheckpointGeneration);
        CollectionAssert.AreEqual(Payload(2, 64), await reopened.GetAsync("second"));
    }

    [TestMethod]
    public async Task MutationWal_CorruptIncrementalCheckpointFallsBackAndReplaysItsTransaction()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with { CheckpointGenerationsToKeep = 2 };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var generation = 1; generation <= 3; generation++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync($"delta-{generation}", Payload(generation, 96));
                await transaction.CommitAsync();
                _ = await store.CheckpointAsync();
            }
        }

        var delta = Directory.EnumerateFiles(
                Path.Combine(root.Path, "checkpoints"),
                "checkpoint-*.idbdlt")
            .Single();
        CorruptLastByte(delta);

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(3, reopened.Count);
        Assert.AreEqual(2L, reopened.Statistics.CheckpointGeneration);
        CollectionAssert.AreEqual(Payload(3, 96), await reopened.GetAsync("delta-3"));
    }

    [TestMethod]
    public async Task MutationWal_IndependentIncrementalFallbackBoundsWalToTwoCheckpointIntervals()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 2,
            WalRecycleMinimumReclaimBytes = 0
        };
        var retainedIntervalBytes = 0L;
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var cycle = 0; cycle < 4; cycle++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync($"bounded-{cycle}", Payload(cycle, 128));
                await transaction.CommitAsync();
                _ = await store.CheckpointAsync();
                var walBytes = new FileInfo(WalPath(root.Path)).Length;
                if (cycle == 0)
                {
                    Assert.AreEqual(TransactionWriteAheadLog.FileHeaderSize, walBytes);
                }
                else if (cycle == 1)
                {
                    retainedIntervalBytes = walBytes;
                    Assert.IsGreaterThan(TransactionWriteAheadLog.FileHeaderSize, walBytes);
                }
                else
                {
                    var maximum = TransactionWriteAheadLog.FileHeaderSize
                        + (2 * (retainedIntervalBytes - TransactionWriteAheadLog.FileHeaderSize));
                    Assert.IsLessThanOrEqualTo(maximum, walBytes);
                }
            }
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(4, reopened.Count);
    }

    [TestMethod]
    public async Task Compact_RewritesLiveFramesReclaimsOldSegmentsAndReopens()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with
        {
            CheckpointGenerationsToKeep = 2,
            LogOptions = WalOptions(stripes: 4).LogOptions with { MaxSegmentBytes = 16 * 1024 }
        };
        SegmentCoreCompactionResult result;
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var index = 0; index < 300; index++)
            {
                await store.SaveAsync($"compact-{index:D4}", Payload(index, 256));
            }

            for (var index = 0; index < 300; index++)
            {
                await store.SaveAsync($"compact-{index:D4}", Payload(index + 1000, 256));
            }

            _ = await store.CheckpointAsync();
            result = await store.CompactAsync();
            Assert.AreEqual(300, result.LiveEntries);
            Assert.AreEqual(300L, result.FramesCopied);
            Assert.IsGreaterThan(0L, result.BytesReclaimed);
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(300, reopened.Count);
        CollectionAssert.AreEqual(Payload(1299, 256), await reopened.GetAsync("compact-0299"));
        Assert.HasCount(300, await reopened.ReadAllAsync());
    }

    [TestMethod]
    public async Task MutationWal_RecycleReplacementFailureFaultsStoreAndReopensSafely()
    {
        foreach (var failAfterReplace in new[] { false, true })
        {
            using var root = new TemporaryDirectory();
            var fileSystem = new FailingDurableFileSystem
            {
                FailOnReplace = 1,
                FailAfterReplace = failAfterReplace
            };
            var options = WalOptions(stripes: 4) with
            {
                CheckpointGenerationsToKeep = 1,
                WalRecycleMinimumReclaimBytes = 0,
                DurableFileSystem = fileSystem
            };
            await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync("durable", Payload(1, 64));
                await transaction.CommitAsync();

                _ = await Assert.ThrowsExactlyAsync<IOException>(() => store.CheckpointAsync().AsTask());
                _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => store.GetAsync("durable").AsTask());
            }

            await using var reopened = await SegmentCoreStore.OpenAsync(
                root.Path,
                options with { DurableFileSystem = PlatformDurableFileSystem.Instance });
            CollectionAssert.AreEqual(Payload(1, 64), await reopened.GetAsync("durable"));
        }
    }

    [TestMethod]
    public async Task MutationWal_MissingRecycledCoordinatorIsRejected()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4) with { CheckpointGenerationsToKeep = 1 };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("durable", Payload(1, 64));
            await transaction.CommitAsync();
            _ = await store.CheckpointAsync();
        }

        File.Delete(WalPath(root.Path));
        _ = await Assert.ThrowsExactlyAsync<ObjectLogCorruptionException>(
            () => SegmentCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    [TestMethod]
    public async Task MutationWal_RecoveryPreservesExplicitAndImplicitSameKeyOrder()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync("ordered", Payload(0, 64));
            await using (var first = await store.BeginTransactionAsync())
            {
                await first.SaveAsync("ordered", Payload(1, 64));
                await first.CommitAsync();
            }

            await store.SaveAsync("ordered", Payload(2, 64));
            await using (var deletion = await store.BeginTransactionAsync())
            {
                Assert.IsTrue(await deletion.DeleteAsync("ordered"));
                await deletion.CommitAsync();
            }

            await store.SaveAsync("ordered", Payload(3, 64));
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual(Payload(3, 64), await reopened.GetAsync("ordered"));
    }

    [TestMethod]
    public async Task MutationWal_IgnoresAbandonedPrepareAndDoesNotCheckpointItsTransactionId()
    {
        using var root = new TemporaryDirectory();
        var options = WalOptions(stripes: 4);
        await using (var empty = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
        }

        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(
                         root.Path,
                         options.LogOptions))
        {
            _ = await writer.AppendTransactionalAsync(
                ObjectLogRecordKind.Put,
                "abandoned",
                Payload(1, 64),
                transactionId: 42);
        }

        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            Assert.IsNull(await store.GetAsync("abandoned"));
            _ = await store.CheckpointAsync();
            await using var committed = await store.BeginTransactionAsync();
            await committed.SaveAsync("committed", Payload(2, 64));
            await committed.CommitAsync();
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.IsNull(await reopened.GetAsync("abandoned"));
        CollectionAssert.AreEqual(Payload(2, 64), await reopened.GetAsync("committed"));
    }

    [TestMethod]
    public async Task TransactionProtocol_CannotChangeForAnExistingStore()
    {
        using var root = new TemporaryDirectory();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, WalOptions(stripes: 4)))
        {
            await store.SaveAsync("value", Payload(1, 32));
        }

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => SegmentCoreStore.OpenAsync(
                root.Path,
                Options(stripes: 4, segmentBytes: 1024 * 1024)).AsTask());
    }

    [TestMethod]
    public async Task MutationWal_ExplicitCommitUsesOneDurabilityFlush()
    {
        using var root = new TemporaryDirectory();
        var fileSystem = new FailingDurableFileSystem();
        var options = WalOptions(stripes: 8) with { DurableFileSystem = fileSystem };
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, options);
        var before = fileSystem.FileFlushCount;
        await using var transaction = await store.BeginTransactionAsync();
        for (var index = 0; index < 64; index++)
        {
            await transaction.SaveAsync($"flush-{index:D2}", Payload(index, 64));
        }

        await transaction.CommitAsync();

        Assert.AreEqual(1, fileSystem.FileFlushCount - before);
    }

    [TestMethod]
    public async Task MutationWal_ConcurrentTransactionsShareDurabilityFlushes()
    {
        using var root = new TemporaryDirectory();
        var fileSystem = new FailingDurableFileSystem();
        var baseOptions = WalOptions(stripes: 8);
        var options = baseOptions with
        {
            DurableFileSystem = fileSystem,
            LogOptions = baseOptions.LogOptions with
            {
                GroupCommitDelay = TimeSpan.FromMilliseconds(10),
                MaximumBatchRecords = 64
            }
        };
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, options);
        var transactions = new ISegmentCoreTransaction[16];
        for (var index = 0; index < transactions.Length; index++)
        {
            transactions[index] = await store.BeginTransactionAsync();
            await transactions[index].SaveAsync($"group-{index:D2}", Payload(index, 64));
        }

        var before = fileSystem.FileFlushCount;
        try
        {
            await Task.WhenAll(transactions.Select(transaction => transaction.CommitAsync().AsTask()));
        }
        finally
        {
            await Task.WhenAll(transactions.Select(transaction => transaction.DisposeAsync().AsTask()));
        }

        var flushes = fileSystem.FileFlushCount - before;
        Assert.IsGreaterThan(0, flushes);
        Assert.IsLessThan(transactions.Length, flushes);
        Assert.AreEqual(transactions.Length, store.Count);
    }

    [TestMethod]
    public async Task MutationWal_FlushFailureFaultsStoreAndRecoveryIsAllOrNothing()
    {
        using var root = new TemporaryDirectory();
        var fileSystem = new FailingDurableFileSystem();
        var options = WalOptions(stripes: 4) with { DurableFileSystem = fileSystem };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            await transaction.SaveAsync("uncertain-a", Payload(1, 128));
            await transaction.SaveAsync("uncertain-b", Payload(2, 128));
            fileSystem.FailOnFileFlush = fileSystem.FileFlushCount + 1;

            _ = await Assert.ThrowsExactlyAsync<IOException>(() => transaction.CommitAsync().AsTask());
            Assert.AreEqual(SegmentCoreTransactionState.Faulted, transaction.State);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.GetAsync("uncertain-a").AsTask());
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(
            root.Path,
            options with { DurableFileSystem = PlatformDurableFileSystem.Instance });
        var recoveredA = await reopened.GetAsync("uncertain-a");
        var recoveredB = await reopened.GetAsync("uncertain-b");
        Assert.AreEqual(recoveredA is null, recoveredB is null);
    }

    [TestMethod]
    public async Task StorageOverloads_AcceptOwnedHandleAndRejectForeignHandle()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        await using var first = await OpenAsync(firstRoot.Path);
        await using var second = await OpenAsync(secondRoot.Path);
        await using var owned = await first.BeginTransactionAsync();
        await using var foreign = await second.BeginTransactionAsync();

        await first.SaveAsync("owned", Payload(1, 64), owned);
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => first.SaveAsync("foreign", Payload(2, 64), foreign).AsTask());
        await owned.CommitAsync();

        CollectionAssert.AreEqual(Payload(1, 64), await first.GetAsync("owned"));
        Assert.IsNull(await first.GetAsync("foreign"));
    }

    [TestMethod]
    public async Task TransactionEvents_ArePostTransitionObserversAndCannotChangeOutcome()
    {
        using var root = new TemporaryDirectory();
        await using var store = await OpenAsync(root.Path);
        var states = new List<SegmentCoreTransactionState>();
        store.TransactionStateChanged += (_, arguments) =>
        {
            states.Add(arguments.State);
            throw new InvalidOperationException("Observer failures must be isolated.");
        };

        await using var transaction = await store.BeginTransactionAsync();
        await transaction.SaveAsync("alpha", Payload(1, 64));
        await transaction.CommitAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                SegmentCoreTransactionState.Active,
                SegmentCoreTransactionState.Committing,
                SegmentCoreTransactionState.Committed
            },
            states);
        CollectionAssert.AreEqual(Payload(1, 64), await store.GetAsync("alpha"));
    }

    private static ValueTask<SegmentCoreStore> OpenAsync(string root) =>
        SegmentCoreStore.OpenAsync(root, Options(stripes: 8, segmentBytes: 1024 * 1024));

    private static SegmentCoreOptions Options(int stripes, long segmentBytes) => new()
    {
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = stripes,
            MaximumBatchRecords = 64,
            MaxSegmentBytes = segmentBytes,
            DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
        }
    };

    private static SegmentCoreOptions WalOptions(int stripes) =>
        Options(stripes, 1024 * 1024) with
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            LogOptions = Options(stripes, 1024 * 1024).LogOptions with
            {
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            }
        };

    private static byte[] Payload(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string WalPath(string root) => Path.Combine(
        root,
        "transactions",
        "mutation-wal.idbwal");

    private static void CorruptLastByte(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = stream.Length - 1;
        var value = stream.ReadByte();
        Assert.AreNotEqual(-1, value);
        stream.Position = stream.Length - 1;
        stream.WriteByte((byte)(value ^ 0xFF));
        stream.Flush(flushToDisk: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterfaceDbSegmentCoreTests",
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
                // Test cleanup should not hide the assertion result.
            }
        }
    }

    private sealed class FailingDurableFileSystem : IDurableFileSystem
    {
        private int _fileFlushCount;
        private int _replaceCount;
        private TaskCompletionSource _checkpointPublication = NewSignal();
        private readonly ManualResetEventSlim _checkpointPublicationRelease = new(initialState: true);
        private int _blockCheckpointPublication;

        public int FileFlushCount => Volatile.Read(ref _fileFlushCount);

        public int? FailOnFileFlush { get; set; }

        public int? FailOnReplace { get; set; }

        public bool FailAfterReplace { get; set; }

        public string DirectoryFlushStrategy => "fault-injection wrapper";

        public void BlockCheckpointPublication()
        {
            _checkpointPublication = NewSignal();
            _checkpointPublicationRelease.Reset();
            Volatile.Write(ref _blockCheckpointPublication, 1);
        }

        public Task WaitForCheckpointPublicationAsync() => _checkpointPublication.Task;

        public void ReleaseCheckpointPublication()
        {
            Volatile.Write(ref _blockCheckpointPublication, 0);
            _checkpointPublicationRelease.Set();
        }

        public void FlushFile(SafeFileHandle handle)
        {
            var count = Interlocked.Increment(ref _fileFlushCount);
            if (count == FailOnFileFlush)
            {
                throw new IOException("Injected transaction durability failure.");
            }

            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath)
        {
            if (Volatile.Read(ref _blockCheckpointPublication) != 0
                && Path.GetExtension(finalPath) is ".idbidx" or ".idbdlt")
            {
                _checkpointPublication.TrySetResult();
                if (!_checkpointPublicationRelease.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Checkpoint publication test gate timed out.");
                }
            }

            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);
        }

        public void ReplaceFile(string temporaryPath, string finalPath)
        {
            var count = Interlocked.Increment(ref _replaceCount);
            if (count == FailOnReplace && !FailAfterReplace)
            {
                throw new IOException("Injected WAL replacement failure before publication.");
            }

            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
            if (count == FailOnReplace && FailAfterReplace)
            {
                throw new IOException("Injected WAL replacement failure after publication.");
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
