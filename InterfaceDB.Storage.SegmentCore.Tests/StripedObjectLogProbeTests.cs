using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class StripedObjectLogProbeTests
{
    [TestMethod]
    public async Task ConcurrentAppends_RecoverEveryAcknowledgedRecord()
    {
        using var root = TestRoot.Create();
        const int recordCount = 512;
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(
                         root.Path,
                         Options(stripes: 8)))
        {
            var writes = Enumerable.Range(0, recordCount)
                .Select(index => writer.AppendAsync(
                    $"object/{index:D6}",
                    BitConverter.GetBytes(index)).AsTask())
                .ToArray();
            var addresses = await Task.WhenAll(writes);

            Assert.HasCount(recordCount, addresses);
            Assert.IsGreaterThan(1, addresses.Select(static address => address.Stripe).Distinct().Count());
        }

        var recovered = StripedObjectLogRecovery.Recover(root.Path);

        Assert.AreEqual(8, recovered.StripeCount);
        Assert.HasCount(recordCount, recovered.Records);
        for (var index = 0; index < recordCount; index++)
        {
            Assert.AreEqual(
                index,
                BitConverter.ToInt32(recovered.Records[$"object/{index:D6}"].Payload));
        }
    }

    [TestMethod]
    public async Task GroupCommit_AmortizesDurabilityFlushes()
    {
        using var root = TestRoot.Create();
        var fileSystem = new CountingDurableFileSystem();
        const int recordCount = 256;
        StripedObjectLogStatistics statistics;
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(
                         root.Path,
                         Options(stripes: 4) with { MaximumBatchRecords = 64 },
                         fileSystem))
        {
            var writes = Enumerable.Range(0, recordCount)
                .Select(index => writer.AppendAsync(
                    $"batch/{index:D6}",
                    new byte[512]).AsTask())
                .ToArray();
            _ = await Task.WhenAll(writes);
            statistics = writer.Statistics;
        }

        Assert.AreEqual(recordCount, statistics.SubmittedRecords);
        Assert.IsLessThan(recordCount, statistics.DurabilityFlushes);
        Assert.IsGreaterThan(1, statistics.LargestBatch);
        Assert.IsGreaterThan(statistics.DurabilityFlushes, fileSystem.FileFlushCount);
    }

    [TestMethod]
    public async Task WriteThroughStripes_RecoverWithoutExplicitBatchFlushes()
    {
        using var root = TestRoot.Create();
        var fileSystem = new CountingDurableFileSystem();
        const int recordCount = 128;
        StripedObjectLogStatistics statistics;
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(
                         root.Path,
                         Options(stripes: 8) with
                         {
                             DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
                         },
                         fileSystem))
        {
            var writes = Enumerable.Range(0, recordCount)
                .Select(index => writer.AppendAsync(
                    $"write-through/{index:D6}",
                    BitConverter.GetBytes(index)).AsTask())
                .ToArray();
            _ = await Task.WhenAll(writes);
            statistics = writer.Statistics;
        }

        var recovered = StripedObjectLogRecovery.Recover(root.Path);

        Assert.HasCount(recordCount, recovered.Records);
        Assert.AreEqual(0, statistics.DurabilityFlushes);
        Assert.AreEqual(1, fileSystem.FileFlushCount);
    }

    [TestMethod]
    public async Task StableRouting_KeepsUpdatesAndDeleteOnOneStripeAcrossReopen()
    {
        using var root = TestRoot.Create();
        StripedObjectLogRecordAddress first;
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, Options(stripes: 8)))
        {
            first = await writer.AppendAsync("stable-key", "version-one"u8.ToArray());
        }

        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, Options(stripes: 8)))
        {
            var second = await writer.AppendAsync("stable-key", "version-two"u8.ToArray());
            Assert.AreEqual(first.Stripe, second.Stripe);
            var deleted = await writer.DeleteAsync("stable-key");
            Assert.AreEqual(first.Stripe, deleted.Stripe);
        }

        var recovered = StripedObjectLogRecovery.Recover(root.Path);
        Assert.IsFalse(recovered.Records.ContainsKey("stable-key"));
    }

    [TestMethod]
    public async Task EverySupportedHash_KeepsUpdatesOnOneStripeAcrossReopen()
    {
        foreach (var algorithm in Enum.GetValues<SegmentCoreHashAlgorithm>())
        {
            using var root = TestRoot.Create();
            var options = Options(stripes: 8) with
            {
                HashAlgorithm = algorithm,
                HashSeed = 0x1020_3040_5060_7080
            };
            StripedObjectLogRecordAddress first;
            await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, options))
            {
                first = await writer.AppendAsync("stable-key", "version-one"u8.ToArray());
            }

            await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, options))
            {
                var second = await writer.AppendAsync("stable-key", "version-two"u8.ToArray());
                Assert.AreEqual(first.Stripe, second.Stripe, algorithm.ToString());
            }

            var recovered = StripedObjectLogRecovery.Recover(root.Path);
            CollectionAssert.AreEqual(
                "version-two"u8.ToArray(),
                recovered.Records["stable-key"].Payload,
                algorithm.ToString());
        }
    }

    [TestMethod]
    public async Task Reopen_RejectsChangedHashAlgorithmOrSeed()
    {
        using var root = TestRoot.Create();
        var original = Options(stripes: 8) with
        {
            HashAlgorithm = SegmentCoreHashAlgorithm.XxHash3,
            HashSeed = 42
        };
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, original))
        {
            _ = await writer.AppendAsync("key", "value"u8.ToArray());
        }

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => StripedObjectLogProbeWriter.OpenAsync(
                root.Path,
                original with { HashAlgorithm = SegmentCoreHashAlgorithm.Fnv1a64 }).AsTask());
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => StripedObjectLogProbeWriter.OpenAsync(
                root.Path,
                original with { HashSeed = 43 }).AsTask());
    }

    [TestMethod]
    public async Task DefaultHash_PreservesLegacyManifestAndRouting()
    {
        using var root = TestRoot.Create();
        var options = Options(stripes: 8);
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, options))
        {
            foreach (var key in new[] { "a", "stable-key", "object/000001", "emoji-\U0001F680" })
            {
                var address = await writer.AppendAsync(key, "value"u8.ToArray());
                Assert.AreEqual(LegacySelectStripe(key, options.StripeCount), address.Stripe, key);
            }
        }

        Assert.AreEqual(
            32,
            new FileInfo(Path.Combine(root.Path, "striped-log.manifest")).Length,
            "Default FNV-1a stores must retain the preview.3 manifest and routing format.");
    }

    [TestMethod]
    public void SupportedHashes_DistributeCommonPrefixKeysAcrossPowerOfTwoStripes()
    {
        const int stripes = 16;
        const int records = 100_000;
        foreach (var algorithm in Enum.GetValues<SegmentCoreHashAlgorithm>())
        {
            var counts = new int[stripes];
            for (var index = 0; index < records; index++)
            {
                var key = $"customer-common-prefix-{index:D8}";
                counts[SegmentCoreHashRouter.SelectStripe(key, stripes, algorithm, seed: 0)]++;
            }

            var expected = records / (double)stripes;
            var maximumDeviation = counts.Max(count => Math.Abs(count - expected) / expected);
            Assert.IsLessThan(0.08, maximumDeviation, $"{algorithm}: {string.Join(',', counts)}");
        }
    }

    [TestMethod]
    public async Task ConcurrentRotation_RecoversEveryStripeChain()
    {
        using var root = TestRoot.Create();
        const int recordCount = 96;
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(
                         root.Path,
                         Options(stripes: 4) with { MaxSegmentBytes = 4 * 1024 }))
        {
            var writes = Enumerable.Range(0, recordCount)
                .Select(index => writer.AppendAsync(
                    $"rotation/{index:D6}",
                    Enumerable.Repeat((byte)index, 1_300).ToArray()).AsTask())
                .ToArray();
            _ = await Task.WhenAll(writes);
        }

        var recovered = StripedObjectLogRecovery.Recover(root.Path);

        Assert.HasCount(recordCount, recovered.Records);
        Assert.IsTrue(Directory.EnumerateFiles(
                Path.Combine(root.Path, "stripes"),
                "segment-*.idbseg",
                SearchOption.AllDirectories)
            .Count() > 4);
    }

    [TestMethod]
    public async Task Reopen_RejectsChangedStripeCount()
    {
        using var root = TestRoot.Create();
        await using (var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, Options(stripes: 4)))
        {
            _ = await writer.AppendAsync("key", "value"u8.ToArray());
        }

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => StripedObjectLogProbeWriter.OpenAsync(root.Path, Options(stripes: 8)).AsTask());
    }

    [TestMethod]
    public async Task PartialOpenFailureDrainsLaunchedStripesAndReleasesTheirWriterLeases()
    {
        using var root = TestRoot.Create();
        var blockedStripeRoot = StripedObjectLogRecovery.StripeRoot(root.Path, stripe: 1);
        var blocker = await ObjectLogProbeWriter.OpenAsync(blockedStripeRoot);
        try
        {
            _ = await Assert.ThrowsExactlyAsync<IOException>(
                () => StripedObjectLogProbeWriter.OpenAsync(
                    root.Path,
                    Options(stripes: 4)).AsTask());
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        await using var reopened = await StripedObjectLogProbeWriter.OpenAsync(
            root.Path,
            Options(stripes: 4));
        _ = await reopened.AppendAsync("after-failed-open", "value"u8.ToArray());
    }

    [TestMethod]
    public async Task PublicWriter_RejectsWhitespaceKeysBeforeRouting()
    {
        using var root = TestRoot.Create();
        await using var writer = await StripedObjectLogProbeWriter.OpenAsync(root.Path, Options(stripes: 4));

        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.AppendAsync("   ", "value"u8.ToArray()).AsTask());
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.DeleteAsync("\t").AsTask());
    }

    [TestMethod]
    public async Task BoundedQueue_RejectModeFailsFastWithoutAcceptingExcessWrites()
    {
        using var root = TestRoot.Create();
        using var fileSystem = new BlockingDurableFileSystem();
        await using var writer = await StripedObjectLogProbeWriter.OpenAsync(
            root.Path,
            Options(stripes: 1) with
            {
                MaximumBatchRecords = 1,
                QueueCapacityPerStripe = 1,
                QueueFullBehavior = ObjectLogQueueFullBehavior.Reject,
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            },
            fileSystem);
        fileSystem.BlockFlushes();

        var active = writer.AppendAsync("active", new byte[256]).AsTask();
        fileSystem.WaitForBlockedFlush();
        var queued = writer.AppendAsync("queued", new byte[256]).AsTask();

        await Assert.ThrowsExactlyAsync<ObjectLogBackpressureException>(
            () => writer.AppendAsync("rejected", new byte[256]).AsTask());

        var saturated = writer.Statistics;
        Assert.AreEqual(1L, saturated.RejectedRecords);
        Assert.AreEqual(2, saturated.PeakOutstandingWritesPerStripe);
        fileSystem.ReleaseFlushes();
        await Task.WhenAll(active, queued);

        var recovered = StripedObjectLogRecovery.Recover(root.Path);
        Assert.HasCount(2, recovered.Records);
        Assert.IsFalse(recovered.Records.ContainsKey("rejected"));
    }

    [TestMethod]
    public async Task BoundedQueue_WaitModeAppliesBackpressureUntilCapacityReturns()
    {
        using var root = TestRoot.Create();
        using var fileSystem = new BlockingDurableFileSystem();
        await using var writer = await StripedObjectLogProbeWriter.OpenAsync(
            root.Path,
            Options(stripes: 1) with
            {
                MaximumBatchRecords = 1,
                QueueCapacityPerStripe = 1,
                QueueFullBehavior = ObjectLogQueueFullBehavior.Wait,
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            },
            fileSystem);
        fileSystem.BlockFlushes();

        var active = writer.AppendAsync("active", new byte[256]).AsTask();
        fileSystem.WaitForBlockedFlush();
        var queued = writer.AppendAsync("queued", new byte[256]).AsTask();
        var waiting = writer.AppendAsync("waiting", new byte[256]).AsTask();
        await Task.Delay(50);

        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(1L, writer.Statistics.BackpressuredRecords);
        Assert.AreEqual(3, writer.Statistics.PeakOutstandingWritesPerStripe);
        fileSystem.ReleaseFlushes();
        await Task.WhenAll(active, queued, waiting);

        Assert.AreEqual(0L, writer.Statistics.RejectedRecords);
        Assert.HasCount(3, StripedObjectLogRecovery.Recover(root.Path).Records);
    }

    private static StripedObjectLogProbeOptions Options(int stripes) => new()
    {
        StripeCount = stripes,
        MaximumBatchRecords = 64,
        MaxSegmentBytes = 1024 * 1024
    };

    private static int LegacySelectStripe(string key, int stripeCount)
    {
        var hash = 2166136261u;
        foreach (var character in key)
        {
            hash = (hash ^ (byte)character) * 16777619u;
            hash = (hash ^ (byte)(character >> 8)) * 16777619u;
        }

        return (int)(hash & (uint)(stripeCount - 1));
    }

    private sealed class CountingDurableFileSystem : IDurableFileSystem
    {
        private int _fileFlushCount;

        public int FileFlushCount => Volatile.Read(ref _fileFlushCount);

        public string DirectoryFlushStrategy => PlatformDurableFileSystem.Instance.DirectoryFlushStrategy;

        public void FlushFile(SafeFileHandle handle)
        {
            Interlocked.Increment(ref _fileFlushCount);
            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);

        public void ReplaceFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
    }

    private sealed class BlockingDurableFileSystem : IDurableFileSystem, IDisposable
    {
        private readonly ManualResetEventSlim _flushEntered = new(initialState: false);
        private readonly ManualResetEventSlim _releaseFlush = new(initialState: false);
        private int _blockFlushes;

        public string DirectoryFlushStrategy => PlatformDurableFileSystem.Instance.DirectoryFlushStrategy;

        public void BlockFlushes() => Volatile.Write(ref _blockFlushes, 1);

        public void ReleaseFlushes() => _releaseFlush.Set();

        public void WaitForBlockedFlush()
        {
            if (!_flushEntered.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test writer did not reach its durability flush.");
            }
        }

        public void FlushFile(SafeFileHandle handle)
        {
            if (Volatile.Read(ref _blockFlushes) != 0)
            {
                _flushEntered.Set();
                if (!_releaseFlush.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the blocked durability flush.");
                }
            }

            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);

        public void ReplaceFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);

        public void Dispose()
        {
            _releaseFlush.Set();
            _flushEntered.Dispose();
            _releaseFlush.Dispose();
        }
    }

    private sealed class TestRoot : IDisposable
    {
        private static readonly string Parent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "InterfaceDbStripedObjectLogTests");

        private TestRoot(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestRoot Create()
        {
            Directory.CreateDirectory(Parent);
            var path = System.IO.Path.Combine(Parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestRoot(path);
        }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var fullParent = System.IO.Path.GetFullPath(Parent) + System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to clean unexpected test path '{fullPath}'.");
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
