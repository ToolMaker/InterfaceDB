using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore.ObjectLog;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SegmentCoreMigrationRegressionTests
{
    private const string Key = "migration-record";
    private const string Secret = "original private value";
    private const long Revision = 1_700_000_000_000_000;

    [TestInitialize]
    public void ResetMigrationCounters()
    {
        SecretMigrationStepOne.UpCalls = 0;
        SecretMigrationStepOne.ClearSecret = false;
        SecretMigrationStepTwo.UpCalls = 0;
        ExplicitSecretMigration.UpCalls = 0;
        AmbiguousSecretMigration.UpCalls = 0;
    }

    [TestMethod]
    [DataRow("9.0.0")]
    [DataRow("invalid-version")]
    public async Task StoredEnvelopeVersionMismatchIsRejectedBeforeUpAndLeavesTargetEmpty(string storedVersion)
    {
        using var root = new TemporaryDirectory();
        var encryptor = Encryptor();
        var original = await SeedSourceAsync(root.Path, storedVersion, encryptor);
        await using (var provider = BuildProvider(root.Path, "2.0.0", encryptor))
        {
            var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationSecret>>();
            var result = await maintenance.MigrateFromAsync(SourceRoot(root.Path), "1.0.0", new() { ContinueOnError = true });

            Assert.AreEqual(1, result.Examined);
            Assert.AreEqual(0, result.Migrated);
            Assert.HasCount(1, result.Failures);
            Assert.AreEqual(Key, result.Failures[0].PhysicalKey);
            Assert.AreEqual(0, SecretMigrationStepOne.UpCalls);
            Assert.IsNull(result.Checkpoint);
            Assert.IsNull(await provider.GetRequiredService<IEnvelopeRepository<IMigrationSecret>>().GetPhysicalAsync(Key));
        }
        await AssertEmptyTargetAndUnchangedSourceAsync(root.Path, original);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EncryptedFieldIsAvailableToBothMigrationStepsAndPersistsAfterReopen(bool clearSecret)
    {
        using var root = new TemporaryDirectory();
        var encryptor = Encryptor();
        SecretMigrationStepOne.ClearSecret = clearSecret;
        var expectedSecret = clearSecret ? null : Secret + "|v2|v3";
        var expectedSecondInput = clearSecret ? "<null>" : Secret + "|v2";
        var original = await SeedSourceAsync(root.Path, "1.0.0", encryptor);
        await using (var provider = BuildProvider(root.Path, "3.0.0", encryptor))
        {
            var result = await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationSecret>>()
                .MigrateFromAsync(SourceRoot(root.Path), "1.0.0");
            Assert.AreEqual(1, result.Migrated);
            Assert.IsEmpty(result.Failures);
            Assert.AreEqual(1, SecretMigrationStepOne.UpCalls);
            Assert.AreEqual(1, SecretMigrationStepTwo.UpCalls);
        }

        await using (var reopened = BuildProvider(root.Path, "3.0.0", encryptor))
        {
            var envelope = await reopened.GetRequiredService<IEnvelopeRepository<IMigrationSecret>>().GetPhysicalAsync(Key);
            Assert.IsNotNull(envelope);
            Assert.IsNotNull(envelope.ObjectInstance);
            Assert.AreEqual(expectedSecret, envelope.ObjectInstance.Secret);
            Assert.AreEqual("first:" + Secret + ";second:" + expectedSecondInput, envelope.ObjectInstance.MigrationResult);
            Assert.AreEqual("3.0.0", envelope.VersionTag);
            Assert.AreEqual(Revision, envelope.RevisionTimestampUtcMicroseconds);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, envelope.ExtensionMetadata);
        }

        await using (var rawTarget = await SegmentCoreStore.OpenAsync(TargetRoot(root.Path, "3.0.0"), Options().StoreOptions))
        {
            var bytes = await rawTarget.GetAsync(Key);
            Assert.IsNotNull(bytes);
            var plain = ((IContextualEncryptor)encryptor).Decrypt(bytes,
                StorageEncryptionContext.Create(new RegressionConfig(root.Path, "3.0.0"), "segment-core", Key));
            var persisted = new BinaryObjectSerializer<IMigrationSecret>().Deserialize(plain);
            Assert.IsNull(persisted.ObjectInstance!.Secret, "The migrated secret must not become a plaintext object property on disk.");
            Assert.IsTrue(persisted.EncryptedFields.TryGetValue(nameof(IMigrationSecret.Secret), out var encrypted));
            Assert.AreEqual("secret-profile", encrypted.ProfileName);
            Assert.AreEqual(expectedSecret, MessagePackSerializer.Deserialize<string?>(encryptor.Decrypt(encrypted.Ciphertext)));
        }
        await using var source = await SegmentCoreStore.OpenAsync(SourceRoot(root.Path), Options().StoreOptions);
        CollectionAssert.AreEqual(original, await source.GetAsync(Key));
    }

    [TestMethod]
    [DataRow("wrong-key")]
    [DataRow("corrupt-ciphertext")]
    public async Task InvalidEncryptedFieldFailsBeforeUpWithoutCommittingTarget(string damage)
    {
        using var root = new TemporaryDirectory();
        var encryptor = Encryptor();
        // The outer envelope uses the correct key. Only the individual encrypted field is invalid.
        var original = await SeedSourceAsync(root.Path, "1.0.0", encryptor,
            damage == "wrong-key" ? Encryptor(0x72) : encryptor, damage == "corrupt-ciphertext");
        await using (var provider = BuildProvider(root.Path, "2.0.0", encryptor))
        {
            var result = await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationSecret>>()
                .MigrateFromAsync(SourceRoot(root.Path), "1.0.0", new() { ContinueOnError = true });
            Assert.AreEqual(0, result.Migrated);
            Assert.HasCount(1, result.Failures);
            Assert.AreEqual(0, SecretMigrationStepOne.UpCalls);
            Assert.IsNull(result.Checkpoint);
            Assert.IsNull(await provider.GetRequiredService<IEnvelopeRepository<IMigrationSecret>>().GetPhysicalAsync(Key));
        }
        await AssertEmptyTargetAndUnchangedSourceAsync(root.Path, original);
    }

    [TestMethod]
    public void HeaderlessPlainLegacyPayloadRetainsCallerSuppliedMigrationVersion()
    {
        var payload = MessagePackSerializer.Serialize(new ProviderRecordMigration { Id = "plain", Name = "Legacy", Score = 3 },
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance));
        var engine = new SegmentCoreMigrationEngine<IProviderRecord>("2.0.0", new EncryptionNone(), [new ProviderRecordMigration()]);

        var result = engine.Migrate("plain", "1.0.0", payload);

        Assert.AreEqual("Legacy migrated", result.Contract.Name);
        Assert.AreEqual(103, result.Contract.Score);
        Assert.IsNull(result.ExtensionMetadata);
    }

    [TestMethod]
    public async Task EncryptedFieldCanRestoreAnExplicitInterfacePropertyBeforeUp()
    {
        using var root = new TemporaryDirectory();
        var encryptor = Encryptor();
        var payload = await SeedSourceAsync(root.Path, "1.0.0", encryptor);
        var engine = new SegmentCoreMigrationEngine<IMigrationSecret>("2.0.0", encryptor, [new ExplicitSecretMigration()],
            key => StorageEncryptionContext.Create(new RegressionConfig(root.Path, "1.0.0"), "segment-core", key));

        var result = engine.Migrate(Key, "1.0.0", payload);

        Assert.AreEqual(1, ExplicitSecretMigration.UpCalls);
        Assert.AreEqual(Secret, result.Contract.Secret);
        Assert.AreEqual("explicit:" + Secret, result.Contract.MigrationResult);
    }

    [TestMethod]
    public async Task AmbiguousExplicitInterfaceFieldSettersAreRejectedBeforeUp()
    {
        using var root = new TemporaryDirectory();
        var encryptor = Encryptor();
        var payload = await SeedSourceAsync(root.Path, "1.0.0", encryptor);
        var engine = new SegmentCoreMigrationEngine<IMigrationSecret>("2.0.0", encryptor, [new AmbiguousSecretMigration()],
            key => StorageEncryptionContext.Create(new RegressionConfig(root.Path, "1.0.0"), "segment-core", key));

        var failure = Assert.ThrowsExactly<MigrationExecutionException>(() => engine.Migrate(Key, "1.0.0", payload));

        Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException.Message, "unambiguous writable property");
        Assert.AreEqual(0, AmbiguousSecretMigration.UpCalls);
    }

    private static async Task<byte[]> SeedSourceAsync(string root, string storedVersion, AesGcmEncryptor outerEncryptor,
        IEncryptor? fieldEncryptor = null, bool corruptField = false)
    {
        var ciphertext = (fieldEncryptor ?? outerEncryptor).Encrypt(MessagePackSerializer.Serialize(Secret));
        if (corruptField) ciphertext[^1] ^= 0x40;
        var sourceConfig = new RegressionConfig(root, "1.0.0");
        var envelope = new FileObjectEnvelope<IMigrationSecret>(sourceConfig)
        {
            EnvelopeCacheKey = Key, Location = Key, VersionTag = storedVersion,
            RevisionTimestampUtcMicroseconds = Revision, ExtensionMetadata = [7, 8, 9],
            ObjectInstance = new SecretMigrationStepOne { Id = Key, Secret = null, MigrationResult = "not migrated" },
            EncryptedFields = new Dictionary<string, EncryptedFieldValue>
            {
                [nameof(IMigrationSecret.Secret)] = new()
                {
                    ProfileName = "secret-profile", PropertyTypeName = typeof(string).AssemblyQualifiedName!, Ciphertext = ciphertext
                }
            }
        };
        var payload = ((IContextualEncryptor)outerEncryptor).Encrypt(new BinaryObjectSerializer<IMigrationSecret>().Serialize(envelope),
            StorageEncryptionContext.Create(sourceConfig, "segment-core", Key));
        await using var store = await SegmentCoreStore.OpenAsync(SourceRoot(root), Options().StoreOptions);
        await store.SaveAsync(Key, payload);
        await store.CheckpointAsync();
        return payload;
    }

    private static async Task AssertEmptyTargetAndUnchangedSourceAsync(string root, byte[] original)
    {
        await using (var target = await SegmentCoreStore.OpenAsync(TargetRoot(root, "2.0.0"), Options().StoreOptions))
        {
            Assert.AreEqual(0, target.Count);
            Assert.IsNull(await target.GetAsync(Key));
        }
        await using var source = await SegmentCoreStore.OpenAsync(SourceRoot(root), Options().StoreOptions);
        CollectionAssert.AreEqual(original, await source.GetAsync(Key));
    }

    private static ServiceProvider BuildProvider(string root, string version, IEncryptor encryptor)
    {
        var configuration = new RegressionConfig(root, version);
        var services = new ServiceCollection();
        services.AddSingleton<IMigration, SecretMigrationStepOne>();
        services.AddSingleton<IMigration, SecretMigrationStepTwo>();
        services.AddSegmentCoreRepository<IMigrationSecret>(configuration, Options(), configuration.StoragePolicy,
            new IdentityFactory(), encryptor);
        return services.BuildServiceProvider();
    }

    private static AesGcmEncryptor Encryptor(byte value = 0x31) => new(new FixedKeyProvider(value));
    private static string SourceRoot(string root) => Path.Combine(root, "source");
    private static string TargetRoot(string root, string version) => Path.Combine(root, "records", version);

    private static SegmentCoreRepositoryOptions Options() => new()
    {
        DirectoryName = "records", HardwareProfile = StorageHardwareProfile.Custom,
        EnableBackgroundMaintenance = false, CheckpointOnDispose = true,
        StoreOptions = new()
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            LogOptions = new()
            {
                StripeCount = 4, MaximumBatchRecords = 64, MaxSegmentBytes = 64 * 1024,
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            }
        }
    };

    private sealed class FixedKeyProvider(byte fill) : IKeyProvider
    {
        private readonly byte[] _key = Enumerable.Repeat(fill, 32).ToArray();
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }

    private sealed class IdentityFactory : IEnvelopeStorageIdentityFactory<IMigrationSecret>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<IMigrationSecret> envelope)
        {
            envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id;
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }

    private sealed class RegressionConfig(string root, string version) : IDbContractConfigDto<IMigrationSecret>
    {
        public string EnvelopeDICacheKey => "migration-regression";
        public string ContractName => "MigrationSecret";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => version;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly, HydrateOnInitialize = false, RaiseNotifications = false
        };
    }

    public interface IMigrationSecret : IStoredContract<IMigrationSecret>
    {
        string Id { get; set; }
        [EncryptedStorageField("secret-profile")]
        string? Secret { get; set; }
        string MigrationResult { get; set; }
    }

    [MessagePackObject]
    public class SecretMigrationStepOne : IMigrationSecret, IMigration
    {
        public static int UpCalls;
        public static bool ClearSecret;
        [Key("identifier")] public string Id { get; set; } = string.Empty;
        [Key("sealed-value")] public string? Secret { get; set; }
        [Key("migration-result")] public string MigrationResult { get; set; } = string.Empty;
        [IgnoreMember] public IObjectEnvelope<IMigrationSecret>? Envelope { get; protected set; }
        [IgnoreMember] public virtual string SourceVersion => "1.0.0";
        [IgnoreMember] public virtual string TargetVersion => "2.0.0";
        public virtual bool Up()
        {
            UpCalls++;
            MigrationResult = "first:" + Secret;
            Secret = ClearSecret ? null : Secret + "|v2";
            return true;
        }
        public bool Down() => throw new NotSupportedException();
    }

    [MessagePackObject]
    public sealed class SecretMigrationStepTwo : SecretMigrationStepOne
    {
        public new static int UpCalls;
        [IgnoreMember] public override string SourceVersion => "2.0.0";
        [IgnoreMember] public override string TargetVersion => "3.0.0";
        public override bool Up()
        {
            UpCalls++;
            MigrationResult += ";second:" + (Secret ?? "<null>");
            Secret = Secret is null ? null : Secret + "|v3";
            return true;
        }
    }

    [MessagePackObject(AllowPrivate = true)]
    public sealed class ExplicitSecretMigration : IMigrationSecret, IMigration
    {
        public static int UpCalls;
        [Key("identifier")] public string Id { get; set; } = string.Empty;
        [IgnoreMember] string? IMigrationSecret.Secret { get; set; }
        [Key("migration-result")] public string MigrationResult { get; set; } = string.Empty;
        [IgnoreMember] public IObjectEnvelope<IMigrationSecret>? Envelope { get; private set; }
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up()
        {
            UpCalls++;
            MigrationResult = "explicit:" + ((IMigrationSecret)this).Secret;
            return true;
        }
        public bool Down() => throw new NotSupportedException();
    }

    public interface IAlternateSecret
    {
        string? Secret { get; set; }
    }

    [MessagePackObject(AllowPrivate = true)]
    public sealed class AmbiguousSecretMigration : IMigrationSecret, IAlternateSecret, IMigration
    {
        public static int UpCalls;
        [Key("identifier")] public string Id { get; set; } = string.Empty;
        [IgnoreMember] string? IMigrationSecret.Secret { get; set; }
        [IgnoreMember] string? IAlternateSecret.Secret { get; set; }
        [Key("migration-result")] public string MigrationResult { get; set; } = string.Empty;
        [IgnoreMember] public IObjectEnvelope<IMigrationSecret>? Envelope { get; private set; }
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up() { UpCalls++; return true; }
        public bool Down() => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "InterfaceDb.MigrationRegressionTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
