using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace MigrationTool.Options;

public sealed class PersonOptions : IDbContractOptions
{
    public string Key { get; } = "Person";
    public string? ImplementationsPath { get; } = @"..\..\..\..\IdbDemoStorage\Storage\Person\v1_0_0";
    public string? ContractPath { get; } = @"..\..\..\..\IdbDemoStorage\Contracts\Person\v1_0_0";
    public string? MigrationsPath { get; } = @"..\..\..\..\IdbDemoStorage\Migrations\Person";
    public string? ActiveVersion { get; } = "1.0.0";
    public IRepositoryIndexConfiguration[]? Indexes { get; } = Array.Empty<IRepositoryIndexConfiguration>();
    public ContractIndexProvisioningModeType IndexProvisioningMode { get; } = ContractIndexProvisioningModeType.Dynamic;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public ContractObjectWriterType ObjectWriter { get; } = ContractObjectWriterType.None;
    public ContractObjectReaderType ObjectReader { get; } = ContractObjectReaderType.None;
}
