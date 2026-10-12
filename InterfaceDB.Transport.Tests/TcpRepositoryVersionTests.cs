using System.Buffers.Binary;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private static CompatibilityManifest Manifest2() => Manifest() with { ContractVersion = "2.0.0", ContractSchemaHash = "person-v2" };
    private static TcpStoreVersion StoreV1() => new(Manifest(), null, (key, bytes) => { _ = Serializer().Deserialize(bytes); });
    private static BinaryObjectSerializer<IRemotePerson> Serializer() => new(runtimeTypeAllowlist: new(typeof(RemotePerson)));
    private static TcpStoreVersion StoreV2(bool fail = false) => new(Manifest2(), (previous, key, bytes, token) =>
    {
        if (fail) throw new InvalidDataException("Injected migration failure.");
        Assert.AreEqual("1.0.0", previous.ContractVersion);
        var envelope = Serializer().Deserialize(bytes);
        Assert.AreEqual("1.0.0", envelope.VersionTag);
        envelope.VersionTag = "2.0.0";
        envelope.ObjectInstance!.Score += 10;
        return ValueTask.FromResult(Serializer().Serialize(envelope));
    }, (_, bytes) => Assert.AreEqual("2.0.0", Serializer().Deserialize(bytes).VersionTag));

    [TestMethod]
    public async Task Version_LiveWritesAreValidatedAtomicallyAndClockMetadataRemainsAvailable()
    {
        var path = Path.Combine(_root, "validated-master");
        var strict = new TcpStoreVersion(Manifest(), null, (_, bytes) =>
        {
            if (bytes.Length != 1 || bytes[0] != 1) throw new InvalidDataException("Invalid version payload.");
        });
        await using (var master = await VersionedTcpStore.OpenAsync(path, "people", [strict]))
        {
            var clock = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(clock, 7);
            BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(8), ~7L);
            await master.Registration.Store.PutAsync("valid", [1]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await master.Registration.Store.PutAsync("invalid", [2]));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await master.Registration.Store.ApplyBatchAsync([new("batch-valid", [1]), new("batch-invalid", [2])]));
            Assert.IsNull(await master.Registration.Store.GetAsync("batch-valid"));
            Assert.IsNull(await master.Registration.Store.GetAsync("batch-invalid"));
            await master.Registration.Store.PutAsync(".idb-remote-repository-clock-v1", clock);
            var forgedPrefix = "idbp1." + new string('A', 64) + ".";
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await master.Registration.Store.PutAsync(forgedPrefix + ".idb-remote-repository-clock-v1", clock));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await master.Registration.Store.PutAsync("suffix.idb-remote-repository-clock-v1", [9]));
        }
        await using var reopened = await VersionedTcpStore.OpenAsync(path, "people", [strict]);
        CollectionAssert.AreEqual(new byte[] { 1 }, await reopened.Registration.Store.GetAsync("valid"));
        Assert.AreEqual(16,
            (await reopened.Registration.Store.GetAsync(".idb-remote-repository-clock-v1"))!.Length);
    }

    [TestMethod]
    public async Task Version_ReopenRejectsPayloadPersistedByAnOlderNonvalidatingBuild()
    {
        var path = Path.Combine(_root, "legacy-poison");
        var strict = new TcpStoreVersion(Manifest(), null, (_, bytes) =>
        {
            if (bytes.Length != 1 || bytes[0] != 1) throw new InvalidDataException("Invalid version payload.");
        });
        await using (var master = await VersionedTcpStore.OpenAsync(path, "people", [strict])) { }
        var generation = Directory.GetDirectories(path).Single();
        await using (var raw = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(generation, "data")))
            await raw.PutAsync("persisted-invalid", [2]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var invalid = await VersionedTcpStore.OpenAsync(path, "people", [strict]);
        });
    }

    [TestMethod]
    public async Task Version_DiscoveryAuthenticatesWithoutTakingStoreLease()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a")).Connection;
        await using var owner = await TcpEncodedEnvelopeStore.ConnectAsync(options);
        Assert.AreEqual(Manifest(), await TcpEncodedEnvelopeStore.DiscoverAsync(options with { Manifest = Manifest2() }));
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpEncodedEnvelopeStore.DiscoverAsync(Options(host, Token("parent-a", "expired")).Connection));
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpEncodedEnvelopeStore.DiscoverAsync(options with { Manifest = Manifest2() with { ContractIdentity = "wrong" } }));
    }

    [TestMethod]
    public async Task Version_MasterMigratesBeforeActivationAndFailedUpgradePreservesOldStore()
    {
        var path = Path.Combine(_root, "master");
        await using (var master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with { Stores = [master.Registration] }))
        await using (var client = Client(host, "parent-a"))
        {
            await Seed(client, "person", 2);
            await Seed(client, "suffix.idb-remote-repository-clock-v1", 3);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var failed = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1(), StoreV2(true)]);
        });
        await using (var original = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]))
            Assert.AreEqual("1.0.0", original.RunningVersion);
        await using (var upgraded = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1(), StoreV2()]))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(upgraded.Registration.Store) with { Stores = [upgraded.Registration] }))
        await using (var client = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a")) with
        { Contract = new VersionPersonConfig("2.0.0"), Connection = Options(host, Token("parent-a")).Connection with { Manifest = Manifest2() } }))
        {
            Assert.AreEqual("2.0.0", upgraded.RunningVersion);
            Assert.AreEqual(12, (await client.QuerySingleAsync(x => x.Name == "person")).Score);
            Assert.AreEqual(13, (await client.QuerySingleAsync(x => x.Name.StartsWith("suffix"))).Score);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await using var downgrade = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]);
        });
    }




    private sealed class VersionPersonConfig(string version, string? root = null) : IDbContractConfigDto<IRemotePerson>
    {
        public string EnvelopeDICacheKey => "people";
        public string ContractName => "RemotePerson";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => version;
        public ContractRepositoryType RepositoryType => root is null ? ContractRepositoryType.Remote : ContractRepositoryType.SegmentCore;
        public StoragePolicy? StoragePolicy => null;
    }
}
