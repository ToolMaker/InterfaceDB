using System.Text;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task Delta_InsertsUpdatesDeletesAndAccountIsolationSurviveRestart()
    {
        var path = Path.Combine(_root, "feed");
        string cursor;
        string key;
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path))
        await using (var host = await Host(store))
        await using (var remote = Client(host, "parent-a"))
        {
            var saved = await Seed(remote, "one", 1);
            key = saved.Envelope!.EnvelopeCacheKey;
            var initial = await remote.ReadDeltaAsync(null, [], default);
            Assert.IsTrue(initial.IsSnapshot);
            cursor = initial.Cursor;
            saved.Score = 2;
            await remote.SaveAsync(saved);
            await Seed(remote, "two", 3);
            var delta = await remote.ReadDeltaAsync(cursor, [], default);
            Assert.IsFalse(delta.IsSnapshot);
            Assert.AreEqual(2, delta.Records.Count);
            Assert.AreEqual(2, delta.Records.Single(x => x.Name == "one").Score);
            cursor = delta.Cursor;
        }
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path);
        await using var restarted = await Host(reopened);
        await using var client = Client(restarted, "parent-a");
        var stable = await client.ReadDeltaAsync(cursor, [], default);
        Assert.IsFalse(stable.IsSnapshot);
        Assert.AreEqual(0, stable.Records.Count);
        await using (var other = Client(restarted, "parent-b"))
            await Seed(other, "private", 99);
        var isolated = await client.ReadDeltaAsync(cursor, [], default);
        Assert.AreEqual(cursor, isolated.Cursor);
        Assert.AreEqual(0, isolated.Records.Count);
        await client.DeleteAsync(await client.QuerySingleAsync(x => x.Name == "one"));
        var deleted = await client.ReadDeltaAsync(cursor, [], default);
        Assert.IsFalse(deleted.IsSnapshot);
        CollectionAssert.Contains(deleted.DeletedPhysicalKeys.ToArray(), key);
        Assert.AreEqual(0, deleted.Records.Count);
    }

    [TestMethod]
    public async Task Delta_ExpiredHistoryResetsToCurrentSnapshot()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root, retainedChangeCount: 2);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "start", 1);
        var old = await remote.ReadDeltaAsync(null, [], default);
        for (int i = 0; i < 4; i++) await Seed(remote, "new" + i, i);
        var reset = await remote.ReadDeltaAsync(old.Cursor, [], default);
        Assert.IsTrue(reset.IsSnapshot);
        Assert.AreEqual(5, reset.Records.Count);
    }




    [TestMethod]
    public async Task Delta_LostAcknowledgementStillPublishesDurableChanges()
    {
        var path = Path.Combine(_root, "server");
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path))
        await using (var host = await Host(new DropAcknowledgement(store)))
        await using (var remote = Client(host, "parent-a"))
        {
            await Assert.ThrowsExactlyAsync<RemoteRepositoryCommitOutcomeUnknownException>(async () => await Seed(remote, "one", 1));
        }
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path);
        await using var restarted = await Host(reopened);
        await using var client = Client(restarted, "parent-a");
        var delta = await client.ReadDeltaAsync(null, [], default);
        Assert.AreEqual(1, delta.Records.Count);
        Assert.AreEqual("one", delta.Records[0].Name);
    }


}
