using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class ContextBoundFieldTests
{
    [TestMethod]
    public void FieldLifecycleCannotSilentlyOmitRepositoryContext()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new Idb.Libraries.Implementation.Indexes.EnvelopeLifecycle<ISecret>(
            () => new FileObjectEnvelope<ISecret> { EnvelopeCacheKey = "a", VersionTag = "1.0.0" }, new Identity(), new AesGcmEncryptor(new Key())));
    }

    [TestMethod]
    public async Task ConflictStrategyReadsIncomingFieldsAfterFinalIdentityAllocation()
    {
        var lifecycle = new Idb.Libraries.Implementation.Indexes.EnvelopeLifecycle<ISecret>(
            () => new FileObjectEnvelope<ISecret> { EnvelopeCacheKey = "", VersionTag = "1.0.0" }, new Identity(),
            new AesGcmEncryptor(new Key()), key => Encoding.UTF8.GetBytes("store:" + key));
        var candidate = await lifecycle.WrapAsync(new Secret { First = "first", Second = "second" });
        candidate.EnvelopeCacheKey = "a-1";
        candidate.Location = "a-1";
        var writes = new EnvelopeWriteCoordinator<ISecret>(new MonotonicRevisionTimestampGenerator(),
            new BinaryObjectSerializer<ISecret>(), lifecycle, new ReadingStrategy());
        var prepared = await writes.PrepareWithCurrentAsync(candidate, true, null,
            _ => ValueTask.FromResult<ISecret?>(null), CancellationToken.None);
        Assert.AreEqual("first", lifecycle.Unwrap(prepared).First);
    }

    private sealed class ReadingStrategy : IWriteConflictStrategy<ISecret>
    {
        public async ValueTask<EnvelopeWriteDecision<ISecret>> DecideAsync(EnvelopeWriteConflict<ISecret> context,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("first", (await context.ReadIncomingAsync(cancellationToken)).First);
            return new(true);
        }
    }
    private sealed class Identity : Idb.Libraries.Abstractions.Services.IEnvelopeStorageIdentityFactory<ISecret>
    {
        public Idb.Libraries.Abstractions.Enums.StorageObjectCardinality ObjectCardinality
            => Idb.Libraries.Abstractions.Enums.StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<ISecret> envelope)
        { envelope.EnvelopeCacheKey = "a"; envelope.Location = "a"; }
    }

    [TestMethod]
    [DataRow("record")] [DataRow("field")] [DataRow("repository")] [DataRow("missing")]
    public void PersistedSubstitutionIsRejected(string attack)
    {
        var encryptor = new AesGcmEncryptor(new Key());
        var fields = Processor(encryptor);
        var envelope = Save(fields, "a");
        var bytes = new BinaryObjectSerializer<ISecret>().Serialize(envelope);
        var stored = new BinaryObjectSerializer<ISecret>().Deserialize(bytes); // No runtime staging provenance.
        if (attack == "record") stored.EnvelopeCacheKey = "b";
        if (attack == "field") stored.EncryptedFields[nameof(ISecret.Second)] = stored.EncryptedFields[nameof(ISecret.First)];
        if (attack == "missing") stored.EncryptedFields.Remove(nameof(ISecret.First));
        if (attack == "repository") fields = Processor(encryptor, repository: "other");
        Assert.ThrowsExactly<CryptographicException>(() =>
        {
            var read = fields.WrapForRead(stored.ObjectInstance!, stored);
            _ = attack == "field" ? read.Second : read.First;
        });
    }

    [TestMethod]
    public void FinalPhysicalIdentityRebindAndProxyIdentityCapturePreserveValues()
    {
        var fields = Processor(new AesGcmEncryptor(new Key()));
        var envelope = Save(fields, "a");
        envelope.EnvelopeCacheKey = "a-1";
        fields.BindFields(envelope);
        var stored = new BinaryObjectSerializer<ISecret>().Deserialize(new BinaryObjectSerializer<ISecret>().Serialize(envelope));
        var read = fields.WrapForRead(stored.ObjectInstance!, stored);
        Assert.AreEqual("first", read.First);
        stored.EnvelopeCacheKey = "uncommitted-edit";
        Assert.AreEqual("first", read.First, "Editing envelope metadata must not redefine a proxy's original context.");
        read.First = null;
        Assert.IsNull(read.First);
    }

    [TestMethod]
    public void LegacyReadRequiresExplicitMigrationAndFullRewriteRemovesLegacyCiphertext()
    {
        var key = new Key();
        var migrationEncryptor = new AesGcmEncryptor(key, new() { AllowContextlessPayloadMigration = true });
        var legacy = Processor(migrationEncryptor, EncryptedFieldWriteMode.LegacyContextless);
        var stored = Save(legacy, "a");
        var strict = Processor(new AesGcmEncryptor(key));
        Assert.ThrowsExactly<CryptographicException>(() => _ = strict.WrapForRead(stored.ObjectInstance!, stored).First);
        var upgrade = Processor(migrationEncryptor);
        var original = upgrade.WrapForRead(stored.ObjectInstance!, stored);
        var copy = EncryptedStorageFieldProcessor<ISecret>.CloneForStorage(original);
        var rewritten = EnvelopeWriteCoordinator<ISecret>.CopyEnvelope(stored);
        rewritten.ObjectInstance = upgrade.PrepareForStorage(copy, rewritten);
        Assert.AreEqual(3, rewritten.EncryptedFields[nameof(ISecret.First)].Ciphertext[4]);
        Assert.AreEqual("first", strict.WrapForRead(rewritten.ObjectInstance, rewritten).First);
        Assert.Throws<CryptographicException>(() => keylessRead(rewritten));

        void keylessRead(IObjectEnvelope<ISecret> e)
            => _ = Processor(new AesGcmEncryptor(new Key())).WrapForRead(e.ObjectInstance!, e).First;
    }

    private static EncryptedStorageFieldProcessor<ISecret> Processor(IEncryptor encryptor,
        EncryptedFieldWriteMode mode = EncryptedFieldWriteMode.ContextBound, string repository = "store")
        => new(encryptor, key => Encoding.UTF8.GetBytes(repository + ":" + key), mode);
    private static IObjectEnvelope<ISecret> Save(EncryptedStorageFieldProcessor<ISecret> processor, string key)
    {
        var envelope = new FileObjectEnvelope<ISecret> { EnvelopeCacheKey = key, VersionTag = "1.0.0",
            ObjectInstance = new Secret { First = "first", Second = "second" } };
        processor.PrepareForStorage(envelope.ObjectInstance, envelope);
        return envelope;
    }
    private sealed class Key : IKeyProvider
    {
        private readonly byte[] _bytes = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _bytes;
    }
    public interface ISecret : IStoredContract<ISecret>
    {
        [EncryptedStorageField] string? First { get; set; }
        [EncryptedStorageField] string? Second { get; set; }
    }
    public sealed class Secret : ISecret
    {
        public string? First { get; set; }
        public string? Second { get; set; }
        public IObjectEnvelope<ISecret>? Envelope { get; private set; }
    }
}
