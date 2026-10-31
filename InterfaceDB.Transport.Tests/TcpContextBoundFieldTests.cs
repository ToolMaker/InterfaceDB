using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task ContextBoundFieldsUseFinalRemoteKeysAndPreserveDetachedEdits()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var connection = Options(host, Token("parent-a")).Connection;
        var options = new TcpRepositoryOptions<IRemoteSecret>
        {
            Connection = connection, Contract = new SecretConfig(), RuntimeTypes = new(typeof(RemoteSecret)),
            Encryptor = new AesGcmEncryptor(new SerializerKey()), StorageIdentityFactory = new SecretIdentity()
        };
        await using (var repository = new TcpRepository<IRemoteSecret>(options))
        {
            var saved = await repository.SaveManyAsync([new RemoteSecret { Secret = "one" }, new RemoteSecret { Secret = "two" }]);
            Assert.AreEqual("same-0", saved[0].Envelope!.EnvelopeCacheKey);
            Assert.AreEqual("same-1", saved[1].Envelope!.EnvelopeCacheKey);
            Assert.AreEqual("one", saved[0].Secret);
            Assert.AreEqual("two", saved[1].Secret);
            Assert.AreEqual(3, saved[0].Envelope!.EncryptedFields[nameof(IRemoteSecret.Secret)].Ciphertext[4]);
            saved[0].Secret = "edited";
            Assert.AreEqual("one", (await repository.QueryFirstByStorageKeyAsync("same-0"))!.Secret);
            await repository.SaveAsync(saved[0]);
        }
        await using var reopened = new TcpRepository<IRemoteSecret>(options);
        Assert.AreEqual("edited", (await reopened.QueryFirstByStorageKeyAsync("same-0"))!.Secret);
        Assert.AreEqual("two", (await reopened.QueryFirstByStorageKeyAsync("same-1"))!.Secret);
    }

    [StorageScope(VersionTag = "1.0.0")]
    public interface IRemoteSecret : IStoredContract<IRemoteSecret>
    {
        [EncryptedStorageField] string? Secret { get; set; }
    }
    public sealed class RemoteSecret : IRemoteSecret
    {
        public string? Secret { get; set; }
        public IObjectEnvelope<IRemoteSecret>? Envelope { get; private set; }
    }
    private sealed class SecretIdentity : IEnvelopeStorageIdentityFactory<IRemoteSecret>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<IRemoteSecret> envelope)
        {
            if (string.IsNullOrEmpty(envelope.EnvelopeCacheKey)) envelope.EnvelopeCacheKey = "same";
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }
    private sealed class SecretConfig : IDbContractConfigDto<IRemoteSecret>
    {
        public ContractRepositoryType RepositoryType => ContractRepositoryType.None;
        public Idb.Libraries.Abstractions.Options.StoragePolicy StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default;
        public string EnvelopeDICacheKey => "secret";
        public string ContractName => "secret";
        public string FileExtension => ".mpk";
        public string? DataRoot => null;
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
    }
}
