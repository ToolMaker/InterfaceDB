using System.Security.Cryptography;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Migrations;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SecurityHardeningTests
{
    [TestMethod]
    public void EncryptedStorageFieldCannotBeSearchIndexed()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SearchIndexDiscovery.Discover(typeof(IIndexedSecret)));

        StringAssert.Contains(exception.Message, nameof(IIndexedSecret.Secret));
        StringAssert.Contains(exception.Message, "plaintext");
    }

    [TestMethod]
    public void TypeLevelSearchIndexExcludesEncryptedStorageFields()
    {
        var definitions = SearchIndexDiscovery.Discover(typeof(IImplicitlyIndexedSecret));

        Assert.AreEqual(1, definitions.Count);
        Assert.AreEqual(nameof(IImplicitlyIndexedSecret.Name), definitions[0].Path);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void NamedEncryptionWinsInEitherRegistrationOrder(
        bool segment,
        bool encryptionFirst)
    {
        var services = new ServiceCollection();
        var root = Path.Combine(Path.GetTempPath(), "idb-security-di-" + Guid.NewGuid().ToString("N"));
        var config = new Config(root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
        var key = new StaticKey(0x42);

        if (encryptionFirst)
        {
            services.AddInterfaceDbAesGcmEncryption("security", key);
        }

        if (segment)
        {
            services.AddSegmentCoreRepository(config);
        }
        else
        {
            services.AddFileRepository(config);
        }

        if (!encryptionFirst)
        {
            services.AddInterfaceDbAesGcmEncryption("security", key);
        }

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<AesGcmEncryptor>(
            provider.GetRequiredKeyedService<IEncryptor>("security"));
        Assert.IsInstanceOfType<EncryptingObjectSerializer<IRecord>>(
            provider.GetRequiredKeyedService<Idb.Libraries.Abstractions.Services.Serialization.IObjectSerializer<IRecord>>("security"));
    }

    [TestMethod]
    public void ContextBoundCiphertextRejectsPhysicalKeySubstitution()
    {
        var encryptor = new AesGcmEncryptor(new StaticKey(0x33));
        var plaintext = "repository secret"u8.ToArray();
        var first = "provider\0contract\0key-a"u8.ToArray();
        var second = "provider\0contract\0key-b"u8.ToArray();

        var cipher = encryptor.Encrypt(plaintext, first);

        CollectionAssert.AreEqual(plaintext, encryptor.Decrypt(cipher, first));
        _ = Assert.ThrowsExactly<CryptographicException>(() => encryptor.Decrypt(cipher, second));
        _ = Assert.ThrowsExactly<CryptographicException>(() => encryptor.Decrypt(cipher));
    }

    [TestMethod]
    public async Task FileRepositoryRejectsCiphertextCopiedToAnotherPhysicalKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-security-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = new StaticKey(0x34);
            string firstKey;
            string secondKey;
            using (var provider = CreateEncryptedFileProvider(root, key))
            {
                var repository = provider.GetRequiredService<IRepository<IRecord>>();
                var first = await repository.SaveAsync(new Record { Id = "first" });
                var second = await repository.SaveAsync(new Record { Id = "second" });
                firstKey = first.Envelope!.EnvelopeCacheKey;
                secondKey = second.Envelope!.EnvelopeCacheKey;
            }

            var versionRoot = Path.Combine(root, "1.0.0");
            File.Copy(
                Path.Combine(versionRoot, firstKey + ".mpk"),
                Path.Combine(versionRoot, secondKey + ".mpk"),
                overwrite: true);

            using var readerProvider = CreateEncryptedFileProvider(root, key);
            var envelopes = readerProvider.GetRequiredService<IEnvelopeRepository<IRecord>>();
            await envelopes.InitializeAsync();
            _ = await Assert.ThrowsExactlyAsync<CryptographicException>(
                () => envelopes.GetPhysicalAsync(secondKey).AsTask());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ContextlessCiphertextRequiresExplicitMigrationSwitch()
    {
        var key = new StaticKey(0x21);
        var legacy = new AesGcmEncryptor(key).Encrypt("legacy"u8);
        var strict = new AesGcmEncryptor(key);
        _ = Assert.ThrowsExactly<CryptographicException>(
            () => strict.Decrypt(legacy, "context"u8));

        var migration = new AesGcmEncryptor(
            key,
            new AesGcmEncryptorOptions { AllowContextlessPayloadMigration = true });
        var result = migration.DecryptWithMetadata(legacy, "context"u8);
        CollectionAssert.AreEqual("legacy"u8.ToArray(), result.Plaintext);
        Assert.IsTrue(result.RequiresReencryption);
    }

    [TestMethod]
    public async Task UnenrolledAssignableRuntimeTypeIsRejectedBeforeMaterializationAndWrite()
    {
        var hostile = new BinaryObjectSerializer<IRecord>(
            runtimeTypeAllowlist: new StoredRuntimeTypeAllowlist<IRecord>(typeof(SideEffectRecord)));
        var payload = hostile.Serialize(new Envelope<IRecord>
        {
            EnvelopeCacheKey = "hostile",
            VersionTag = "1.0.0",
            ObjectInstance = new SideEffectRecord { Id = "hostile" }
        });
        SideEffectRecord.SetterCalls = 0;

        var target = new BinaryObjectSerializer<IRecord>(
            runtimeTypeAllowlist: new StoredRuntimeTypeAllowlist<IRecord>(typeof(Record)));
        _ = Assert.ThrowsExactly<InvalidDataException>(() => target.Deserialize(payload));
        Assert.AreEqual(0, SideEffectRecord.SetterCalls);

        await using var source = new MemoryStream(payload, writable: false);
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => target.DeserializeAsync(source).AsTask());
        Assert.AreEqual(0, SideEffectRecord.SetterCalls);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => target.Serialize(new Envelope<IRecord>
        {
            EnvelopeCacheKey = "hostile",
            VersionTag = "1.0.0",
            ObjectInstance = new SideEffectRecord()
        }));
        await using var destination = new MemoryStream();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => target.SerializeAsync(
            new Envelope<IRecord>
            {
                EnvelopeCacheKey = "hostile",
                VersionTag = "1.0.0",
                ObjectInstance = new SideEffectRecord()
            },
            destination).AsTask());
        Assert.AreEqual(0L, destination.Length);
        Assert.AreEqual(0, SideEffectRecord.SetterCalls);
    }

    [TestMethod]
    public async Task SeekableStreamGrowthIsRejectedBeforeMaterialization()
    {
        var allowlist = new StoredRuntimeTypeAllowlist<IRecord>(typeof(SideEffectRecord));
        var payload = new BinaryObjectSerializer<IRecord>(runtimeTypeAllowlist: allowlist).Serialize(
            new Envelope<IRecord>
            {
                EnvelopeCacheKey = "growing",
                VersionTag = "1.0.0",
                ObjectInstance = new SideEffectRecord { Id = "growing" }
            });
        SideEffectRecord.SetterCalls = 0;
        var maximumPayloadBytes = payload.Length - 1;
        var target = new BinaryObjectSerializer<IRecord>(
            maximumPayloadBytes: maximumPayloadBytes,
            runtimeTypeAllowlist: allowlist);
        await using var source = new UnderreportedLengthStream(payload, maximumPayloadBytes);

        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => target.DeserializeAsync(source).AsTask());

        Assert.AreEqual(0, SideEffectRecord.SetterCalls);
    }

    [TestMethod]
    public async Task EncryptedNonSeekableStreamsRejectOversizedCiphertextBeforeDecryption()
    {
        var decryptor = new CountingEncryptor();
        var serializer = new EncryptingObjectSerializer<IRecord>(
            new BinaryObjectSerializer<IRecord>(
                maximumPayloadBytes: 32,
                runtimeTypeAllowlist: new StoredRuntimeTypeAllowlist<IRecord>(typeof(Record))),
            decryptor);
        var oversized = new byte[33];

        await using (var source = new NonSeekableReadStream(oversized))
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => serializer.DeserializeAsync(source).AsTask());
        }

        await using (var source = new NonSeekableReadStream(oversized))
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => serializer.ReadRevisionAsync(source).AsTask());
        }

        Assert.AreEqual(0, decryptor.DecryptCalls);
    }

    [TestMethod]
    public async Task RotationAndConditionalReplacementRejectOversizedFilesBeforeReadingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-security-maintenance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var policy = StoragePolicy.Default with
            {
                MaximumStoredObjectBytes = 64,
                WatchStorageChanges = false
            };
            var config = new Config(root, ContractRepositoryType.FileSystem, policy);
            var coordinator = new CoalescingFileWriteCoordinator<IRecord>(
                new SidecarFileWriteLock<IRecord>(policy),
                config,
                policy);
            var versionRoot = Path.Combine(root, "1.0.0");
            Directory.CreateDirectory(versionRoot);
            var payloadPath = Path.Combine(versionRoot, "oversized.mpk");
            await using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(65);
            }

            var encryptor = new CountingRotatingEncryptor();
            var rotation = new FileSystemEncryptionRotationOperation<IRecord>(
                config,
                encryptor,
                coordinator,
                policy);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => rotation.RotateAllAsync().AsTask());
            Assert.AreEqual(0, encryptor.DecryptCalls);

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => coordinator
                .ReplaceIfUnchangedAsync(payloadPath, ReadOnlyMemory<byte>.Empty, new byte[] { 1 })
                .AsTask());
            Assert.AreEqual(65L, new FileInfo(payloadPath).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MigrationRejectsOversizedLegacyPayloadBeforeDeserialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-security-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var policy = StoragePolicy.Default with
            {
                MaximumStoredObjectBytes = 64,
                WatchStorageChanges = false
            };
            var config = new Config(root, ContractRepositoryType.FileSystem, policy);
            var coordinator = new CoalescingFileWriteCoordinator<IRecord>(
                new SidecarFileWriteLock<IRecord>(policy),
                config,
                policy);
            var versionRoot = Path.Combine(root, "1.0.0");
            Directory.CreateDirectory(versionRoot);
            var payloadPath = Path.Combine(versionRoot, "oversized.mpk");
            await using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(65);
            }

            var migration = new ExposedMigrationStrategy(config, coordinator, policy);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => migration.MigrateFileForTestAsync(new FileInfo(payloadPath)).AsTask());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RetainedEditingBoundsCommittedCaptureAndArchiveRereads()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-security-retained-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int maximumPayloadBytes = 4096;
            var policy = StoragePolicy.Default with
            {
                MaximumStoredObjectBytes = maximumPayloadBytes,
                RetainedVersions = new RetainedVersionOptions(),
                WatchStorageChanges = false,
                HydrateOnInitialize = false
            };
            var services = new ServiceCollection();
            services.AddFileRepository(
                new Config(root, ContractRepositoryType.FileSystem, policy),
                runtimeTypeAllowlist: new StoredRuntimeTypeAllowlist<IRecord>(typeof(Record)));
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepository<IRecord>>();

            var archived = await repository.SaveAsync(new Record { Id = "archived" });
            await using var edit = await repository.BeginEditAsync(archived.Envelope!.EnvelopeCacheKey);
            var archivePath = Directory
                .EnumerateFiles(Path.Combine(root, ".idb-history"), "*.version", SearchOption.AllDirectories)
                .Single();
            await using (var archive = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                archive.WriteByte(0x00);
            }

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => edit.ReadOriginalAsync().AsTask());

            var live = await repository.SaveAsync(new Record { Id = "live" });
            await using (var stream = new FileStream(
                live.Envelope!.Location!,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(maximumPayloadBytes + 1L);
            }

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => repository.BeginEditAsync(live.Envelope.EnvelopeCacheKey).AsTask());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SerializerRejectsPayloadAboveConfiguredLimit()
    {
        var normal = new BinaryObjectSerializer<IRecord>();
        var payload = normal.Serialize(new Envelope<IRecord>
        {
            EnvelopeCacheKey = "row",
            VersionTag = "1.0.0",
            ObjectInstance = new Record { Id = "row" }
        });
        var bounded = new BinaryObjectSerializer<IRecord>(maximumPayloadBytes: payload.Length - 1);

        _ = Assert.ThrowsExactly<InvalidDataException>(() => bounded.Deserialize(payload));
    }

    [TestMethod]
    public async Task FileRepositoryRejectsOversizedPayloadBeforeDeserialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-security-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            var versionRoot = Path.Combine(root, "1.0.0");
            Directory.CreateDirectory(versionRoot);
            await using (var stream = new FileStream(
                Path.Combine(versionRoot, "oversized.mpk"),
                FileMode.CreateNew,
                FileAccess.Write))
            {
                stream.SetLength(1025);
            }

            var policy = StoragePolicy.Default with
            {
                HydrateOnInitialize = false,
                WatchStorageChanges = false,
                MaximumStoredObjectBytes = 1024
            };
            var services = new ServiceCollection();
            services.AddFileRepository(new Config(
                root,
                ContractRepositoryType.FileSystem,
                policy));
            using var provider = services.BuildServiceProvider();
            var envelopes = provider.GetRequiredService<IEnvelopeRepository<IRecord>>();
            await envelopes.InitializeAsync();

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => envelopes.GetPhysicalAsync("oversized").AsTask());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticKey(byte fill) : IKeyProvider
    {
        private readonly byte[] _key = Enumerable.Repeat(fill, 32).ToArray();
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }

    private static ServiceProvider CreateEncryptedFileProvider(string root, IKeyProvider key)
    {
        var services = new ServiceCollection();
        services.AddInterfaceDbAesGcmEncryption("security", key);
        services.AddFileRepository(new Config(root, ContractRepositoryType.FileSystem));
        return services.BuildServiceProvider();
    }

    [SearchIndex]
    private interface IIndexedSecret
    {
        [SearchIndex]
        [EncryptedStorageField]
        string Secret { get; set; }
    }

    [SearchIndex]
    private interface IImplicitlyIndexedSecret
    {
        string Name { get; set; }

        [EncryptedStorageField]
        string Secret { get; set; }
    }

    [StorageScope(VersionTag = "1.0.0")]
    public interface IRecord : IStoredContract<IRecord>
    {
        string Id { get; set; }
    }

    public sealed class Record : IRecord
    {
        public string Id { get; set; } = string.Empty;
        public IObjectEnvelope<IRecord>? Envelope { get; private set; }
    }

    public sealed class SideEffectRecord : IRecord
    {
        private string _id = string.Empty;
        public static int SetterCalls;
        public string Id { get => _id; set { SetterCalls++; _id = value; } }
        public IObjectEnvelope<IRecord>? Envelope { get; private set; }
    }

    private sealed class CountingEncryptor : IEncryptor
    {
        public int DecryptCalls { get; private set; }
        public byte[] Encrypt(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Decrypt(ReadOnlySpan<byte> cipher)
        {
            DecryptCalls++;
            return cipher.ToArray();
        }
    }

    private sealed class CountingRotatingEncryptor : IRotatingEncryptor
    {
        public int DecryptCalls { get; private set; }
        public byte[] Encrypt(ReadOnlySpan<byte> plain) => plain.ToArray();
        public byte[] Decrypt(ReadOnlySpan<byte> cipher)
        {
            DecryptCalls++;
            return cipher.ToArray();
        }

        public EncryptionDecryptionResult DecryptWithMetadata(ReadOnlySpan<byte> cipher)
        {
            DecryptCalls++;
            return new EncryptionDecryptionResult(cipher.ToArray(), null, false, true);
        }
    }

    private sealed class NonSeekableReadStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class UnderreportedLengthStream(byte[] payload, long reportedLength) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class ExposedMigrationStrategy : FileSystemMigrationStrategyBase<IRecord>
    {
        public ExposedMigrationStrategy(
            IDbContractConfigDto<IRecord> config,
            IFileWriteCoordinator coordinator,
            StoragePolicy policy)
            : base(
                config,
                new BinaryObjectSerializer<IRecord>(
                    maximumPayloadBytes: policy.NormalizedMaximumStoredObjectBytes,
                    runtimeTypeAllowlist: new StoredRuntimeTypeAllowlist<IRecord>(typeof(Record))),
                new FlatFileNameFactory<IRecord>(config),
                Array.Empty<IMigration>(),
                MigrationStrategyType.None,
                new EncryptionNone(),
                coordinator,
                policy)
        {
        }

        public ValueTask<IRecord?> MigrateFileForTestAsync(FileInfo file)
            => MigrateFileAsync(file, deleteLegacyOnSuccess: false, CancellationToken.None);
    }

    private sealed class Envelope<T> : IObjectEnvelope<T>
        where T : class, IStoredContract<T>
    {
        public long RevisionTimestampUtcMicroseconds { get; set; }
        public string EnvelopeCacheKey { get; set; } = string.Empty;
        public string VersionTag { get; set; } = string.Empty;
        public T? ObjectInstance { get; set; }
        public WeakReference<T>? ObjectInstanceWeakReference { get; set; }
        public string? ImplementationTypeName { get; set; }
        public bool CacheObjectInstance { get; set; }
        public IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; } = new Dictionary<string, EncryptedFieldValue>();
        public byte[]? ExtensionMetadata { get; set; }
        public string? Location { get; set; }
        public StorageObjectState State { get; set; }
    }

    private sealed record Config(
        string DataRoot,
        ContractRepositoryType RepositoryType,
        StoragePolicy? Policy = null)
        : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey => "security";
        public string ContractName => "security-record";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
        public StoragePolicy StoragePolicy => Policy ?? StoragePolicy.Default with
        {
            HydrateOnInitialize = false,
            WatchStorageChanges = false
        };
    }
}
