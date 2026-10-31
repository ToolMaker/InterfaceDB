using System.Buffers.Binary;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task ClockMutationCannotDeleteRollbackOrPoisonAnyScope(bool partitioned, bool legacyWriter)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
            { DefaultAccessPolicy = partitioned ? new SubjectDataAccessPolicy() : UnrestrictedDataAccessPolicy.Instance });
        await using (var repository = Client(host, "parent-a")) await repository.SaveAsync(new RemotePerson { Name = "saved" });
        var connection = Options(host, Token("parent-a")).Connection;
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(connection with
            { RequiredCapabilities = legacyWriter ? connection.RequiredCapabilities & ~EncodedStoreCapabilities.ReserveRevisions : connection.RequiredCapabilities });
        var original = await client.GetAsync(RepositoryRevisionStore.ClockKey);
        foreach (var value in new long?[] { null, 0, long.MaxValue, long.MaxValue - 1 })
        foreach (var batch in new[] { false, true })
        {
            var payload = value is null ? null : RepositoryRevisionStore.EncodeClock(value.Value);
            var request = batch ? new EncodedStoreRequest { Command = EncodedStoreCommand.ApplyBatch,
                Mutations = [new("innocent", [7]), new(RepositoryRevisionStore.ClockKey, payload)] }
                : new() { Command = value is null ? EncodedStoreCommand.Delete : EncodedStoreCommand.Put,
                    Key = RepositoryRevisionStore.ClockKey, Payload = payload };
            var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.ExecuteAsync(request));
            Assert.AreEqual(RemoteErrorCode.InvalidRequest, error.Code);
            CollectionAssert.AreEqual(original, await client.GetAsync(RepositoryRevisionStore.ClockKey));
            Assert.IsNull(await client.GetAsync("innocent"));
        }
        if (legacyWriter) return; // An older writer can still read, but cannot mutate the protected clock.
        var import = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(import, long.MaxValue / 2);
        var denied = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.ExecuteAsync(new()
            { Command = EncodedStoreCommand.ReserveRevisions, PageSize = 1, Payload = import }));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, denied.Code);
        CollectionAssert.AreEqual(original, await client.GetAsync(RepositoryRevisionStore.ClockKey));
    }

    [TestMethod]
    public async Task AuthorizedFutureImportSurvivesRestartAndOrdinaryWritesAdvancePastIt()
    {
        const long imported = long.MaxValue / 2;
        string key;
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(store) with
            { Stores = [new("people", Manifest(), store) { RevisionImportPolicy = new TestImportPolicy() }] }))
        await using (var repository = Client(host, "parent-a"))
        {
            var saved = await repository.SaveWithExtensionMetadataAsync(new RemotePerson { Name = "imported" }, null,
                [5], new EnvelopeWriteCondition(0, EnvelopeRevisionAction.Import, imported, ExpectedMissing: true));
            Assert.AreEqual(imported, saved.Envelope!.RevisionTimestampUtcMicroseconds);
            key = saved.Envelope.EnvelopeCacheKey;
        }
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root))
        await using (var host = await Host(store))
        await using (var repository = Client(host, "parent-a"))
        {
            var saved = await repository.QuerySingleAsync(_ => true);
            Assert.AreEqual(imported, saved.Envelope!.RevisionTimestampUtcMicroseconds);
            saved.Score++;
            var updated = await repository.SaveAsync(saved);
            Assert.IsTrue(updated.Envelope!.RevisionTimestampUtcMicroseconds > imported);
            Assert.AreEqual(key, updated.Envelope.EnvelopeCacheKey);
            CollectionAssert.AreEqual(new byte[] { 5 }, updated.Envelope.ExtensionMetadata);
        }
    }

    private sealed class TestImportPolicy : IRepositoryRevisionImportPolicy
    {
        public bool AuthorizeImport(DataAccessContext context, DataAccessScope authorizedScope, long sourceRevision)
            => context.Identity?.Principal.FindFirst("sub")?.Value == "parent-a";
    }
}
