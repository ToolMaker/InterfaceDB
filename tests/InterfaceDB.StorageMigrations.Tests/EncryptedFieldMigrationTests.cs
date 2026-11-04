using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

[TestClass]
public sealed class EncryptedFieldMigrationTests
{
    [TestMethod]
    [DataRow("1.0.0", false)]
    [DataRow("2.0.0", false)]
    [DataRow("1.0.0", true)]
    [DataRow("2.0.0", true)]
    public async Task EncryptedFields_RebindToDestinationProviderKeyAndVersion_WithoutChangingSource(string destinationVersion, bool legacy)
    {
        var scratch = OperatingSystem.IsWindows() ? @"C:\Temp\InterfaceDBStorageMigrationTests\fields" : Path.Combine(Path.GetTempPath(), "InterfaceDBStorageMigrationTests", "fields");
        var root = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = new FieldConfig("source", Path.Combine(root, "file"), "1.0.0");
            var segment = new FieldConfig("destination", Path.Combine(root, "segment"), destinationVersion);
            long revision;
            var sourceServices = new ServiceCollection();
            sourceServices.AddKeyedSingleton<IEncryptor>("source", new AesGcmEncryptor(new RotatingKeys("old")));
            sourceServices.AddFileRepository<IFieldRecord>(file);
            await using (var source = sourceServices.BuildServiceProvider())
            {
                var stored = await source.GetRequiredService<IEnvelopeMetadataRepository<IFieldRecord>>()
                    .SaveWithExtensionMetadataAsync(new FieldRecord { Secret = "secret-field-value", Number = 79 }, "original-15", new byte[] { 8 },
                        new EnvelopeWriteCondition(0, ExpectedMissing: true));
                revision = stored.Envelope!.RevisionTimestampUtcMicroseconds;
            }
            var sourcePayload = Directory.GetFiles(file.DataRoot, "*.mpk", SearchOption.AllDirectories).Single();
            if (legacy)
            {
                var serializer = new EncryptingObjectSerializer<IFieldRecord>(new BinaryObjectSerializer<IFieldRecord>(), new AesGcmEncryptor(new RotatingKeys("old")));
                var context = StorageEncryptionContext.Create(file, "file", "original-15");
                var envelope = serializer.Deserialize(await File.ReadAllBytesAsync(sourcePayload), context);
                envelope.RevisionTimestampUtcMicroseconds = 0;
                await File.WriteAllBytesAsync(sourcePayload, serializer.Serialize(envelope, context));
            }
            var before = await File.ReadAllBytesAsync(sourcePayload);
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IEncryptor>("source", new AesGcmEncryptor(new RotatingKeys("new"),
                new AesGcmEncryptorOptions { RotationMode = EncryptionRotationMode.ReencryptOnRead }));
            services.AddFileRepository<IFieldRecord>(file);
            var destinationEncryptor = new AesGcmEncryptor(new Key(77));
            services.AddSegmentCoreRepository<IFieldRecord>(segment, new() { EnableBackgroundMaintenance = false }, encryptor: destinationEncryptor);
            services.AddStorageMigration<IFieldRecord>("fields", sp => new StorageMigration<IFieldRecord>(new()
                { MigrationId = "fields-to-segment", RunningVersion = destinationVersion,
                    LegacyRevisions = legacy ? LegacyRevisionHandling.RepairOnStartup : LegacyRevisionHandling.Reject },
                StorageMigrationEndpointFactory.FileSource<IFieldRecord>(sp, "source", PlatformFilePublication.Instance),
                StorageMigrationEndpointFactory.LocalDestination<IFieldRecord>(sp, "destination",
                    Path.Combine(segment.DataRoot, ".segment-core", destinationVersion), PlatformFilePublication.Instance, async ct =>
                    {
                        var maintenance = sp.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<IFieldRecord>>("destination");
                        await maintenance.CheckpointAsync(ct);
                        if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid)
                            throw new InvalidDataException("Destination integrity check failed.");
                    }), new FieldUpgrade(),
                new SerializerStorageMigrationFingerprint<IFieldRecord>(sp.GetRequiredKeyedService<BinaryObjectSerializer<IFieldRecord>>("destination"), "binary-v1"),
                new FileStorageMigrationStateStore(Path.Combine(root, "receipt"), "fields", destinationEncryptor, PlatformFilePublication.Instance),
                revisionRepair: new TimestampStorageMigrationRevisionRepair<IFieldRecord>(new MonotonicRevisionTimestampGenerator())));
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepository<IFieldRecord>>();
            var moved = await repository.QueryFirstAsync(_ => true);
            Assert.IsNotNull(moved);
            Assert.AreEqual("secret-field-value", moved.Secret);
            Assert.AreEqual(79, moved.Number);
            Assert.AreEqual("original-15", moved.Envelope!.EnvelopeCacheKey);
            if (legacy) Assert.IsTrue(moved.Envelope.RevisionTimestampUtcMicroseconds > revision);
            else Assert.AreEqual(revision, moved.Envelope.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual(destinationVersion, moved.Envelope.VersionTag);
            Assert.IsTrue(moved.Envelope.EncryptedFields.ContainsKey(nameof(IFieldRecord.Secret)));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(sourcePayload));
            moved.Secret = "edited-after-migration";
            await repository.SaveAsync(moved);
            Assert.AreEqual("edited-after-migration", (await repository.QueryFirstAsync(_ => true))!.Secret);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            Assert.IsTrue(resolved.StartsWith(Path.GetFullPath(scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            DeleteOwned(new DirectoryInfo(resolved));
        }
    }

    private static void DeleteOwned(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateFileSystemInfos())
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) child.Delete();
            else if (child is DirectoryInfo nested) DeleteOwned(nested);
            else child.Delete();
        directory.Delete();
    }
    private sealed class Key(int value) : IKeyProvider
    { public ReadOnlyMemory<byte> GetCurrentKey() => Enumerable.Repeat((byte)value, 32).ToArray(); }
    private sealed class RotatingKeys(string active) : IRotatingKeyProvider
    {
        public string GetActiveKeyId() => active;
        public ReadOnlyMemory<byte> GetCurrentKey() => GetKey(active);
        public ReadOnlyMemory<byte> GetKey(string keyId) => Enumerable.Repeat(keyId switch
        { "old" => (byte)41, "new" => (byte)42, _ => throw new InvalidOperationException("Unknown test key.") }, 32).ToArray();
    }
    private sealed class FieldUpgrade : IStorageMigrationTransform<IFieldRecord>
    {
        public string Identity => "fields-compatible-v1-v2";
        public IFieldRecord Transform(IFieldRecord source, string sourceVersion, string destinationVersion) => source;
    }
    private sealed class FieldConfig(string key, string root, string version) : IDbContractConfigDto<IFieldRecord>
    {
        public string EnvelopeDICacheKey => key;
        public string ContractName => "FieldRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string DataRoot => root;
        public string ActiveVersion => version;
        public ContractRepositoryType RepositoryType => key == "source" ? ContractRepositoryType.FileSystem : ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
        { HydrateOnInitialize = false, WatchStorageChanges = false };
    }
}

[StorageScope(VersionTag = "1.0.0")]
public interface IFieldRecord : IStoredContract<IFieldRecord>
{
    [EncryptedStorageField] string? Secret { get; set; }
    [EncryptedStorageField] int Number { get; set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class FieldRecord : IFieldRecord
{
    public string? Secret { get; set; }
    public int Number { get; set; }
    [IgnoreMember] public IObjectEnvelope<IFieldRecord>? Envelope { get; private set; }
}
