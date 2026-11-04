using Idb.Commercial.Repositories;
using Idb.Commercial.Repositories.Peers;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Transport;
using Idb.Transport.Peers;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.TcpRepository.Sample;

/// <summary>Client composition for disposable histories. Use a dedicated root per endpoint, account, dataset and contract version.</summary>
public static class PeerExampleComposition
{
    public static CompatibilityManifest Manifest { get; } = new()
    {
        ApiVersion = "idb-peer-v2", StorageEngine = "peer", StorageFormat = "peer-journal-v1", EnvelopeFormat = "IDB6",
        ContractIdentity = "ExamplePeerRecord", ContractVersion = "1.0.0", ContractSchemaHash = "record-id-sequence-date-text-v2",
        ImplementationManifestHash = "example-peer-record-v2", IndexDefinitionHash = "id-sequence-v1"
    };

    public static ServiceProvider CreateClient(TcpStoreClientOptions endpoint, string expectedIssuer, string expectedSubject,
        string accountDatasetDirectory, PeerRegistrationOptions registration, PeerLimits? limits = null)
    {
        if (endpoint.Manifest != Manifest) throw new ArgumentException("Deploy the matching peer contract before connecting.");
        var selected = limits ?? new PeerLimits();
        var dates = new PeerHistoryDateAccessor<IExamplePeerRecord>(x => x.CreatedAt);
        registration = registration with { DateProperty = registration.DateProperty ?? dates.PropertyName };
        var services = new ServiceCollection();
        // A custom serializer can be registered under "peer-local" before AddFileRepository.
        // AddPeerReplicatedRepository resolves that same keyed instance for wire payloads.
        services.AddFileRepository<IExamplePeerRecord>(new Config(Path.Combine(accountDatasetDirectory, "data")),
            durabilityMode: StorageDurabilityMode.Durable, runtimeTypeAllowlist: new(typeof(ExamplePeerRecord)));
        services.AddPeerReplicatedRepository("peer", "peer-local", Path.Combine(accountDatasetDirectory, "journal"),
            endpoint.StoreAlias, Manifest.ContractVersion, new Mapper(),
            new TcpPeerConnectionFactory(endpoint, expectedIssuer, expectedSubject, selected, registration), selected, historyDate: dates);
        return services.BuildServiceProvider();
    }

    public static PeerReplicatedRepository<IExamplePeerRecord> Repository(IServiceProvider provider)
        => provider.GetRequiredKeyedService<PeerReplicatedRepository<IExamplePeerRecord>>("peer");

    private sealed class Mapper : IResilientEntityMapper<IExamplePeerRecord>
    {
        public string GetStorageKey(IExamplePeerRecord entity)
            => entity.RecordId == Guid.Empty ? throw new ArgumentException("Assign a stable record ID before saving.") : entity.RecordId.ToString("N");
        public IExamplePeerRecord CreateDetachedCopy(IExamplePeerRecord source)
            => new ExamplePeerRecord { RecordId = source.RecordId, Sequence = source.Sequence, Text = source.Text, CreatedAt = source.CreatedAt };
        public void CopyValues(IExamplePeerRecord source, IExamplePeerRecord target)
        { target.RecordId = source.RecordId; target.Sequence = source.Sequence; target.Text = source.Text; target.CreatedAt = source.CreatedAt; }
    }
    private sealed class Config(string root) : IDbContractConfigDto<IExamplePeerRecord>
    {
        public string EnvelopeDICacheKey => "peer-local";
        public string ContractName => "ExamplePeerRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => new() { DurabilityMode = StorageDurabilityMode.Durable, RepositoryNotifications = new() };
    }
}

[StorageScope(VersionTag = "1.0.0")]
public interface IExamplePeerRecord : IStoredContract<IExamplePeerRecord>
{
    DateTimeOffset CreatedAt { get; set; }
    [SearchIndex(SearchIndexMode.Exact)] Guid RecordId { get; set; }
    [SequenceNumber, SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)] long? Sequence { get; set; }
    string Text { get; set; }
}

public sealed class ExamplePeerRecord : IExamplePeerRecord
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid RecordId { get; set; }
    public long? Sequence { get; set; }
    public string Text { get; set; } = "";
    public IObjectEnvelope<IExamplePeerRecord>? Envelope { get; private set; }
}
