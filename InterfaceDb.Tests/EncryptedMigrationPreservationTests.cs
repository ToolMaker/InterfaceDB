using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Migrations;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;

namespace InterfaceDb.Tests;

[TestClass]
public sealed partial class EncryptedMigrationPreservationTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task MigrationRestoresFieldsBeforeUpAndDoesNotRestoreAgainAfterUpClearsThem(bool clear, bool contextual)
    {
        await Run(clear, corrupt: false, contextual);
    }

    [TestMethod]
    public async Task CorruptFieldFailsBeforeMigrationCanDiscardIt() => await Run(false, corrupt: true);

    private static async Task Run(bool clear, bool corrupt, bool contextual = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-FieldMigration", Guid.NewGuid().ToString("N"));
        try
        {
            var config = new Config(root);
            var encryptor = new AesGcmEncryptor(new Key(), new() { AllowContextlessPayloadMigration = true });
            var context = StorageEncryptionContext.Create(config, "file", "record");
            var field = contextual
                ? encryptor.Encrypt(MessagePackSerializer.Serialize("original"), StorageEncryptionContext.CreateField(context, nameof(ISecret.Secret)))
                : encryptor.Encrypt(MessagePackSerializer.Serialize("original"));
            if (corrupt) field[^1] ^= 1;
            var envelope = new FileObjectEnvelope<ISecret>
            {
                EnvelopeCacheKey = "record", VersionTag = "1.0.0", ExtensionMetadata = [1, 2],
                ObjectInstance = new First { Clear = clear },
                EncryptedFields = new Dictionary<string, EncryptedFieldValue>
                {
                    [nameof(ISecret.Secret)] = new() { Ciphertext = field, PropertyTypeName = typeof(string).AssemblyQualifiedName!, ProfileName = "default" }
                }
            };
            var payload = ((IContextualEncryptor)encryptor).Encrypt(new BinaryObjectSerializer<ISecret>().Serialize(envelope), context);
            async Task<ISecret?> Migrate()
            {
                bool handled = false;
                ISecret? external = null;
                MigrateExternal(payload, context, encryptor, ref handled, ref external);
                if (handled) return external;
                var path = Path.Combine(root, "1.0.0", "record.mpk");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, payload);
                var coordinator = new CoalescingFileWriteCoordinator<ISecret>(new SidecarFileWriteLock<ISecret>(config.StoragePolicy), config, config.StoragePolicy);
                return await new FileMigration(config, encryptor, coordinator).Run(new FileInfo(path));
            }
            if (corrupt)
            {
                var failure = await Assert.ThrowsExactlyAsync<MigrationExecutionException>(async () => await Migrate());
                Assert.IsInstanceOfType<System.Security.Cryptography.CryptographicException>(failure.InnerException);
            }
            else
            {
                var result = await Migrate();
                Assert.IsNotNull(result);
                Assert.AreEqual(clear ? null : "original-upgraded", result.Secret);
                Assert.AreEqual("second-saw:" + (clear ? "null" : "original-upgraded"), result.Observed);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static partial void MigrateExternal(byte[] payload, byte[] context, IEncryptor encryptor, ref bool handled, ref ISecret? result);

    private sealed class FileMigration(Config config, IEncryptor encryptor, IFileWriteCoordinator coordinator)
        : FileSystemMigrationStrategyBase<ISecret>(config, new BinaryObjectSerializer<ISecret>(), new FlatFileNameFactory<ISecret>(config),
            [new First(), new Second()], MigrationStrategyType.EagerStrict, encryptor, coordinator, config.StoragePolicy)
    {
        public ValueTask<ISecret?> Run(FileInfo file) => MigrateFileAsync(file, false, CancellationToken.None);
    }

    public interface ISecret : IStoredContract<ISecret>
    {
        [EncryptedStorageField] string? Secret { get; set; }
        bool Clear { get; set; }
        string Observed { get; set; }
    }
    public sealed class First : ISecret, IMigration
    {
        public string? Secret { get; set; }
        public bool Clear { get; set; }
        public string Observed { get; set; } = "";
        public IObjectEnvelope<ISecret>? Envelope { get; private set; }
        public string SourceVersion => "1.0.0";
        public string TargetVersion => "2.0.0";
        public bool Down() => false;
        public bool Up()
        {
            Assert.AreEqual("original", Secret, "Migration must see the original encrypted field.");
            Secret = Clear ? null : Secret + "-upgraded";
            return true;
        }
    }
    public sealed class Second : ISecret, IMigration
    {
        public string? Secret { get; set; }
        public bool Clear { get; set; }
        public string Observed { get; set; } = "";
        public IObjectEnvelope<ISecret>? Envelope { get; private set; }
        public string SourceVersion => "2.0.0";
        public string TargetVersion => "3.0.0";
        public bool Down() => false;
        public bool Up() { Observed = "second-saw:" + (Secret ?? "null"); return true; }
    }
    private sealed class Key : IKeyProvider
    {
        private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
    private sealed record Config(string DataRoot) : IDbContractConfigDto<ISecret>
    {
        public string EnvelopeDICacheKey => "migration-fields";
        public string ContractName => "migration-fields";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "3.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy StoragePolicy => StoragePolicy.Default with { WatchStorageChanges = false };
    }
}
