using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FlatFileStorageIdentityFactoryTests
{
    [TestMethod]
    public void InitEnvelope_WhenKeyIsAlreadyValid_ReusesFormattedKey()
    {
        const string key = "customer-000001";
        var envelope = CreateEnvelope(key);
        var factory = CreateFactory();

        factory.InitEnvelope(envelope);

        Assert.AreSame(key, envelope.EnvelopeCacheKey);
    }

    [TestMethod]
    public void InitEnvelope_WhenKeyContainsInvalidFileNameCharacter_ReplacesIt()
    {
        var invalid = Path.GetInvalidFileNameChars()[0];
        var envelope = CreateEnvelope($"customer{invalid}000001");
        var factory = CreateFactory();

        factory.InitEnvelope(envelope);

        Assert.AreEqual("customer_000001", envelope.EnvelopeCacheKey);
    }

    private static FlatFileStorageIdentityFactory<TestStoredContract, string> CreateFactory()
        => new(new TestContractConfig(), new TestKeyFactory());

    private static FileObjectEnvelope<TestStoredContract> CreateEnvelope(string key)
        => new()
        {
            EnvelopeCacheKey = string.Empty,
            VersionTag = "1.0.0",
            ObjectInstance = new TestStoredContract(key),
            Location = "already-resolved"
        };

    private sealed record TestStoredContract(string Key) : IStoredContract<TestStoredContract>
    {
        public IObjectEnvelope<TestStoredContract>? Envelope => null;
    }

    private sealed class TestKeyFactory : IEnvelopeKeyFactory<TestStoredContract, string>
    {
        public string CreateKey(TestStoredContract instance) => instance.Key;
    }

    private sealed class TestContractConfig : IDbContractConfigDto<TestStoredContract>
    {
        public string EnvelopeDICacheKey => "identity-tests";
        public string ContractName => "IdentityTests";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => "identity-tests";
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy => null;
    }
}
