using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    public async Task ProviderNativeMigration_ReadsContextBoundEncryptedSource()
    {
        using var root = new ProviderTemporaryDirectory();
        var options = ProviderOptions();
        var sourceConfig = new ProviderConfig(root.Path, activeVersion: "1.0.0");
        var sourceRoot = Path.Combine(root.Path, options.DirectoryName, "1.0.0");
        const string key = "encrypted-migration-0";
        var encryptor = new AesGcmEncryptor(new FixedKeyProvider());
        var serializer = new BinaryObjectSerializer<IProviderRecord>();
        var envelope = new FileObjectEnvelope<IProviderRecord>(sourceConfig)
        {
            EnvelopeCacheKey = key,
            VersionTag = "1.0.0",
            Location = key,
            ObjectInstance = new ProviderRecordMigration
            {
                Id = "encrypted-migration",
                Name = "Encrypted source",
                Score = 9
            }
        };
        var sourcePayload = ((IContextualEncryptor)encryptor).Encrypt(
            serializer.Serialize(envelope),
            StorageEncryptionContext.Create(sourceConfig, "segment-core", key));

        await using (var sourceStore = await SegmentCoreStore.OpenAsync(sourceRoot, options.StoreOptions))
        {
            await sourceStore.SaveAsync(key, sourcePayload);
            _ = await sourceStore.CheckpointAsync();
        }

        var targetConfig = new ProviderConfig(root.Path, activeVersion: "2.0.0");
        var services = new ServiceCollection();
        services.AddSingleton<IMigration, ProviderRecordMigration>();
        services.AddSegmentCoreRepository<IProviderRecord>(
            targetConfig,
            options,
            targetConfig.StoragePolicy,
            new ProviderIdentityFactory(),
            encryptor);
        await using var provider = services.BuildServiceProvider();
        var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();

        var result = await maintenance.MigrateFromAsync(sourceRoot, "1.0.0");

        Assert.AreEqual(1, result.Migrated);
        Assert.IsEmpty(result.Failures);
        var repository = provider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
        var migrated = (await repository.GetPhysicalAsync(key))?.ObjectInstance;
        Assert.IsNotNull(migrated);
        Assert.AreEqual("Encrypted source migrated", migrated.Name);
        Assert.AreEqual(109, migrated.Score);
    }

    [TestMethod]
    public async Task Repository_ReadWithHistoricalKey_RewritesRecordWithActiveContextualKey()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            HydrateOnInitialize = false,
            RaiseNotifications = false,
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();
        var storeRoot = Path.Combine(root.Path, options.DirectoryName, "1.0.0");
        const string key = "historical-key-0";
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["old"] = Enumerable.Repeat((byte)0x21, 32).ToArray(),
            ["active"] = Enumerable.Repeat((byte)0x42, 32).ToArray()
        };
        var context = StorageEncryptionContext.Create(config, "segment-core", key);
        var serializer = new BinaryObjectSerializer<IProviderRecord>();
        var envelope = new FileObjectEnvelope<IProviderRecord>(config)
        {
            EnvelopeCacheKey = key,
            VersionTag = "1.0.0",
            Location = key,
            ObjectInstance = new ProviderRecord
            {
                Id = "historical-key",
                Name = "Rotate me",
                Score = 17
            }
        };
        var oldEncryptor = new AesGcmEncryptor(new RotatingTestKeyProvider("old", keys));
        var oldPayload = ((IContextualEncryptor)oldEncryptor).Encrypt(serializer.Serialize(envelope), context);
        await using (var sourceStore = await SegmentCoreStore.OpenAsync(storeRoot, options.StoreOptions))
        {
            await sourceStore.SaveAsync(key, oldPayload);
            _ = await sourceStore.CheckpointAsync();
        }

        var activeEncryptor = new AesGcmEncryptor(
            new RotatingTestKeyProvider("active", keys),
            new() { RotationMode = EncryptionRotationMode.ReencryptOnRead });
        await using (var provider = BuildProvider(config, options, encryptor: activeEncryptor))
        {
            var repository = provider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            var read = await repository.GetPhysicalAsync(key);
            Assert.IsNotNull(read);
            Assert.AreEqual("Rotate me", read.ObjectInstance!.Name);
        }

        await using var reopenedStore = await SegmentCoreStore.OpenAsync(storeRoot, options.StoreOptions);
        var rotatedPayload = await reopenedStore.GetAsync(key);
        Assert.IsNotNull(rotatedPayload);
        Assert.AreEqual((byte)0x03, rotatedPayload[4]);
        var metadata = activeEncryptor.DecryptWithMetadata(rotatedPayload, context);
        Assert.AreEqual("active", metadata.KeyId);
        Assert.IsTrue(metadata.UsesCurrentEncryption);
        Assert.IsFalse(metadata.RequiresReencryption);
    }

    [TestMethod]
    public async Task Repository_ConfiguredPayloadLimitRejectsOversizedWriteAndRemainsUsable()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            MaximumStoredObjectBytes = 512,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repository = provider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => repository.WriteAsync(new ProviderRecord
        {
            Id = "too-large",
            Name = new string('x', 4_096),
            Score = 1
        }).AsTask());

        var saved = await repository.WriteAsync(new ProviderRecord
        {
            Id = "small",
            Name = "Small",
            Score = 2
        });
        Assert.AreEqual("Small", saved.Name);
    }

    private sealed class RotatingTestKeyProvider(
        string activeKeyId,
        IReadOnlyDictionary<string, byte[]> keys) : IRotatingKeyProvider
    {
        public ReadOnlyMemory<byte> GetCurrentKey() => GetKey(activeKeyId);

        public string GetActiveKeyId() => activeKeyId;

        public ReadOnlyMemory<byte> GetKey(string keyId) => keys.TryGetValue(keyId, out var key)
            ? key
            : throw new KeyNotFoundException(keyId);
    }
}
