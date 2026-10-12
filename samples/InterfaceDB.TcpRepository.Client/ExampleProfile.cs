using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace InterfaceDB.TcpRepository.Sample;

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IExampleProfile : IStoredContract<IExampleProfile>
{
    [SearchIndex(SearchIndexMode.Exact)]
    string DisplayName { get; set; }
    int Counter { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class ExampleProfile : IExampleProfile
{
    public string DisplayName { get; set; } = "";
    public int Counter { get; set; }
    [IgnoreMember] public IObjectEnvelope<IExampleProfile>? Envelope { get; private set; }
}

public sealed class ExampleProfileConfig : IDbContractConfigDto<IExampleProfile>
{
    public string EnvelopeDICacheKey => "example-profiles";
    public string ContractName => "ExampleProfile";
    public string FileExtension => ".mpk";
    public string? ImplementationsPath => null;
    public string? ContractPath => null;
    public string? MigrationsPath => null;
    public string? DataRoot => null;
    public string? ActiveVersion => "1.0.0";
    public ContractRepositoryType RepositoryType => ContractRepositoryType.Remote;
    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
    public StoragePolicy? StoragePolicy => null;

    public static CompatibilityManifest Manifest { get; } = new()
    {
        ApiVersion = "InterfaceDB-remote-v1", StorageEngine = "SegmentCore", StorageFormat = "repository-example-v1",
        EnvelopeFormat = TcpRepositoryOptions<IExampleProfile>.EnvelopeFormat, ContractIdentity = "ExampleProfile",
        ContractVersion = "1.0.0", ContractSchemaHash = "example-profile-display-name-counter-v1",
        ImplementationManifestHash = "InterfaceDB.TcpRepository.Client:ExampleProfile:v1", IndexDefinitionHash = "display-name-exact-v1"
    };
}

public sealed record ExampleClientConfiguration
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 7444;
    public string TargetHost { get; init; } = "localhost";
    public string StoreAlias { get; init; } = "example-profiles";
    public string AccessTokenEnvironmentVariable { get; init; } = "IDB_USER_ACCESS_TOKEN";
    public string? ServerCaCertificatePath { get; init; }
    public bool DevelopmentCaWithoutRevocation { get; init; }
}
