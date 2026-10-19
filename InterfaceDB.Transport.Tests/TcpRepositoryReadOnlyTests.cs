using Idb.Transport;
using Idb.Transport.Repositories;
namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests {
    [TestMethod]
    public async Task ReadOnlyCapabilitiesArePreservedAndMutationsAreDenied() {
        await using var store=await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        bool readOnly=false;
        const EncodedStoreCapabilities read=EncodedStoreCapabilities.Read|EncodedStoreCapabilities.GetMany|EncodedStoreCapabilities.Enumerate|EncodedStoreCapabilities.DurableCommits|EncodedStoreCapabilities.SubjectIsolation;
        await using var host=await TcpStoreHost.StartAsync(HostOptions(store) with {AuthorizeIdentity=(_,alias,caps)=>alias=="people" && (!readOnly || (caps & ~read)==0)});
        await using(var writer=Client(host,"reader")) await writer.SaveAsync(new RemotePerson {Name="private",Score=3});
        readOnly=true;
        var options=Options(host,Token("reader"));
        await using var reader=new TcpRepository<IRemotePerson>(options with {Connection=options.Connection with {RequiredCapabilities=read}});
        var row=await reader.QuerySingleAsync(_=>true);Assert.AreEqual(3,row.Score);
        var error=await Assert.ThrowsExactlyAsync<RemoteStoreException>(async()=>await reader.DeleteAsync(row));
        Assert.AreEqual(RemoteErrorCode.UnsupportedCapability,error.Code);
        Assert.AreEqual(3,(await reader.QuerySingleAsync(_=>true)).Score);
    }
}
