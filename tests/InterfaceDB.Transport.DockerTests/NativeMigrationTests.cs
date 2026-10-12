using System.Security.Cryptography;
using Idb.Transport;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.DockerTests;

/// <summary>
/// Explicit test orchestration: export Docker-hosted bytes, run the real public native migration
/// against an isolated local copy, and upload a verified result. This is not a remote migration API.
/// </summary>
[TestClass]
[TestCategory("Docker")]
[DoNotParallelize]
public sealed class NativeMigrationTests
{
    private string _workRoot = null!;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _workRoot = Path.Combine(Path.GetTempPath(), "InterfaceDB.NativeMigrationDockerTests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workRoot)) Directory.Delete(_workRoot, true);
    }

    [TestMethod]
    public async Task DockerExportNativeChainMigrationAndV3UploadPreserveIdentityRevisionMetadataAndSource()
    {
        await using var source = await DockerStoreFixture.StartAsync(Manifest("1.0.0"));
        var original = new Dictionary<string, byte[]>
        {
            ["first"] = SerializeRecord("first", 7, 41001),
            ["second"] = SerializeRecord("second", 9, 41002)
        };
        await UploadAsync(source, original);
        var sourcePath = Local("source");
        await ExportAsync(source, sourcePath);
        var targetConfig = new MigrationConfig<IMigrationRecord>(Local("target"), "3.0.0");
        await using (var provider = BuildProvider(targetConfig, record => record.Id, [new ToV2(), new ToV3()]))
        {
            var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>();
            var migrated = await maintenance.MigrateFromAsync(sourcePath, "1.0.0", new() { BatchSize = 1 });
            Assert.AreEqual(2, migrated.Migrated);
            Assert.IsEmpty(migrated.Failures);
            Assert.IsTrue(migrated.Checkpoint.HasValue);
            var envelopes = provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>();
            foreach (var pair in original)
            {
                var old = new BinaryObjectSerializer<IMigrationRecord>().Deserialize(pair.Value);
                var current = await envelopes.GetPhysicalAsync(pair.Key);
                Assert.IsNotNull(current);
                Assert.AreEqual(old.ObjectInstance!.Amount + 1100, current.ObjectInstance!.Amount);
                Assert.AreEqual("3.0.0", current.VersionTag);
                Assert.AreEqual(old.RevisionTimestampUtcMicroseconds, current.RevisionTimestampUtcMicroseconds);
                CollectionAssert.AreEqual(old.ExtensionMetadata, current.ExtensionMetadata);
            }
            var resumed = await maintenance.MigrateFromAsync(sourcePath, "1.0.0");
            Assert.AreEqual(0, resumed.Migrated);
            Assert.AreEqual(2, resumed.AlreadyPresent);
        }
        await AssertSourcePreservedAsync(source, sourcePath, original);
        var migratedBytes = await ReadLocalAsync(targetConfig.StorePath);
        await using var destination = await DockerStoreFixture.StartAsync(Manifest("3.0.0"));
        await UploadAsync(destination, migratedBytes);
        await destination.RecreateAsync();
        var remote = await ReadRemoteAsync(destination);
        Assert.AreEqual(2, remote.Count);
        foreach (var pair in migratedBytes) CollectionAssert.AreEqual(pair.Value, remote[pair.Key]);
        TestContext.WriteLine("Real IMigration 1->2->3 chain completed; source hashes, revisions and extension metadata preserved; v3 bytes survived Docker container recreation.");
    }

    [TestMethod]
    public async Task DockerExportFailureKeepsSourceAndEarlierCommittedBatchThenResumes()
    {
        await using var source = await DockerStoreFixture.StartAsync(Manifest("1.0.0"));
        var original = Enumerable.Range(1, 3).ToDictionary(n => "record-" + n, n => SerializeRecord("record-" + n, n, 42000 + n));
        await UploadAsync(source, original);
        var sourcePath = Local("failure-source");
        await ExportAsync(source, sourcePath);
        var target = new MigrationConfig<IMigrationRecord>(Local("failure-target"), "2.0.0");
        FailSecondToV2.Reset(failOnCall: 2);
        await using (var provider = BuildProvider(target, record => record.Id, [new FailSecondToV2()]))
        {
            var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>();
            await Assert.ThrowsExactlyAsync<MigrationExecutionException>(async () =>
                await maintenance.MigrateFromAsync(sourcePath, "1.0.0", new() { BatchSize = 1 }));
            var envelopes = provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>();
            var committed = new List<IObjectEnvelope<IMigrationRecord>>();
            await foreach (var envelope in envelopes.EnumerateAsync()) committed.Add(envelope);
            Assert.HasCount(1, committed, "Migration commits batches; failure must not be described as a whole-store rollback.");
        }
        // Resume with a newly opened provider so only durable state can identify completed work.
        FailSecondToV2.Reset(failOnCall: int.MaxValue);
        await using (var provider = BuildProvider(target, record => record.Id, [new FailSecondToV2()]))
        {
            var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>();
            var envelopes = provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>();
            var resumed = await maintenance.MigrateFromAsync(sourcePath, "1.0.0", new() { BatchSize = 1 });
            Assert.AreEqual(1, resumed.AlreadyPresent);
            Assert.AreEqual(2, resumed.Migrated);
            foreach (var pair in original)
            {
                var old = new BinaryObjectSerializer<IMigrationRecord>().Deserialize(pair.Value);
                var current = await envelopes.GetPhysicalAsync(pair.Key);
                Assert.AreEqual(old.ObjectInstance!.Amount + 100, current!.ObjectInstance!.Amount, "Resume must not apply Up again to committed keys.");
            }
        }
        await AssertSourcePreservedAsync(source, sourcePath, original);
        TestContext.WriteLine("Injected second-object Up failure left one acknowledged target batch; a freshly reopened provider skipped that key and completed remaining records. Source bytes remained unchanged.");
    }

    [TestMethod]
    public async Task DockerExportMissingAndAmbiguousNativeChainsRejectWithoutSourceChanges()
    {
        await using var source = await DockerStoreFixture.StartAsync(Manifest("1.0.0"));
        var original = new Dictionary<string, byte[]> { ["record"] = SerializeRecord("record", 5, 43001) };
        await UploadAsync(source, original);
        var sourcePath = Local("invalid-chain-source");
        await ExportAsync(source, sourcePath);
        var missingConfig = new MigrationConfig<IMigrationRecord>(Local("missing-target"), "3.0.0");
        await using (var provider = BuildProvider(missingConfig, record => record.Id, [new ToV2()]))
            await Assert.ThrowsExactlyAsync<MissingMigrationException>(async () =>
                await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>().MigrateFromAsync(sourcePath, "1.0.0"));
        var ambiguousConfig = new MigrationConfig<IMigrationRecord>(Local("ambiguous-target"), "2.0.0");
        await using (var provider = BuildProvider(ambiguousConfig, record => record.Id, [new ToV2(), new FailSecondToV2()]))
            await Assert.ThrowsExactlyAsync<AmbiguousMigrationException>(async () =>
                await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>().MigrateFromAsync(sourcePath, "1.0.0"));
        Assert.AreEqual(0, (await ReadLocalAsync(missingConfig.StorePath)).Count);
        Assert.AreEqual(0, (await ReadLocalAsync(ambiguousConfig.StorePath)).Count);
        await AssertSourcePreservedAsync(source, sourcePath, original);
    }

    [TestMethod]
    public async Task NativeMigrationRejectsEnvelopeVersionMismatchWithoutTransformingSource()
    {
        await using var source = await DockerStoreFixture.StartAsync(Manifest("1.0.0"));
        // Opaque storage can hold v2 envelopes under a host whose compatibility manifest still says v1.
        var original = new Dictionary<string, byte[]> { ["already-v2"] = SerializeRecord("already-v2", 107, 44001, "2.0.0") };
        await UploadAsync(source, original);
        var sourcePath = Local("version-source");
        await ExportAsync(source, sourcePath);
        var target = new MigrationConfig<IMigrationRecord>(Local("version-target"), "2.0.0");
        await using (var provider = BuildProvider(target, record => record.Id, [new ToV2()]))
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>()
                    .MigrateFromAsync(sourcePath, "1.0.0"));
            var envelope = await provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>().GetPhysicalAsync("already-v2");
            Assert.IsNull(envelope, "A stored v2 envelope must not be transformed as v1 or committed to the target.");
        }
        Assert.AreEqual(0, (await ReadLocalAsync(target.StorePath)).Count);
        await AssertSourcePreservedAsync(source, sourcePath, original);
        TestContext.WriteLine("Stored VersionTag=2.0.0 with caller sourceVersion=1.0.0 was rejected; target stayed empty and source bytes stayed unchanged.");
    }

    [TestMethod]
    public async Task NativeMigrationReadsAndPreservesEncryptedPropertyAcrossTargetReopen()
    {
        const string key = "secret-record";
        const string secret = "preserve-this-private-value";
        long sourceRevision;
        var encryptor = new AesGcmEncryptor(new FixtureKeyProvider());
        var originalConfig = new MigrationConfig<ISecretRecord>(Local("typed-secret-source"), "1.0.0");
        await using (var originalProvider = BuildProvider(originalConfig, record => record.Id, [], encryptor))
        {
            var stored = await originalProvider.GetRequiredService<IEnvelopeRepository<ISecretRecord>>()
                .WriteAsync(new SecretMigration { Id = key, Amount = 7, Secret = secret });
            Assert.AreEqual(secret, stored.Secret);
            Assert.HasCount(1, stored.Envelope!.EncryptedFields);
            sourceRevision = stored.Envelope.RevisionTimestampUtcMicroseconds;
        }
        var original = await ReadLocalAsync(originalConfig.StorePath);
        await using var source = await DockerStoreFixture.StartAsync(Manifest("1.0.0") with { ContractIdentity = "ISecretRecord" });
        await UploadAsync(source, original);
        var sourcePath = Local("secret-export-source");
        await ExportAsync(source, sourcePath);
        var target = new MigrationConfig<ISecretRecord>(Local("secret-target"), "2.0.0");
        await using (var provider = BuildProvider(target, record => record.Id, [new SecretMigration()], encryptor))
        {
            var result = await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<ISecretRecord>>()
                .MigrateFromAsync(sourcePath, "1.0.0");
            var envelope = await provider.GetRequiredService<IEnvelopeRepository<ISecretRecord>>().GetPhysicalAsync(key);
            Assert.AreEqual(1, result.Migrated);
            Assert.AreEqual(107 + secret.Length, envelope!.ObjectInstance!.Amount,
                "Up must observe the original plaintext encrypted field, not a missing/default value.");
            Assert.AreEqual(secret, envelope.ObjectInstance.Secret);
            Assert.AreEqual(sourceRevision, envelope.RevisionTimestampUtcMicroseconds);
            Assert.HasCount(1, envelope.EncryptedFields);
        }
        await using (var reopenedTarget = BuildProvider(target, record => record.Id, [new SecretMigration()], encryptor))
        {
            var persisted = await reopenedTarget.GetRequiredService<IEnvelopeRepository<ISecretRecord>>().GetPhysicalAsync(key);
            Assert.AreEqual(secret, persisted!.ObjectInstance!.Secret);
            Assert.AreEqual(107 + secret.Length, persisted.ObjectInstance.Amount);
            Assert.AreEqual(sourceRevision, persisted.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual("2.0.0", persisted.VersionTag);
        }
        await AssertSourcePreservedAsync(source, sourcePath, original);
        await using var reopenedSource = BuildProvider(originalConfig, record => record.Id, [], encryptor);
        var preserved = await reopenedSource.GetRequiredService<IEnvelopeRepository<ISecretRecord>>().GetPhysicalAsync(key);
        Assert.AreEqual(secret, preserved!.ObjectInstance!.Secret);
        TestContext.WriteLine("Up read the original encrypted property; target preserved its value and source revision across provider reopen. Source ciphertext and plaintext stayed intact.");
    }

    private string Local(string name) => Path.Combine(_workRoot, name);

    private static byte[] SerializeRecord(string key, int amount, long revision, string version = "1.0.0")
        => new BinaryObjectSerializer<IMigrationRecord>().Serialize(new FileObjectEnvelope<IMigrationRecord>
        {
            EnvelopeCacheKey = key, VersionTag = version, RevisionTimestampUtcMicroseconds = revision,
            ObjectInstance = new MigrationRecord { Id = key, Amount = amount }, ExtensionMetadata = [0x31, 0x77, 0x05]
        });

    private static ServiceProvider BuildProvider<TContract>(MigrationConfig<TContract> config,
        Func<TContract, string> key, IEnumerable<IMigration> migrations, IEncryptor? encryptor = null)
        where TContract : class, IStoredContract<TContract>
    {
        var services = new ServiceCollection();
        foreach (var migration in migrations) services.AddSingleton<IMigration>(migration);
        services.AddSegmentCoreRepository(config, new()
        {
            DirectoryName = "segment-core", HardwareProfile = StorageHardwareProfile.Custom,
            EnableBackgroundMaintenance = false, CheckpointOnDispose = true,
            StoreOptions = new SegmentCoreOptions()
        }, config.StoragePolicy, new FixtureIdentity<TContract>(key), encryptor);
        return services.BuildServiceProvider();
    }

    private static async Task UploadAsync(DockerStoreFixture fixture, IReadOnlyDictionary<string, byte[]> records)
    {
        await using var client = await fixture.ConnectAsync();
        await client.ApplyBatchAsync(records.Select(record => new EncodedMutation(record.Key, record.Value)).ToArray());
    }

    private static async Task<Dictionary<string, byte[]>> ReadRemoteAsync(DockerStoreFixture fixture)
    {
        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var client = await fixture.ConnectAsync();
        string? after = null;
        do
        {
            var page = await client.EnumerateAsync(32, after);
            foreach (var record in page.Records!) records.Add(record.Key, record.Payload);
            after = page.NextAfterKey;
        } while (after is not null);
        return records;
    }

    private static async Task ExportAsync(DockerStoreFixture fixture, string localPath)
    {
        var records = await ReadRemoteAsync(fixture);
        await using var source = await SegmentCoreStore.OpenAsync(localPath);
        foreach (var record in records) await source.SaveAsync(record.Key, record.Value);
    }

    private static async Task<Dictionary<string, byte[]>> ReadLocalAsync(string path)
    {
        await using var store = await SegmentCoreStore.OpenAsync(path);
        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var record in store.StreamAllAsync()) records.Add(record.Key, record.Payload);
        return records;
    }

    private static async Task AssertSourcePreservedAsync(DockerStoreFixture fixture, string localPath, IReadOnlyDictionary<string, byte[]> expected)
    {
        var local = await ReadLocalAsync(localPath);
        var remote = await ReadRemoteAsync(fixture);
        Assert.AreEqual(expected.Count, local.Count);
        Assert.AreEqual(expected.Count, remote.Count);
        foreach (var record in expected)
        {
            CollectionAssert.AreEqual(SHA256.HashData(record.Value), SHA256.HashData(local[record.Key]), "Exported source changed during migration.");
            CollectionAssert.AreEqual(SHA256.HashData(record.Value), SHA256.HashData(remote[record.Key]), "Docker source changed during migration.");
        }
    }

    private static CompatibilityManifest Manifest(string version) => new()
    {
        ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0", EnvelopeFormat = "IDB6-IDBE",
        ContractIdentity = "IMigrationRecord", ContractVersion = version, ContractSchemaHash = "schema-" + version,
        ImplementationManifestHash = "native-migration-fixtures", IndexDefinitionHash = "none"
    };

    private sealed class MigrationConfig<TContract>(string root, string version) : IDbContractConfigDto<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public string EnvelopeDICacheKey => "native-docker-migration-" + typeof(TContract).Name;
        public string ContractName => typeof(TContract).Name;
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => version;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public StoragePolicy StoragePolicy => StoragePolicy.Default with
        {
            HydrateOnInitialize = false, RaiseNotifications = false, ObjectCacheMode = StorageObjectCacheMode.MetadataOnly
        };
        internal string StorePath => Path.Combine(root, "segment-core", version);
    }

    private sealed class FixtureIdentity<TContract>(Func<TContract, string> key) : IEnvelopeStorageIdentityFactory<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public void InitEnvelope(IObjectEnvelope<TContract> envelope)
        {
            envelope.EnvelopeCacheKey = key(envelope.ObjectInstance!);
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }

    public interface IMigrationRecord : IStoredContract<IMigrationRecord>
    {
        string Id { get; set; }
        int Amount { get; set; }
    }

    [MessagePackObject(true)]
    public class MigrationRecord : IMigrationRecord
    {
        public string Id { get; set; } = "";
        public int Amount { get; set; }
        [IgnoreMember] public IObjectEnvelope<IMigrationRecord>? Envelope { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class ToV2 : MigrationRecord, IMigration
    {
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up() { Amount += 100; return true; }
        public bool Down() { Amount -= 100; return true; }
    }

    [MessagePackObject(true)]
    public sealed class ToV3 : MigrationRecord, IMigration
    {
        [IgnoreMember] public string SourceVersion => "2.0.0";
        [IgnoreMember] public string TargetVersion => "3.0.0";
        public bool Up() { Amount += 1000; return true; }
        public bool Down() { Amount -= 1000; return true; }
    }

    [MessagePackObject(true)]
    public sealed class FailSecondToV2 : MigrationRecord, IMigration
    {
        private static int _calls;
        private static int _failOnCall;
        internal static void Reset(int failOnCall) { _calls = 0; _failOnCall = failOnCall; }
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up() { if (Interlocked.Increment(ref _calls) == _failOnCall) return false; Amount += 100; return true; }
        public bool Down() { Amount -= 100; return true; }
    }

    public interface ISecretRecord : IStoredContract<ISecretRecord>
    {
        string Id { get; set; }
        int Amount { get; set; }
        [EncryptedStorageField] string? Secret { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class SecretMigration : ISecretRecord, IMigration
    {
        public string Id { get; set; } = "";
        public int Amount { get; set; }
        public string? Secret { get; set; }
        [IgnoreMember] public IObjectEnvelope<ISecretRecord>? Envelope { get; set; }
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up()
        {
            if (Secret is null) throw new InvalidOperationException("Encrypted property plaintext must be restored before Up.");
            Amount += 100 + Secret.Length;
            return true;
        }
        public bool Down()
        {
            if (Secret is null) return false;
            Amount -= 100 + Secret.Length;
            return true;
        }
    }

    private sealed class FixtureKeyProvider : IKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
}
