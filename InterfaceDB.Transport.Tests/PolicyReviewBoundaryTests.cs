using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(3)] [DataRow(10)]
    public async Task PolicyReview_CustomProviderHidesProtectedPagesWithoutChangingVisibleKeys(int pageSize)
    {
        var prefix = DataAccessScope.Partitioned("review", "a").Identity;
        string[] visible = ["a", "z", "idbp1.not-a-partition", ".idb-remote-repository-clock-v1"];
        var all = visible.Concat([prefix + "secret-1", prefix + "secret-2", "\u0001idb-changes:" + prefix])
            .Order(StringComparer.Ordinal).ToArray();
        var provider = new ReviewResponseStore(request =>
        {
            var remaining = all.Where(key => request.AfterKey is null
                || StringComparer.Ordinal.Compare(key, request.AfterKey) > 0).ToArray();
            var page = remaining.Take(request.PageSize).Select(key => new EncodedRecord(key, [7])).ToArray();
            return new() { Records = page, NextAfterKey = remaining.Length > page.Length ? page[^1].Key : null };
        });
        await using var host = await TcpStoreHost.StartAsync(HostOptions(provider) with
            { DefaultAccessPolicy = UnrestrictedDataAccessPolicy.Instance });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("shared")).Connection);
        var actual = new List<string>();
        string? after = null;
        do
        {
            var page = await client.EnumerateAsync(pageSize, after);
            actual.AddRange(page.Records!.Select(record => record.Key));
            Assert.IsTrue(page.NextAfterKey is null || visible.Contains(page.NextAfterKey));
            after = page.NextAfterKey;
        } while (after is not null);
        CollectionAssert.AreEqual(visible.Order(StringComparer.Ordinal).ToArray(), actual.ToArray());
    }

    [TestMethod]
    [DataRow(EncodedStoreCommand.Get)] [DataRow(EncodedStoreCommand.GetMany)]
    public async Task PolicyReview_ReservedReadsAreRejectedBeforeAnyProviderCall(EncodedStoreCommand command)
    {
        var provider = new ReviewResponseStore(_ => throw new AssertFailedException("Reserved keys reached storage."));
        IEncodedEnvelopeStore view = new ScopedEncodedStore(provider, DataAccessScope.Unrestricted);
        foreach (var key in new[] { DataAccessScope.Partitioned("review", "a").Identity + "secret", "\u0001idb-changes:" })
        {
            var request = command == EncodedStoreCommand.Get
                ? new EncodedStoreRequest { Command = command, Key = key }
                : new EncodedStoreRequest { Command = command, Keys = ["ordinary", key] };
            var failure = await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () => await view.ExecuteAsync(request));
            Assert.AreEqual(RemoteErrorCode.InvalidRequest, failure.Code);
        }
    }

    [TestMethod]
    [DataRow(EncodedStoreCommand.GetMany)] [DataRow(EncodedStoreCommand.Changes)]
    public async Task PolicyReview_ProviderCannotSmuggleProtectedKeysInOtherReadResponses(EncodedStoreCommand command)
    {
        var key = DataAccessScope.Partitioned("review", "a").Identity + "secret";
        var provider = new ReviewResponseStore(_ => new() { Records = [new(key, [])] });
        IEncodedEnvelopeStore view = new ScopedEncodedStore(provider, DataAccessScope.Unrestricted);
        var request = command == EncodedStoreCommand.GetMany
            ? new EncodedStoreRequest { Command = command, Keys = ["ordinary"] }
            : new EncodedStoreRequest { Command = command, PageSize = 10 };
        var failure = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await view.ExecuteAsync(request));
        Assert.AreEqual(RemoteErrorCode.ProtocolError, failure.Code);
    }

    [TestMethod]
    public async Task PolicyReview_HiddenNonAdvancingProviderPagesFailInsteadOfLooping()
    {
        var key = DataAccessScope.Partitioned("review", "a").Identity + "secret";
        var provider = new ReviewResponseStore(_ => new() { Records = [new(key, [42])], NextAfterKey = key });
        IEncodedEnvelopeStore view = new ScopedEncodedStore(provider, DataAccessScope.Unrestricted);
        var failure = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await view.EnumerateAsync(1));
        Assert.AreEqual(RemoteErrorCode.ProtocolError, failure.Code);
        Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task PolicyReview_InvalidChangeCursorDoesNotEvictOtherTenantsIndex()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        IEncodedEnvelopeStore alice = new ScopedEncodedStore(store, DataAccessScope.Partitioned("review", "a"));
        IEncodedEnvelopeStore bob = new ScopedEncodedStore(store, DataAccessScope.Partitioned("review", "b"));
        await alice.PutAsync("a", [7]);
        await alice.EnumerateAsync(10);
        var before = store.PartitionIndexStatistics;
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await bob.ExecuteAsync(new()
            { Command = EncodedStoreCommand.Changes, PageSize = 10, AfterKey = "invalid" }));
        await alice.EnumerateAsync(10);
        Assert.AreEqual(before.Misses, store.PartitionIndexStatistics.Misses);
        Assert.AreEqual(before.Hits + 1, store.PartitionIndexStatistics.Hits);
    }

    [TestMethod]
    public async Task PolicyReview_GenericProviderFailureAfterCommitStillRequiresReconciliation()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(new ReviewCommittedFailureStore(inner)));
        await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("alice")).Connection))
        {
            await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () => await client.PutAsync("committed", [42]));
            await Assert.ThrowsExactlyAsync<IOException>(async () => await client.GetAsync("committed"));
        }
        IEncodedEnvelopeStore view = new ScopedEncodedStore(inner, DataAccessScope.Partitioned(Issuer, "alice"));
        CollectionAssert.AreEqual(new byte[] { 42 }, await view.GetAsync("committed"));
    }

    private sealed class ReviewResponseStore(Func<EncodedStoreRequest, EncodedStoreResponse> response) : IEncodedEnvelopeStore
    {
        public int Calls { get; private set; }
        public EncodedStoreCapabilities Capabilities => EncodedStoreCapabilities.All;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(response(request)); }
    }

    [TestMethod]
    public async Task PolicyReview_ProviderFrameBudgetRejectionHasKnownOutcome()
    {
        var limits = new TransportLimits();
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root, limits);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(inner) with
            { DefaultAccessPolicy = UnrestrictedDataAccessPolicy.Instance, Limits = limits });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("shared")).Connection with { Limits = limits });
        var payload = new byte[limits.MaximumPayloadBytes - 1024];
        var failure = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await client.ApplyBatchAsync([new("one", payload), new("two", payload)]));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, failure.Code);
        Assert.HasCount(0, (await client.EnumerateAsync(10)).Records!);
        await client.PutAsync("valid", [7]);
        CollectionAssert.AreEqual(new byte[] { 7 }, await client.GetAsync("valid"));
    }

    [TestMethod]
    public async Task PolicyReview_RejectionExceptionFromPostCommitIndexCannotClaimNoWrite()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root,
            keyIndexFactory: new ReviewRejectingIndexFactory());
        await using var host = await TcpStoreHost.StartAsync(HostOptions(inner));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("alice")).Connection);
        await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () => await client.PutAsync("committed", [42]));
        IEncodedEnvelopeStore view = new ScopedEncodedStore(inner, DataAccessScope.Partitioned(Issuer, "alice"));
        CollectionAssert.AreEqual(new[] { "committed" }, (await view.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
        CollectionAssert.AreEqual(new byte[] { 42 }, await view.GetAsync("committed"));
    }

    private sealed class ReviewRejectingIndexFactory : IPartitionKeyIndexFactory, IPartitionKeyIndex
    {
        private readonly IPartitionKeyIndex _inner = new BoundedPartitionKeyIndexFactory().Create();
        public IPartitionKeyIndex Create() => this;
        public PartitionIndexStatistics Statistics => _inner.Statistics;
        public void Initialize(Func<IReadOnlyList<string>> source, CancellationToken token) => _inner.Initialize(source, token);
        public PartitionKeyPage? Select(string partition, string? after, int take, Func<IReadOnlyList<string>> source, CancellationToken token)
            => _inner.Select(partition, after, take, source, token);
        public void Invalidate() => _inner.Invalidate();
        public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations)
        {
            _inner.ApplyCommitted(mutations);
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.LimitExceeded, "Injected extension error.");
        }
    }

    private sealed class ReviewCommittedFailureStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.ExecuteAsync(request, cancellationToken);
            if (request.IsMutation) throw new RemoteStoreException(RemoteErrorCode.LimitExceeded, "Injected error after commit.");
            return response;
        }
    }
}
