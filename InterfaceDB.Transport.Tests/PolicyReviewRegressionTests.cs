using System.Text;
using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private static IDataAccessPolicy MixedReviewPolicy()
    {
        var subject = new SubjectDataAccessPolicy();
        return new DelegateDataAccessPolicy(context =>
            context.Identity?.Principal.FindFirst("sub")?.Value == "shared"
                ? DataAccessScope.Unrestricted : subject.Authorize(context));
    }

    [TestMethod]
    [DataRow(PartitionIndexMode.Disabled)]
    [DataRow(PartitionIndexMode.Lazy)]
    [DataRow(PartitionIndexMode.Eager)]
    public async Task PolicyReview_MixedScopesKeepPagesAndChangesWithinTheirOwnNamespace(PartitionIndexMode mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root,
            keyIndexFactory: new BoundedPartitionKeyIndexFactory(new() { Mode = mode }));
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = MixedReviewPolicy() });
        await using var alice = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("alice")).Connection);
        await alice.PutAsync("secret", [42]);
        await using var shared = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("shared")).Connection);
        var initial = await shared.ExecuteAsync(new() { Command = EncodedStoreCommand.Changes, PageSize = 10 });
        string[] ordinary = ["a", "z", "idbp1.not-a-partition", "idbp1." + new string('A', 64) + "."];
        foreach (var key in ordinary) await shared.PutAsync(key, [7]);
        await alice.PutAsync("second-secret", [43]);

        var actual = new List<string>();
        string? after = null;
        do
        {
            var page = await shared.EnumerateAsync(1, after);
            actual.AddRange(page.Records!.Select(record => record.Key));
            Assert.IsTrue(page.Records!.All(record => record.Payload.SequenceEqual(new byte[] { 7 })));
            after = page.NextAfterKey;
        } while (after is not null);
        CollectionAssert.AreEqual(ordinary.Order(StringComparer.Ordinal).ToArray(), actual.ToArray());
        foreach (var key in ordinary) CollectionAssert.AreEqual(new byte[] { 7 }, await shared.GetAsync(key));
        var changes = await shared.ExecuteAsync(new() { Command = EncodedStoreCommand.Changes, PageSize = 10,
            AfterKey = Encoding.UTF8.GetString(initial.Payload!) });
        // An empty journal acquires a new epoch on its first write, requiring a snapshot.
        if (changes.Found)
        {
            await shared.PutAsync("a", [7]);
            await alice.PutAsync("secret", [44]);
            changes = await shared.ExecuteAsync(new() { Command = EncodedStoreCommand.Changes, PageSize = 10,
                AfterKey = Encoding.UTF8.GetString(changes.Payload!) });
        }
        Assert.IsTrue(changes.Records!.All(record => ordinary.Contains(record.Key)));
        CollectionAssert.AreEqual(new[] { "second-secret", "secret" },
            (await alice.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
    }

    [TestMethod]
    public async Task PolicyReview_MixedScopesPreserveTypedSharedRepositoryCrud()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = MixedReviewPolicy() });
        await using (var alice = Client(host, "alice")) await Seed(alice, "private", 42);
        await using var shared = Client(host, "shared");
        Assert.IsNull(await shared.QueryFirstAsync(_ => true));
        var saved = await Seed(shared, "ordinary", 7);
        Assert.AreEqual(7, (await shared.QuerySingleAsync(_ => true)).Score);
        saved.Score = 8;
        await shared.SaveAsync(saved);
        Assert.AreEqual(8, (await shared.QuerySingleAsync(_ => true)).Score);
        Assert.IsTrue(await shared.DeleteAsync(saved));
        Assert.IsNull(await shared.QueryFirstAsync(_ => true));
        await using var owner = Client(host, "alice");
        Assert.AreEqual(42, (await owner.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task PolicyReview_RejectedTenantGrowthPreservesOtherTenantsWarmIndex()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root, maximumPartitionKeys: 2);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store));
        await using var alice = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("alice")).Connection);
        await alice.PutAsync("a", [1]);
        await alice.EnumerateAsync(10);
        var before = store.PartitionIndexStatistics;
        await using var bob = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("bob")).Connection);
        await Assert.ThrowsAsync<RemoteStoreException>(async () => await bob.ApplyBatchAsync(
            [new("1", [1]), new("2", [2]), new("3", [3])]));
        Assert.AreEqual(before.CachedPartitions, store.PartitionIndexStatistics.CachedPartitions);
        CollectionAssert.AreEqual(new[] { "a" }, (await alice.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
        Assert.AreEqual(before.Misses, store.PartitionIndexStatistics.Misses);
        Assert.AreEqual(before.Hits + 1, store.PartitionIndexStatistics.Hits);
    }

    [TestMethod]
    public async Task PolicyReview_CapacityRejectionHasKnownOutcomeAndSessionRemainsUsable()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root, maximumPartitionKeys: 2);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store));
        await using var bob = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("bob")).Connection);
        var rejected = await Assert.ThrowsAsync<RemoteStoreException>(async () => await bob.ApplyBatchAsync(
            [new("1", [1]), new("2", [2]), new("3", [3])]));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, rejected.Code);
        Assert.HasCount(0, (await bob.EnumerateAsync(10)).Records!);
        await bob.PutAsync("valid", [9]);
        CollectionAssert.AreEqual(new byte[] { 9 }, await bob.GetAsync("valid"));
    }

    [TestMethod]
    [DataRow(EncodedStoreCommand.Put)]
    [DataRow(EncodedStoreCommand.Delete)]
    [DataRow(EncodedStoreCommand.ApplyBatch)]
    public async Task PolicyReview_ReservedNamespaceRejectionLeavesSessionAndDataIntact(EncodedStoreCommand command)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = MixedReviewPolicy() });
        await using var shared = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("shared")).Connection);
        var reserved = DataAccessScope.Partitioned(Issuer, "alice").Identity + "secret";
        var request = command == EncodedStoreCommand.ApplyBatch
            ? new EncodedStoreRequest { Command = command, Mutations = [new("ordinary", [8]), new(reserved, [1])] }
            : new EncodedStoreRequest { Command = command, Key = reserved, Payload = command == EncodedStoreCommand.Put ? [1] : null };
        var rejected = await Assert.ThrowsAsync<RemoteStoreException>(async () => await shared.ExecuteAsync(request));
        Assert.AreEqual(RemoteErrorCode.InvalidRequest, rejected.Code);
        Assert.IsNull(await shared.GetAsync("ordinary"));
        await shared.PutAsync("ordinary", [7]);
        CollectionAssert.AreEqual(new byte[] { 7 }, await shared.GetAsync("ordinary"));
        await using var alice = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("alice")).Connection);
        Assert.IsNull(await alice.GetAsync("secret"));
    }
}
