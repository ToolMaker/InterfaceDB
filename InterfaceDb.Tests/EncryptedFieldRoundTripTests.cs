using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed partial class EncryptedFieldRoundTripTests
{
    [TestMethod]
    [DataRow("file")] [DataRow("segment")]
    public async Task ConcreteQueriesMaterializeReadableIndependentValues(string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-FieldQuery", Guid.NewGuid().ToString("N"));
        try
        {
            await using var services = Build(root, engine, new Key());
            var repository = services.GetRequiredService<IRepository<ISecret>>();
            await repository.SaveAsync(new Secret { Name = "query", Number = 42, Text = "private" });
            var queried = await repository.QueryAsync<Secret>(r => r.Name == "query");
            Assert.IsNotNull(queried);
            Assert.AreEqual(42, queried.Number);
            Assert.AreEqual("private", queried.Text);
            queried.Number = 99;
            var searched = await repository.SearchFirstAsync<Secret>(r => r.Name == "query");
            Assert.IsNotNull(searched);
            Assert.AreEqual(42, searched.Number);
            Assert.AreNotSame(queried, searched);
            await repository.SaveAsync(queried);
            Assert.AreEqual(99, (await repository.QuerySingleAsync(_ => true)).Number);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    [DataRow("file", false)] [DataRow("segment", false)]
    [DataRow("file", true)] [DataRow("segment", true)]
    public async Task UpdatesPreserveEncryptedValuesAndExplicitNullsAfterReopen(string engine, bool useProxy)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-FieldRoundTrip", Guid.NewGuid().ToString("N"));
        var key = new Key();
        try
        {
            await using (var services = Build(root, engine, key))
            {
                var repository = services.GetRequiredService<IRepository<ISecret>>();
                ISecret original = new Secret { Name = "one", Number = 42, Text = "private", Optional = 9 };
                var saved = await repository.SaveAsync(original);
                var edit = useProxy ? saved : original;
                edit.Name = "two";
                saved = await repository.SaveAsync(edit);
                Assert.AreEqual(42, saved.Number, "An unrelated edit must preserve encrypted value types.");
                Assert.AreEqual("private", saved.Text);
                edit = useProxy ? saved : original;
                edit.Number = 73;
                edit.Text = null;
                edit.Optional = null;
                saved = await repository.SaveAsync(edit);
                Assert.AreEqual(73, saved.Number);
                Assert.IsNull(saved.Text, "An explicit null must replace the previous ciphertext value.");
                Assert.IsNull(saved.Optional);
                saved.Number = 100;
                Assert.AreEqual(73, (await repository.QuerySingleAsync(_ => true)).Number, "Unsaved proxy edits must stay private.");
            }
            await using (var services = Build(root, engine, key))
            {
                var repository = services.GetRequiredService<IRepository<ISecret>>();
                await repository.InitializeAsync();
                var stored = await repository.QuerySingleAsync(_ => true);
                Assert.AreEqual("two", stored.Name);
                Assert.AreEqual(73, stored.Number);
                Assert.IsNull(stored.Text);
                Assert.IsNull(stored.Optional);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("file")] [DataRow("segment")]
    public async Task ReaderFirstUpgradeRewritesLegacyFieldsAndStrictReopenSucceeds(string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-FieldUpgrade", Guid.NewGuid().ToString("N"));
        var key = new Key();
        try
        {
            await using (var services = Build(root, engine, key, EncryptedFieldWriteMode.LegacyContextless, allowLegacy: true))
            {
                var repository = services.GetRequiredService<IRepository<ISecret>>();
                var saved = await repository.SaveAsync(new Secret { Name = "legacy", Number = 42, Text = "private" });
                Assert.AreNotEqual(3, saved.Envelope!.EncryptedFields[nameof(ISecret.Number)].Ciphertext[4]);
            }
            await using (var services = Build(root, engine, key, allowLegacy: true))
            {
                var repository = services.GetRequiredService<IRepository<ISecret>>();
                await repository.InitializeAsync();
                var original = await repository.QuerySingleAsync(_ => true);
                Assert.AreEqual(42, original.Number);
                var saved = await repository.SaveAsync(original);
                Assert.AreEqual(3, saved.Envelope!.EncryptedFields[nameof(ISecret.Number)].Ciphertext[4]);
            }
            await using (var services = Build(root, engine, key))
            {
                var repository = services.GetRequiredService<IRepository<ISecret>>();
                await repository.InitializeAsync();
                var read = await repository.QuerySingleAsync(_ => true);
                Assert.AreEqual(42, read.Number);
                Assert.AreEqual("private", read.Text);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ServiceProvider Build(string root, string engine, IKeyProvider key,
        EncryptedFieldWriteMode mode = EncryptedFieldWriteMode.ContextBound, bool allowLegacy = false)
    {
        var services = new ServiceCollection();
        services.AddInterfaceDbAesGcmEncryption(key, new() { AllowContextlessPayloadMigration = allowLegacy });
        var config = new Config(root, engine == "segment" ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem) { FieldMode = mode };
        if (engine == "segment") services.AddSegmentCoreRepository(config);
        else if (engine == "file") services.AddFileRepository(config);
        else
        {
            var handled = false;
            RegisterAdditionalProvider(services, config, engine, ref handled);
            if (!handled) throw new NotSupportedException(engine);
        }
        return services.BuildServiceProvider();
    }

    static partial void RegisterAdditionalProvider(IServiceCollection services, Config config, string engine, ref bool handled);

    private sealed class Key : IKeyProvider
    {
        private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }

    [StorageScope(VersionTag = "1.0.0")]
    public interface ISecret : IStoredContract<ISecret>
    {
        [SearchIndex] string Name { get; set; }
        [EncryptedStorageField] int Number { get; set; }
        [EncryptedStorageField] string? Text { get; set; }
        [EncryptedStorageField] int? Optional { get; set; }
    }

    public sealed class Secret : ISecret
    {
        public string Name { get; set; } = "";
        public int Number { get; set; }
        public string? Text { get; set; }
        public int? Optional { get; set; }
        public IObjectEnvelope<ISecret>? Envelope { get; private set; }
    }

    private sealed record Config(string DataRoot, ContractRepositoryType RepositoryType) : IDbContractConfigDto<ISecret>
    {
        public EncryptedFieldWriteMode FieldMode { get; init; }
        public string EnvelopeDICacheKey => "field-roundtrip";
        public string ContractName => "field-roundtrip";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
        public StoragePolicy StoragePolicy => StoragePolicy.Default with { WatchStorageChanges = false, ObjectCacheMode = StorageObjectCacheMode.StrongObject, EncryptedFieldWrites = FieldMode };
    }
}
