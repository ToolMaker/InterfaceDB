using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task WritePolicyRejectsDirectAndBatchedDeletesBeforeStorageAndKeepsSessionUsable(bool batch)
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        var policy = new ProtectedDeletePolicy();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = policy });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("parent-a")).Connection);
        await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Put, Key = "protected", Payload = [7] });
        var before = store.Operations;
        var request = batch
            ? new EncodedStoreRequest { Command = EncodedStoreCommand.ApplyBatch, Mutations = [new("other", [8]), new("protected", null)] }
            : new EncodedStoreRequest { Command = EncodedStoreCommand.Delete, Key = "protected" };
        var failure = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.ExecuteAsync(request));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, failure.Code);
        Assert.AreEqual(before, store.Operations, "Denied deletions must not reach storage.");
        CollectionAssert.AreEqual(new byte[] { 7 }, (await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "protected" })).Payload);
        Assert.IsNull((await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "other" })).Payload);
        policy.AllowDelete = true;
        await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Delete, Key = "protected" });
        Assert.IsNull((await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "protected" })).Payload);
    }

    private sealed class ProtectedDeletePolicy : IDataAccessPolicy, IDataAccessWritePolicy
    {
        public bool AllowDelete { get; set; }
        public DataAccessScope? Authorize(DataAccessContext context) => new SubjectDataAccessPolicy().Authorize(context);
        public void ValidateWrite(DataAccessContext context, EncodedStoreRequest request)
        {
            if (!AllowDelete && (request.Command == EncodedStoreCommand.Delete
                || request.Mutations?.Any(m => m.Payload is null) == true))
                throw new UnauthorizedAccessException("Deletion is denied by the custom write policy.");
        }
    }
}
