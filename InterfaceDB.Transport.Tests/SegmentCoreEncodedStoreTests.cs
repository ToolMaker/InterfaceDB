using System.Security.Cryptography;
using Idb.Transport;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class SegmentCoreEncodedStoreTests
{
    private string _path = null!;

    [TestInitialize]
    public void Initialize() => _path = Path.Combine(Path.GetTempPath(), "InterfaceDB.Transport.Tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public async Task OpaqueRoundtripDistinguishesEmptyMissingAndOwnsPayload()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        byte[] source = [0xc1, 0xff, 0x00, 0x83]; // Deliberately not a valid domain envelope.
        await store.PutAsync("opaque", source);
        source[0] = 0;
        var first = await store.GetAsync("opaque");
        CollectionAssert.AreEqual(new byte[] { 0xc1, 0xff, 0x00, 0x83 }, first);
        first![0] = 0;
        CollectionAssert.AreEqual(new byte[] { 0xc1, 0xff, 0x00, 0x83 }, await store.GetAsync("opaque"));
        await store.PutAsync("empty", []);
        var empty = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "empty" });
        Assert.IsTrue(empty.Found);
        Assert.HasCount(0, empty.Payload!);
        var missing = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "missing" });
        Assert.IsFalse(missing.Found);
        Assert.IsNull(missing.Payload);
    }

    [TestMethod]
    public async Task AcknowledgedPutDeleteAndAtomicBatchSurviveReopen()
    {
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path))
        {
            await store.PutAsync("deleted", [1]);
            await store.PutAsync("survives", [2]);
            Assert.IsTrue(await store.DeleteAsync("deleted"));
            Assert.IsFalse(await store.DeleteAsync("missing"));
            var batch = await store.ApplyBatchAsync([new("survives", [3]), new("new", [4]), new("absent", null)]);
            Assert.AreEqual(2, batch.AffectedCount);
        }
        await using (var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path))
        {
            Assert.IsNull(await reopened.GetAsync("deleted"));
            Assert.IsNull(await reopened.GetAsync("absent"));
            CollectionAssert.AreEqual(new byte[] { 3 }, await reopened.GetAsync("survives"));
            CollectionAssert.AreEqual(new byte[] { 4 }, await reopened.GetAsync("new"));
            await reopened.ApplyBatchAsync([new("new", null), new("final", [5])]);
            await reopened.ExecuteAsync(new() { Command = EncodedStoreCommand.Checkpoint });
        }
        await using var final = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        Assert.IsNull(await final.GetAsync("new"));
        CollectionAssert.AreEqual(new byte[] { 5 }, await final.GetAsync("final"));
    }

    [TestMethod]
    public async Task InvalidBatchHasNoPartialMutation()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        await store.PutAsync("existing", [7]);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.ApplyBatchAsync([new("existing", [8]), new(" ", [9])]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.ApplyBatchAsync([new("existing", [8]), new("existing", null)]));
        CollectionAssert.AreEqual(new byte[] { 7 }, await store.GetAsync("existing"));
        Assert.HasCount(1, (await store.EnumerateAsync(10)).Records!);
    }

    [TestMethod]
    public async Task EnumerationUsesOrdinalKeysAndContinuationWithoutSkipping()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        foreach (var key in new[] { "z", "B", "b", "a", "A" }) await store.PutAsync(key, [1]);
        var page1 = await store.EnumerateAsync(2);
        CollectionAssert.AreEqual(new[] { "A", "B" }, page1.Records!.Select(r => r.Key).ToArray());
        Assert.AreEqual("B", page1.NextAfterKey);
        var page2 = await store.EnumerateAsync(2, page1.NextAfterKey);
        CollectionAssert.AreEqual(new[] { "a", "b" }, page2.Records!.Select(r => r.Key).ToArray());
        var page3 = await store.EnumerateAsync(2, page2.NextAfterKey);
        CollectionAssert.AreEqual(new[] { "z" }, page3.Records!.Select(r => r.Key).ToArray());
        Assert.IsNull(page3.NextAfterKey);
        Assert.HasCount(0, (await store.EnumerateAsync(2, "zz")).Records!);
        CollectionAssert.AreEqual(new[] { "b", "z" }, (await store.EnumerateAsync(2, "az")).Records!.Select(r => r.Key).ToArray());
        var many = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.GetMany, Keys = ["z", "missing", "A", "z"] });
        CollectionAssert.AreEqual(new[] { "z", "A", "z" }, many.Records!.Select(r => r.Key).ToArray());
    }

    [TestMethod]
    public async Task FrameBudgetShortensPagesAndRejectsOversizedReadAndWriteBatches()
    {
        var limits = SmallLimits();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, limits);
        foreach (var key in new[] { "a", "b", "c" }) await store.PutAsync(key, new byte[6500]);
        var first = await store.EnumerateAsync(10);
        CollectionAssert.AreEqual(new[] { "a", "b" }, first.Records!.Select(r => r.Key).ToArray());
        Assert.AreEqual("b", first.NextAfterKey);
        Assert.HasCount(1, (await store.EnumerateAsync(10, first.NextAfterKey)).Records!);
        var readError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await store.ExecuteAsync(new() { Command = EncodedStoreCommand.GetMany, Keys = ["a", "b", "c"] }));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, readError.Code);
        var writeError = await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () =>
            await store.ApplyBatchAsync([new("a", new byte[6500]), new("b", new byte[6500]), new("new", new byte[6500])]));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, writeError.Code);
        Assert.IsNull(await store.GetAsync("new"));
    }

    [TestMethod]
    public async Task KeyCapacityRejectsGrowthBeforeStateBecomesUnenumerable()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, maximumEnumerationKeys: 2);
        await store.PutAsync("a", [1]);
        await store.PutAsync("b", [2]);
        Assert.HasCount(2, (await store.EnumerateAsync(10)).Records!);
        var error = await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () => await store.PutAsync("c", [3]));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, error.Code);
        Assert.IsNull(await store.GetAsync("c"));
        await store.PutAsync("a", [4]);
        Assert.IsTrue(await store.DeleteAsync("b"));
        await store.PutAsync("c", [3]);
        CollectionAssert.AreEqual(new[] { "a", "c" },
            (await store.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
    }

    [TestMethod]
    public async Task PartitionCapacityCannotConsumeAnotherPartitionsBudget()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path,
            maximumEnumerationKeys: 4, maximumPartitionKeys: 2);
        static EncodedStoreRequest Put(string partition, string key, byte value) => new()
            { Command = EncodedStoreCommand.Put, Partition = partition, Key = partition + key, Payload = [value] };
        await store.ExecuteAsync(Put("a.", "one", 1));
        await store.ExecuteAsync(Put("a.", "two", 2));

        var rejected = await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () =>
            await store.ExecuteAsync(Put("a.", "three", 3)));

        Assert.AreEqual(RemoteErrorCode.LimitExceeded, rejected.Code);
        await store.ExecuteAsync(Put("b.", "one", 4));
        await store.ExecuteAsync(Put("b.", "two", 5));
        var a = await store.ExecuteAsync(new()
            { Command = EncodedStoreCommand.Enumerate, Partition = "a.", AfterKey = "a.", PageSize = 10 });
        var b = await store.ExecuteAsync(new()
            { Command = EncodedStoreCommand.Enumerate, Partition = "b.", AfterKey = "b.", PageSize = 10 });
        CollectionAssert.AreEqual(new[] { "a.one", "a.two" }, a.Records!.Select(record => record.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "b.one", "b.two" }, b.Records!.Select(record => record.Key).ToArray());
        Assert.HasCount(4, (await store.EnumerateAsync(10)).Records!);
    }

    [TestMethod]
    public async Task EmptyDeletesCannotCreateUnboundedChangePartitions()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, maximumEnumerationKeys: 2);
        static EncodedStoreRequest Delete(string partition) => new()
            { Command = EncodedStoreCommand.Delete, Partition = partition, Key = partition + "missing" };
        await store.ExecuteAsync(Delete("a."));
        await store.ExecuteAsync(Delete("b."));

        var rejected = await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () =>
            await store.ExecuteAsync(Delete("c.")));

        Assert.AreEqual(RemoteErrorCode.LimitExceeded, rejected.Code);
    }

    [TestMethod]
    public async Task ReopenedStoreRejectsValuesAboveNewResponseLimits()
    {
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path))
            await store.PutAsync("large", new byte[9000]);
        await using var restricted = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, SmallLimits());
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await restricted.GetAsync("large"));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, error.Code);
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await restricted.EnumerateAsync(1));
    }

    [TestMethod]
    public async Task CancellationBeforeMutationPreservesExistingState()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        await store.PutAsync("a", [1]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await store.PutAsync("a", [2], cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await store.ApplyBatchAsync([new("a", null)], cancellation.Token));
        CollectionAssert.AreEqual(new byte[] { 1 }, await store.GetAsync("a"));
    }

    [TestMethod]
    public async Task ExclusiveOwnershipAndIdempotentDisposalReleaseTheStore()
    {
        var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        try
        {
            await Assert.ThrowsAsync<IOException>(async () => await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path));
            await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Ping });
            await Task.WhenAll(store.DisposeAsync().AsTask(), store.DisposeAsync().AsTask());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await store.PutAsync("a", [1]));
        }
        finally { await store.DisposeAsync(); }
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        await reopened.PutAsync("a", [2]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanonicalSerializerPreservesEnvelopeBytesRevisionAndExplicitEncryptionContext(bool encrypted)
    {
        var binary = new BinaryObjectSerializer<SampleRecord>();
        IObjectSerializer<SampleRecord> serializer = encrypted
            ? new EncryptingObjectSerializer<SampleRecord>(binary, new AesGcmEncryptor(new SampleKeyProvider()))
            : binary;
        var bridge = new EncodedEnvelopeSerializer<SampleRecord>(serializer);
        byte[] context = "caller-selected-stable-storage-context"u8.ToArray();
        var envelope = new FileObjectEnvelope<SampleRecord>
        {
            EnvelopeCacheKey = "record", VersionTag = "1.0", RevisionTimestampUtcMicroseconds = 12345,
            ObjectInstance = new() { Name = "sample", Number = 42 }, ExtensionMetadata = [9, 8]
        };
        var payload = bridge.Serialize("record", envelope, context);
        Assert.IsTrue(payload.AsSpan(0, 4).SequenceEqual(encrypted ? "IDBE"u8 : "IDB6"u8));
        if (encrypted) Assert.AreEqual(3, payload[4]);
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path)) await store.PutAsync("record", payload);
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        var stored = await reopened.GetAsync("record");
        CollectionAssert.AreEqual(payload, stored);
        var restored = bridge.Deserialize("record", stored!, context);
        Assert.AreEqual(12345, restored.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual("sample", restored.ObjectInstance!.Name);
        Assert.AreEqual(42, restored.ObjectInstance.Number);
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, restored.ExtensionMetadata);
        Assert.ThrowsExactly<ArgumentException>(() => bridge.Serialize("other", envelope, context));
        Assert.ThrowsExactly<InvalidDataException>(() => bridge.Deserialize("other", stored!, context));
        if (encrypted) Assert.ThrowsExactly<CryptographicException>(() => bridge.Deserialize("record", stored!, "wrong"u8));
    }

    private static TransportLimits SmallLimits() => new()
    {
        MaximumFrameBytes = 16 * 1024, MaximumPayloadBytes = 8 * 1024, MaximumKeyBytes = 32
    };

    [MessagePackObject(true)]
    public sealed class SampleRecord : IStoredContract<SampleRecord>
    {
        public string Name { get; set; } = "";
        public int Number { get; set; }
        [IgnoreMember] public IObjectEnvelope<SampleRecord>? Envelope { get; set; }
    }

    private sealed class SampleKeyProvider : IKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
}
