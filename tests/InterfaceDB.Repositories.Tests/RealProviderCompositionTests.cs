using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Commercial.Repositories.Tests;

[TestClass]
public sealed partial class RealProviderCompositionTests
{
    [TestMethod]
    public async Task DurableFileEndpoints_ReopenPendingUpsertAndTombstoneWithoutCopyingEnvelopes()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDB.Commercial.Tests",
            Guid.NewGuid().ToString("N"));
        var preferredRoot = Path.Combine(testRoot, "preferred");
        var peerRoot = Path.Combine(testRoot, "peer");

        try
        {
            await using (var provider = CreateProvider(preferredRoot, peerRoot))
            {
                var repository = provider.GetRequiredService<IResilientRepository<IRealProviderTestRecord>>();
                await repository.InitializeAsync();

                var saved = await repository.SaveAsync(new RealProviderTestRecord
                {
                    Key = "customer",
                    Value = "pending-before-restart"
                });
                Assert.IsNotNull(saved.Envelope);
                Assert.AreEqual("customer", saved.Envelope.EnvelopeCacheKey);
                Assert.IsNull(saved.Envelope.Location);
                Assert.IsNull(saved.Envelope.ExtensionMetadata);

                var preferred = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>(
                    "physical-preferred");
                var pending = ReadMetadata(
                    (await preferred.QueryFirstByStorageKeyAsync("customer"))!);
                Assert.AreNotEqual(Guid.Empty, pending.PendingMutationId);

                var peer = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>(
                    "physical-peer");
                Assert.IsNull(await peer.QueryFirstByStorageKeyAsync("customer"));
            }

            await using (var provider = CreateProvider(preferredRoot, peerRoot))
            {
                var repository = provider.GetRequiredService<IResilientRepository<IRealProviderTestRecord>>();
                await repository.InitializeAsync();
                var result = await repository.SynchronizeAsync("customer");
                Assert.AreEqual(1, result.PeerCommits);
                Assert.AreEqual(1, result.Acknowledgements);

                var preferred = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>(
                    "physical-preferred");
                var peer = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>(
                    "physical-peer");
                var preferredEntity = await preferred.QueryFirstByStorageKeyAsync("customer");
                var peerEntity = await peer.QueryFirstByStorageKeyAsync("customer");
                Assert.IsNotNull(preferredEntity);
                Assert.IsNotNull(peerEntity);
                Assert.AreEqual("pending-before-restart", peerEntity.Value);
                Assert.AreNotSame(preferredEntity.Envelope, peerEntity.Envelope);
                Assert.AreEqual("customer-0", preferredEntity.Envelope!.EnvelopeCacheKey);
                Assert.AreEqual("customer-0", peerEntity.Envelope!.EnvelopeCacheKey);
                StringAssert.StartsWith(
                    Path.GetFullPath(preferredEntity.Envelope.Location!),
                    Path.GetFullPath(preferredRoot));
                StringAssert.StartsWith(
                    Path.GetFullPath(peerEntity.Envelope!.Location!),
                    Path.GetFullPath(peerRoot));
                Assert.AreEqual(
                    ReadMetadata(preferredEntity).RevisionTimestampUtcMicroseconds,
                    ReadMetadata(peerEntity).RevisionTimestampUtcMicroseconds);

                Assert.IsTrue(await repository.DeleteAsync(preferredEntity));
                Assert.IsNull(await repository.QueryFirstByStorageKeyAsync("customer"));
                Assert.IsTrue(ReadMetadata(
                    (await preferred.QueryFirstByStorageKeyAsync("customer"))!).IsTombstone);
            }

            await using (var provider = CreateProvider(preferredRoot, peerRoot))
            {
                var repository = provider.GetRequiredService<IResilientRepository<IRealProviderTestRecord>>();
                await repository.InitializeAsync();
                var result = await repository.SynchronizeAsync("customer");
                Assert.AreEqual(1, result.PeerCommits);
                Assert.AreEqual(1, result.Acknowledgements);
                Assert.IsNull(await repository.QueryFirstByStorageKeyAsync("customer"));

                var peer = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>(
                    "physical-peer");
                var peerTombstone = await peer.QueryFirstByStorageKeyAsync("customer");
                Assert.IsNotNull(peerTombstone);
                Assert.IsTrue(ReadMetadata(peerTombstone).IsTombstone);
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateProvider(string preferredRoot, string peerRoot, bool notifications = false)
    {
        var preferredConfig = new TestContractConfig("preferred-config", preferredRoot);
        var peerConfig = new TestContractConfig("peer-config", peerRoot);
        var mapper = new RealProviderTestMapper();
        var storagePolicy = new StoragePolicy
        {
            DurabilityMode = StorageDurabilityMode.Durable,
            WatchStorageChanges = false,
            HydrateOnInitialize = false,
            RepositoryNotifications = notifications ? new() : null
        };
        var services = new ServiceCollection();

        services.AddRepository<IRealProviderTestRecord>(
            preferredConfig,
            "physical-preferred",
            storagePolicy: storagePolicy,
            storageIdentityFactory:
                new FlatFileStorageIdentityFactory<IRealProviderTestRecord, string>(
                    preferredConfig,
                    mapper),
            durabilityMode: StorageDurabilityMode.Durable);
        services.AddRepository<IRealProviderTestRecord>(
            peerConfig,
            "physical-peer",
            storagePolicy: storagePolicy,
            storageIdentityFactory:
                new FlatFileStorageIdentityFactory<IRealProviderTestRecord, string>(
                    peerConfig,
                    mapper),
            durabilityMode: StorageDurabilityMode.Durable);
        services.AddResilientRepository(
            resilientRepositoryKey: "logical-records",
            preferredRepositoryKey: "physical-preferred",
            peerRepositoryKey: "physical-peer",
            entityMapper: mapper,
            policy: new ResilientRepositoryOptions<IRealProviderTestRecord>
            {
                DatasetId = "real-provider-tests",
                RevisionCheckMode = RevisionCheckMode.PreferredOnly,
                MutationAuthority = MutationAuthority.PreferredWritable,
                EnableBackgroundSynchronization = false
            });

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task LegacyPrivateTimestampMovesIntoCoreWithoutDroppingDeliveryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-commercial-revision-" + Guid.NewGuid());
        var preferredRoot = Path.Combine(root, "preferred"); var peerRoot = Path.Combine(root, "peer");
        var pending = Guid.NewGuid(); var applied = Guid.NewGuid();
        var legacyMetadata = new ResilientEnvelopeMetadata(1, 123456789, false, pending, PendingMutationKind.Upsert, applied);
        string path;
        try
        {
            await using (var di = CreateProvider(preferredRoot, peerRoot))
            {
                var endpoint = (IEnvelopeMetadataRepository<IRealProviderTestRecord>)di.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-preferred");
                var saved = await endpoint.SaveWithExtensionMetadataAsync(new RealProviderTestRecord { Key = "legacy", Value = "v1" }, null,
                    ResilientEnvelopeMetadataCodec.Encode(legacyMetadata));
                path = saved.Envelope!.Location!;
            }
            var bytes = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path, "IDB5"u8.ToArray().Concat(bytes.AsSpan(12).ToArray()).ToArray());
            await using var provider = CreateProvider(preferredRoot, peerRoot);
            var repository = provider.GetRequiredService<IResilientRepository<IRealProviderTestRecord>>();
            await repository.InitializeAsync();
            var loaded = (await repository.QueryFirstByStorageKeyAsync("legacy"))!;
            Assert.AreEqual(123456789L, loaded.Envelope!.RevisionTimestampUtcMicroseconds);
            var preferred = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-preferred");
            var upgraded = (await preferred.QueryFirstByStorageKeyAsync("legacy"))!;
            var metadata = ReadMetadata(upgraded);
            Assert.AreEqual(pending, metadata.PendingMutationId); Assert.AreEqual(applied, metadata.LastAppliedMutationId);
            Assert.AreEqual(2, (int)metadata.FormatVersion);
            Assert.AreEqual(0L, ResilientEnvelopeMetadataCodec.Decode(upgraded.Envelope!.ExtensionMetadata).RevisionTimestampUtcMicroseconds);
            var synchronized = await repository.SynchronizeAsync("legacy");
            Assert.AreEqual(1, synchronized.Acknowledgements);
            var acknowledged = (await preferred.QueryFirstByStorageKeyAsync("legacy"))!;
            Assert.AreEqual(123456789L, acknowledged.Envelope!.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual(Guid.Empty, ReadMetadata(acknowledged).PendingMutationId);
            loaded.Value = "new";
            var next = await repository.SaveAsync(loaded);
            Assert.IsTrue(next.Envelope!.RevisionTimestampUtcMicroseconds > 123456789L);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SeparateLogicalApplicationCopiesCannotOverwriteEachOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-commercial-conflict-" + Guid.NewGuid());
        try
        {
            await using var di = CreateProvider(Path.Combine(root, "preferred"), Path.Combine(root, "peer"));
            var repo = di.GetRequiredService<IResilientRepository<IRealProviderTestRecord>>();
            await repo.SaveAsync(new RealProviderTestRecord { Key = "one", Value = "first" });
            var a = (await repo.QueryFirstByStorageKeyAsync("one"))!;
            var b = (await repo.QueryFirstByStorageKeyAsync("one"))!;
            a.Value = "winner"; b.Value = "stale";
            var saved = await repo.SaveAsync(a);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRealProviderTestRecord>>(() => repo.SaveAsync(b).AsTask());
            await repo.SynchronizeAsync("one");
            var afterAck = (await repo.QueryFirstByStorageKeyAsync("one"))!;
            Assert.AreEqual(saved.Envelope!.RevisionTimestampUtcMicroseconds, afterAck.Envelope!.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual("winner", afterAck.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ResilientEnvelopeMetadata ReadMetadata(IRealProviderTestRecord entity)
        => ResilientEnvelopeMetadataCodec.Decode(entity.Envelope!);

    private sealed class TestContractConfig(string databaseKey, string dataRoot)
        : IDbContractConfigDto<IRealProviderTestRecord>
    {
        public string EnvelopeDICacheKey { get; } = databaseKey;

        public string ContractName => "RealProviderTestRecords";

        public string FileExtension => ".mpk";

        public string? ImplementationsPath => null;

        public string? ContractPath => null;

        public string? MigrationsPath => null;

        public string? DataRoot { get; } = dataRoot;

        public string? ActiveVersion => "1.0.0";

        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;

        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

        public StoragePolicy? StoragePolicy => null;
    }
}

[StorageScope(VersionTag = "1.0.0")]
public interface IRealProviderTestRecord : IStoredContract<IRealProviderTestRecord>
{
    string Key { get; set; }

    string Value { get; set; }
}

public sealed class RealProviderTestRecord : IRealProviderTestRecord
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public IObjectEnvelope<IRealProviderTestRecord>? Envelope { get; set; }
}

public sealed class RealProviderTestMapper :
    IResilientEntityMapper<IRealProviderTestRecord>,
    IEnvelopeKeyFactory<IRealProviderTestRecord, string>
{
    public string GetStorageKey(IRealProviderTestRecord entity) => entity.Key;

    public string CreateKey(IRealProviderTestRecord instance) => GetStorageKey(instance);

    public IRealProviderTestRecord CreateDetachedCopy(IRealProviderTestRecord source)
        => new RealProviderTestRecord
        {
            Key = source.Key,
            Value = source.Value
        };

    public void CopyValues(IRealProviderTestRecord source, IRealProviderTestRecord target)
    {
        target.Key = source.Key;
        target.Value = source.Value;
    }
}
