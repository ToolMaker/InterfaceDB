using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SerializerPasses_CrudUsesOneEncodingPerWriteAndOneDecodingPerSnapshotRecord(bool marker, bool encrypted)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var manifest = Manifest() with { EnvelopeFormat = marker ? MarkerFormat : Manifest().EnvelopeFormat };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { Stores = [new("people", manifest, store)] });
        var counter = new MarkerSerializer(marker);
        await using var repository = new TcpRepository<IRemotePerson>(CountedOptions(host, counter, manifest, encrypted));

        var saved = await repository.SaveAsync(new RemotePerson { Name = "first", Score = 1 });
        counter.AssertAndReset(1, 0);
        // The unencrypted envelope is stored byte-for-byte, with no server-side re-encoding.
        if (!encrypted)
        {
            var records = (await store.EnumerateAsync(20)).Records!;
            CollectionAssert.AreEqual(counter.LastPayload!, records.Single(record => record.Payload.Length != 16).Payload);
        }
        var stale = await repository.QuerySingleAsync(_ => true);
        counter.AssertAndReset(0, 1);
        saved.Name = "updated";
        saved = await repository.SaveAsync(saved);
        counter.AssertAndReset(1, 1);
        Assert.AreEqual("updated", (await repository.SearchFirstAsync(person => person.Name == "updated"))!.Name);
        counter.AssertAndReset(0, 1);
        var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(
            async () => await repository.SaveAsync(stale));
        counter.AssertAndReset(0, 1);
        Assert.AreEqual("updated", (await conflict.ReadCurrentAsync())!.Name);
        counter.AssertAndReset(0, 1); // An explicit fresh read is a new operation.
        Assert.IsTrue(await repository.DeleteAsync(saved));
        counter.AssertAndReset(0, 1);
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
        counter.AssertAndReset(0, 0);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SerializerPasses_BatchAndDeltaMaterializeEachReceivedRecordOnlyOnce(bool marker, bool encrypted)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var manifest = Manifest() with { EnvelopeFormat = marker ? MarkerFormat : Manifest().EnvelopeFormat };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { Stores = [new("people", manifest, store)] });
        var counter = new MarkerSerializer(marker);
        await using var repository = new TcpRepository<IRemotePerson>(CountedOptions(host, counter, manifest, encrypted));

        var saved = await repository.SaveManyAsync([new RemotePerson { Name = "one" }, new RemotePerson { Name = "two" }]);
        counter.AssertAndReset(2, 0);
        var initial = await repository.ReadDeltaAsync(null, []);
        Assert.AreEqual(2, initial.Records.Count);
        counter.AssertAndReset(0, 2);
        var unchanged = await repository.ReadDeltaAsync(initial.Cursor, []);
        Assert.AreEqual(0, unchanged.Records.Count);
        counter.AssertAndReset(0, 0);
        saved[0].Score = 3;
        await repository.SaveAsync(saved[0]);
        counter.AssertAndReset(1, 2); // The current online repository loads a fresh snapshot per operation.
        var changed = await repository.ReadDeltaAsync(unchanged.Cursor, []);
        Assert.AreEqual(1, changed.Records.Count);
        Assert.AreEqual(3, changed.Records[0].Score);
        counter.AssertAndReset(0, 1);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SerializerPasses_MetadataAcknowledgementReusesTheMaterializedSnapshot(bool marker, bool encrypted)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var manifest = Manifest() with { EnvelopeFormat = marker ? MarkerFormat : Manifest().EnvelopeFormat };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { Stores = [new("people", manifest, store)] });
        var counter = new MarkerSerializer(marker);
        await using var repository = new TcpRepository<IRemotePerson>(CountedOptions(host, counter, manifest, encrypted));
        var saved = await repository.SaveWithExtensionMetadataAsync(new RemotePerson { Name = "original", Score = 7 }, null, [1]);
        counter.AssertAndReset(1, 0);
        var revision = saved.Envelope!.RevisionTimestampUtcMicroseconds;
        var key = saved.Envelope.EnvelopeCacheKey;
        var acknowledged = await repository.SaveWithExtensionMetadataAsync(new RemotePerson { Name = "must not replace data" }, key,
            [2], new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
        counter.AssertAndReset(1, 1);
        Assert.AreEqual("original", acknowledged.Name);
        Assert.AreEqual(7, acknowledged.Score);
        Assert.AreEqual(revision, acknowledged.Envelope!.RevisionTimestampUtcMicroseconds);
        CollectionAssert.AreEqual(new byte[] { 2 }, acknowledged.Envelope.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 1 }, saved.Envelope.ExtensionMetadata);

        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () =>
            await repository.SaveWithExtensionMetadataAsync(new RemotePerson(), key, [3],
                new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1])));
        counter.AssertAndReset(0, 1);
        var stored = await repository.QuerySingleAsync(_ => true);
        counter.AssertAndReset(0, 1);
        CollectionAssert.AreEqual(new byte[] { 2 }, stored.Envelope!.ExtensionMetadata);
        Assert.AreEqual("original", stored.Name);
    }

    [TestMethod]
    public async Task SerializerPasses_PushNotificationsDoNotEncodeOrDecodeEnvelopes()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var counter = new MarkerSerializer(false);
        await using var subscriber = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a")) with { Serializer = counter });
        await using var writer = Client(host, "parent-a");
        int notifications = 0;
        await using var subscription = subscriber.SubscribeChanges(() => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; });
        await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(Volatile.Read(ref notifications) == 1));
        await writer.SaveAsync(new RemotePerson { Name = "notification" });
        await Until(() => Task.FromResult(Volatile.Read(ref notifications) >= 2));
        counter.AssertAndReset(0, 0);
        Assert.AreEqual("notification", (await subscriber.QuerySingleAsync(_ => true)).Name);
        counter.AssertAndReset(0, 1);
    }

    private TcpRepositoryOptions<IRemotePerson> CountedOptions(TcpStoreHost host, MarkerSerializer counter,
        CompatibilityManifest manifest, bool encrypted)
    {
        IEncryptor encryptor = encrypted ? new AesGcmEncryptor(new SerializerKey()) : new EncryptionNone();
        IObjectSerializer<IRemotePerson> serializer = encrypted
            ? new EncryptingObjectSerializer<IRemotePerson>(counter, encryptor, 4 * 1024 * 1024) : counter;
        return WithSerializer(host, serializer, manifest) with { Encryptor = encryptor };
    }
}
