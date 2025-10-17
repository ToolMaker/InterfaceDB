using Idb.Libraries.Abstraction.Dtos;
using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Implementation.Options;

namespace IdbDemoStorage.Options;

public sealed class PersonOptions : IDbContractConfigDto
{
    public string Key { get; set; } = "Person";
    public string ContractName { get; set; } = "Person";
    public string? ImplementationsPath { get; set; } = @"..\Storage\Person";
    public string? ContractPath { get; set; } = @"..\Contracts\Person";
    public string? MigrationsPath { get; set; } = @"..\Mig\Person";
    public string? DataRoot { get; set; } = "Data";
    public string? ActiveVersion { get; set; } = "1.0.0";
    public IRepositoryIndexConfiguration[]? Indexes { get; set; } = Array.Empty<IRepositoryIndexConfiguration>();
    public ContractIndexProvisioningModeType IndexProvisioningMode { get; set; } = ContractIndexProvisioningModeType.Dynamic;
    public ContractRepositoryType RepositoryType { get; set; } = ContractRepositoryType.FileSystem;
    public ContractObjectWriterType ObjectWriter { get; set; } = ContractObjectWriterType.Binary;
    public ContractObjectReaderType ObjectReader { get; set; } = ContractObjectReaderType.Binary;
    public RepositoryLoadStrategyType LoadStrategy { get; set; } = RepositoryLoadStrategyType.Lazy;
    public IMigrationStrategy MigrationStrategy { get; set; } = new MigrationStrategyOptions();
}

