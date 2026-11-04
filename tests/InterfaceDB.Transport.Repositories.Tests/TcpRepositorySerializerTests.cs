using System.Security.Cryptography;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Serializer_ManagedCacheSharesCustomSerializerAndReopens(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var manifest = Manifest() with { EnvelopeFormat = MarkerFormat };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { Stores = [new("people", manifest, store)] });
        var serializer = new MarkerSerializer();
        var options = WithSerializer(host, serializer, manifest);
        var cachePath = Path.Combine(_root, "managed");
        LocalRemoteRepository<IRemotePerson> Open() => SegmentCoreManagedRepository.Create(
            [options], cachePath, "parent-a", new PersonMapper(), mode, retryDelay: TimeSpan.FromMilliseconds(50));
        await using (var managed = Open())
        {
            await managed.InitializeAsync();
            await managed.SaveAsync(new RemotePerson { Name = "custom", Score = 7 });
            await using var remote = new TcpRepository<IRemotePerson>(options);
            await Until(async () => (await remote.QueryFirstAsync(_ => true))?.Score == 7);
        }
        await using var reopened = Open();
        await reopened.InitializeAsync();
        Assert.AreEqual(7, (await reopened.QuerySingleAsync(_ => true)).Score);
    }
    [TestMethod]
    public async Task Serializer_ManagedRejectsMissingCustomImplementation()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        options = options with { SerializerEnvelopeFormat = MarkerFormat,
            Connection = options.Connection with { Manifest = Manifest() with { EnvelopeFormat = MarkerFormat } } };
        Assert.ThrowsExactly<ArgumentException>(() => SegmentCoreManagedRepository.Create(
            [options], Path.Combine(_root, "invalid-cache"), "parent-a", new PersonMapper(), RepositoryReadPreference.LocalFirst));
    }
}
