using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class PolicyPagingHintTests
{
    private string _path = null!;

    [TestInitialize]
    public void Initialize() => _path = Path.Combine(Path.GetTempPath(), "InterfaceDB.PolicyPaging.Tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public async Task SharedPagesSkipOversizedHiddenPayloadsAndStillRespectFrameBudget()
    {
        var scope = DataAccessScope.Partitioned("paging", "owner");
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path))
        {
            IEncodedEnvelopeStore owner = new ScopedEncodedStore(store, scope);
            await owner.PutAsync("secret", new byte[9000]);
            foreach (var key in new[] { "zz-a", "zz-b", "zz-c" })
                await store.PutAsync(key, Enumerable.Repeat((byte)key[^1], 6500).ToArray());
        }

        var limits = new TransportLimits
        {
            MaximumFrameBytes = 16 * 1024, MaximumPayloadBytes = 8192,
            MaximumKeyBytes = 1024, MaximumBatchItems = 64, MaximumPageItems = 128
        };
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, limits);
        IEncodedEnvelopeStore shared = new ScopedEncodedStore(reopened, DataAccessScope.Unrestricted);
        var first = await shared.EnumerateAsync(10);
        CollectionAssert.AreEqual(new[] { "zz-a", "zz-b" }, first.Records!.Select(record => record.Key).ToArray());
        Assert.AreEqual("zz-b", first.NextAfterKey);
        var second = await shared.EnumerateAsync(10, first.NextAfterKey);
        CollectionAssert.AreEqual(new[] { "zz-c" }, second.Records!.Select(record => record.Key).ToArray());
        Assert.IsNull(second.NextAfterKey);
        foreach (var record in first.Records!.Concat(second.Records!))
            CollectionAssert.AreEqual(Enumerable.Repeat((byte)record.Key[^1], 6500).ToArray(), record.Payload);

        IEncodedEnvelopeStore ownerView = new ScopedEncodedStore(reopened, scope);
        var ownerError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await ownerView.EnumerateAsync(10));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, ownerError.Code);
        var rawError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await reopened.EnumerateAsync(10));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, rawError.Code);
    }

    [TestMethod]
    public async Task SharedPagesBypassEmptyScopeIndexWithoutChangingRawOrTenantEnumeration()
    {
        var index = new RecordingIndex();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, keyIndexFactory: index);
        var scope = DataAccessScope.Partitioned("paging", "owner");
        IEncodedEnvelopeStore owner = new ScopedEncodedStore(store, scope);
        await owner.PutAsync("secret", [1]);
        await store.PutAsync("zz-shared", [2]);

        IEncodedEnvelopeStore shared = new ScopedEncodedStore(store, DataAccessScope.Unrestricted);
        CollectionAssert.AreEqual(new[] { "zz-shared" }, (await shared.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
        Assert.HasCount(0, index.SelectedPartitions);

        CollectionAssert.AreEqual(new[] { scope.Partition + "secret", "zz-shared" },
            (await store.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "" }, index.SelectedPartitions);

        CollectionAssert.AreEqual(new[] { "secret" }, (await owner.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "", scope.Partition }, index.SelectedPartitions);
    }

    [TestMethod]
    public async Task SharedPagesPreserveLegalKeysThatResembleTheReservedPrefix()
    {
        var scope = DataAccessScope.Partitioned("paging", "owner");
        var keys = new[]
        {
            scope.Partition, "idbp1.short", "idbp1." + new string('a', 64) + ".lowercase",
            "idbp1." + new string('G', 64) + ".nonhex", "zz-shared"
        };
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        IEncodedEnvelopeStore shared = new ScopedEncodedStore(store, DataAccessScope.Unrestricted);
        foreach (var key in keys) await shared.PutAsync(key, [42]);
        IEncodedEnvelopeStore owner = new ScopedEncodedStore(store, scope);
        await owner.PutAsync("secret", [99]);

        var actual = new List<string>();
        string? after = null;
        do
        {
            var page = await shared.EnumerateAsync(1, after);
            Assert.HasCount(1, page.Records!);
            CollectionAssert.AreEqual(new byte[] { 42 }, page.Records![0].Payload);
            actual.Add(page.Records[0].Key);
            after = page.NextAfterKey;
        } while (after is not null);
        CollectionAssert.AreEqual(keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(), actual);
        Assert.HasCount(0, (await shared.EnumerateAsync(1, "zzzz")).Records!);
    }

    [TestMethod]
    public async Task SharedPagesRetainGlobalPhysicalKeyCeiling()
    {
        var scope = DataAccessScope.Partitioned("paging", "owner");
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path, maximumEnumerationKeys: 10))
        {
            IEncodedEnvelopeStore owner = new ScopedEncodedStore(store, scope);
            foreach (var key in new[] { "a", "b", "c" }) await owner.PutAsync(key, [1]);
            await store.PutAsync("zz-shared", [2]);
        }

        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path,
            maximumEnumerationKeys: 3, maximumPartitionKeys: 3);
        IEncodedEnvelopeStore shared = new ScopedEncodedStore(reopened, DataAccessScope.Unrestricted);
        var sharedError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await shared.EnumerateAsync(10));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, sharedError.Code);
        var rawError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await reopened.EnumerateAsync(10));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, rawError.Code);
        IEncodedEnvelopeStore ownerView = new ScopedEncodedStore(reopened, scope);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" },
            (await ownerView.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
    }

    [TestMethod]
    public async Task TenantViewClearsSharedOnlyHintBeforeCallingProvider()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        var recording = new RecordingStore(store);
        var scope = DataAccessScope.Partitioned("paging", "owner");
        IEncodedEnvelopeStore owner = new ScopedEncodedStore(recording, scope);
        var nestedLookingKey = DataAccessScope.Partitioned("paging", "nested").Partition + "message";
        await owner.PutAsync(nestedLookingKey, [7]);
        var page = await owner.ExecuteAsync(new()
        {
            Command = EncodedStoreCommand.Enumerate, PageSize = 10, UnpartitionedOnly = true
        });
        Assert.IsFalse(recording.LastRequest!.UnpartitionedOnly);
        Assert.AreEqual(scope.Partition, recording.LastRequest.Partition);
        CollectionAssert.AreEqual(new[] { nestedLookingKey }, page.Records!.Select(record => record.Key).ToArray());
    }

    [TestMethod]
    public async Task SharedOnlyHintDoesNotChangeWireBytesOrSurviveDecoding()
    {
        var limits = new TransportLimits();
        var request = new EncodedStoreRequest { Command = EncodedStoreCommand.Enumerate, PageSize = 10, AfterKey = "cursor" };
        Assert.IsFalse(request.UnpartitionedOnly);
        var message = new WireMessage
        {
            Kind = WireMessageKind.Request, CorrelationId = 1, TimeoutMilliseconds = 30_000, Request = request
        };
        var expected = MessagePackFrameCodec.Encode(message, limits);
        var hinted = MessagePackFrameCodec.Encode(message with { Request = request with { UnpartitionedOnly = true } }, limits);
        CollectionAssert.AreEqual(expected, hinted);
        await using var stream = new MemoryStream(hinted);
        var decoded = await MessagePackFrameCodec.ReadAsync(stream, limits);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(request, decoded.Request);
        Assert.IsFalse(decoded.Request!.UnpartitionedOnly);
    }

    private sealed class RecordingIndex : IPartitionKeyIndexFactory, IPartitionKeyIndex
    {
        internal List<string> SelectedPartitions { get; } = [];
        public IPartitionKeyIndex Create() => this;
        public PartitionIndexStatistics Statistics => new(0, 0, 0, 0, 0, 0, 0);
        public void Initialize(Func<IReadOnlyList<string>> source, CancellationToken token) { }
        public PartitionKeyPage? Select(string partition, string? after, int take, Func<IReadOnlyList<string>> source, CancellationToken token)
        {
            SelectedPartitions.Add(partition);
            return null;
        }
        public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations) { }
        public void Invalidate() { }
    }

    private sealed class RecordingStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        internal EncodedStoreRequest? LastRequest { get; private set; }
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return inner.ExecuteAsync(request, cancellationToken);
        }
    }
}
