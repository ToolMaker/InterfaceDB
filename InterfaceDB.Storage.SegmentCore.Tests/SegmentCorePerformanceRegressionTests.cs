using System.Collections;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCorePerformanceRegressionTests
{
    [TestMethod]
    public void Crc32C_AcceleratedAndPortableAgreeForUnalignedTailsAndIncrementalUpdates()
    {
        var bytes = new byte[1040];
        new Random(137).NextBytes(bytes);
        Assert.AreEqual(0u, Crc32C.Compute([]));
        for (var offset = 0; offset < 16; offset++)
        {
            for (var length = 0; length <= 1024; length++)
            {
                var input = bytes.AsSpan(offset, length);
                var expected = Crc32C.AppendPortable(Crc32C.InitialState, input);
                Assert.AreEqual(expected, Crc32C.Append(Crc32C.InitialState, input));
                var split = length / 3;
                var incremental = Crc32C.Append(Crc32C.InitialState, input[..split]);
                Assert.AreEqual(expected, Crc32C.Append(incremental, input[split..]));
                Assert.AreEqual(Crc32C.AppendPortable(0x12345678, input), Crc32C.Append(0x12345678, input));
            }
        }
    }

    [TestMethod]
    public void PrimaryIndexOverlay_EnumeratesWithoutPointReadsAndPreservesDeleteReinsertIdentity()
    {
        var original = Address(1);
        var replacement = Address(2);
        var basis = new CountingBasis(new Dictionary<string, StripedObjectLogRecordAddress>
        {
            ["updated"] = original,
            ["removed"] = original,
            ["untouched"] = original
        });
        using var map = new PrimaryIndexMap(basis);
        map["updated"] = replacement;
        Assert.IsTrue(map.TryRemove("removed", out _));
        map["new"] = replacement;
        Assert.IsTrue(map.TryRemove("new", out _));
        map["new"] = original;
        map["removed"] = replacement;
        var reads = basis.PointReads;

        var snapshot = map.ToDictionary();

        Assert.AreEqual(reads, basis.PointReads, "Enumeration must not reread paged basis membership.");
        Assert.AreEqual(4, map.Count);
        Assert.AreEqual(4, snapshot.Count);
        Assert.AreEqual(replacement, snapshot["updated"]);
        Assert.AreEqual(replacement, snapshot["removed"]);
        Assert.AreEqual(original, snapshot["new"]);
    }

    [TestMethod]
    public void DirectFrameEncoding_ClearsPaddingAndPreservesUnicodePayloadAndChecksums()
    {
        const string key = "unicode/Grüße/日本語/😀";
        for (var length = 0; length < 33; length++)
        {
            var payload = Enumerable.Range(0, length).Select(static value => (byte)value).ToArray();
            var frame = Enumerable.Repeat((byte)0xcc, ObjectLogFormat.GetFrameLength(key, length)).ToArray();
            ObjectLogFormat.WriteFrame(frame, ObjectLogRecordKind.Put, 123, key, payload, 456);
            var header = ObjectLogFormat.ParseFrameHeader(frame.AsSpan(0, ObjectLogFormat.FrameHeaderSize), "test", 64);
            ObjectLogFormat.ValidateFrame(frame, header, "test", 64);
            Assert.AreEqual(key, ObjectLogFormat.ReadKey(frame, header));
            Assert.AreEqual(123L, header.Lsn);
            Assert.AreEqual(456L, header.Auxiliary);
            CollectionAssert.AreEqual(payload, frame.AsSpan(ObjectLogFormat.FrameHeaderSize + Encoding.UTF8.GetByteCount(key), length).ToArray());
            var paddingStart = ObjectLogFormat.FrameHeaderSize + header.KeyLength + length;
            Assert.IsTrue(frame.AsSpan(paddingStart, frame.Length - ObjectLogFormat.FrameFooterSize - paddingStart).IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [TestMethod]
    public async Task CheckpointFingerprints_RevalidateCompleteFilesBeforeRecycling()
    {
        using var root = new TemporaryRoot();
        ObjectLogWritePosition[] positions = [new(1, 256, 2, 0)];
        var entries = new Dictionary<string, StripedObjectLogRecordAddress> { ["item"] = Address(1) };
        var first = await PrimaryIndexCheckpoint.WriteAsync(root.Path, positions, entries, 0, 1, 2);
        var second = await PrimaryIndexCheckpoint.WriteAsync(root.Path, positions, entries, first.Generation, 2, 2);
        var cache = new Dictionary<long, PrimaryIndexCheckpointFingerprint>();
        Assert.AreEqual(1L, PrimaryIndexCheckpoint.GetOldestRetainedTransactionWatermark(root.Path, 1, second, 2, cache));
        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(1L, PrimaryIndexCheckpoint.GetOldestRetainedTransactionWatermark(root.Path, 1, second, 2, cache));

        CorruptCheckpoint(root.Path, first.Generation);
        Assert.AreEqual(2L, PrimaryIndexCheckpoint.GetOldestRetainedTransactionWatermark(root.Path, 1, second, 2, cache));
        Assert.IsFalse(cache.ContainsKey(first.Generation));
        CorruptCheckpoint(root.Path, second.Generation);
        Assert.ThrowsExactly<ObjectLogCorruptionException>(() =>
            PrimaryIndexCheckpoint.GetOldestRetainedTransactionWatermark(root.Path, 1, second, 2, cache));
    }

    [TestMethod]
    public async Task OwnedTransactionPayloadAndPublicCopy_BothSurviveKeyedBatchReadAndReopen()
    {
        using var root = new TemporaryRoot();
        var options = Options();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await using var transaction = await store.BeginTransactionAsync();
            var publicPayload = "original"u8.ToArray();
            await transaction.SaveAsync("copied", publicPayload);
            publicPayload[0] = 0;
            await store.SaveOwnedAsync("owned", "transferred"u8.ToArray(), transaction);
            await transaction.CommitAsync();
            var records = await store.ReadManyAsync(["owned", "missing", "copied"]);
            var byKey = records.ToDictionary(static record => record.Key);
            Assert.AreEqual(2, byKey.Count);
            CollectionAssert.AreEqual("original"u8.ToArray(), byKey["copied"].Payload);
            CollectionAssert.AreEqual("transferred"u8.ToArray(), byKey["owned"].Payload);
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual("original"u8.ToArray(), await reopened.GetAsync("copied"));
        CollectionAssert.AreEqual("transferred"u8.ToArray(), await reopened.GetAsync("owned"));
    }

    [TestMethod]
    public async Task WalRecycling_CopiesSuffixAcrossChunkAndRecordBoundariesAndRecoversFallback()
    {
        using var root = new TemporaryRoot();
        var options = Options() with { WalRecycleMinimumReclaimBytes = 0, CheckpointGenerationsToKeep = 2 };
        var expected = new Dictionary<string, byte[]>();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var cycle = 1; cycle <= 4; cycle++)
            {
                await using var transaction = await store.BeginTransactionAsync();
                var payload = new byte[cycle == 3 ? (3 * 1024 * 1024) + 37 : 97];
                new Random(cycle).NextBytes(payload);
                expected.Add($"cycle-{cycle}", payload);
                await transaction.SaveAsync($"cycle-{cycle}", payload);
                await transaction.CommitAsync();
                await store.CheckpointAsync();
            }
        }

        var newest = Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, "checkpoints"), "*.idbidx")
            .OrderDescending(StringComparer.Ordinal).First();
        CorruptFooter(newest);
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(expected.Count, reopened.Count);
        foreach (var pair in expected)
        {
            CollectionAssert.AreEqual(pair.Value, await reopened.GetAsync(pair.Key));
        }
    }

    private static SegmentCoreOptions Options() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 2,
            MaximumBatchRecords = 8,
            QueueCapacityPerStripe = 16,
            MaxSegmentBytes = 4 * 1024 * 1024,
            MaximumCombinedWriteBytes = 64 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    private static StripedObjectLogRecordAddress Address(long lsn) => new(0, new(1, 64, 80, lsn));

    private static void CorruptCheckpoint(string root, long generation) =>
        CorruptFooter(System.IO.Path.Combine(root, "checkpoints", $"checkpoint-{generation:D20}.idbidx"));

    private static void CorruptFooter(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        file.Position = file.Length - 1;
        var value = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(value ^ 0x80));
        file.Flush(true);
    }

    private sealed class CountingBasis(Dictionary<string, StripedObjectLogRecordAddress> entries)
        : IReadOnlyDictionary<string, StripedObjectLogRecordAddress>
    {
        public int PointReads { get; private set; }
        public int Count => entries.Count;
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<StripedObjectLogRecordAddress> Values => entries.Values;
        public StripedObjectLogRecordAddress this[string key] => entries[key];
        public bool ContainsKey(string key) { PointReads++; return entries.ContainsKey(key); }
        public bool TryGetValue(string key, out StripedObjectLogRecordAddress value) { PointReads++; return entries.TryGetValue(key, out value); }
        public IEnumerator<KeyValuePair<string, StripedObjectLogRecordAddress>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("InterfaceDbSegmentPerformance-").FullName;
        public void Dispose()
        {
            if (!Path.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unexpected test directory.");
            }
            Directory.Delete(Path, recursive: true);
        }
    }
}
