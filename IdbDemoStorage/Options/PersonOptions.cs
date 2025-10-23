using System;
using System.IO;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace IdbDemoStorage.Options;

// TODO: Update the generic argument if your runtime contract type differs from MigrationTool.Contracts.Person.v1_0_0.IPerson.
public sealed class PersonOptions : IDbContractConfigDto<MigrationTool.Contracts.Person.v1_0_0.IPerson, FileInfo>
{
    public string Key { get; set; } = "Person";
    public string ContractName { get; set; } = "Person";
    public string? ImplementationsPath { get; set; } = @"..\..\..\..\IdbDemoStorage\Storage\Person";
    public string? ContractPath { get; set; } = @"..\..\..\..\IdbDemoStorage\Contracts\Person";
    public string? MigrationsPath { get; set; } = @"..\..\..\..\IdbDemoStorage\Migrations\Person";
    public string? DataRoot { get; set; } = "Data";
    public string? ActiveVersion { get; set; } = "1.0.0";
    public IIndex<MigrationTool.Contracts.Person.v1_0_0.IPerson>[]? Indexes { get; set; } = Array.Empty<IIndex<MigrationTool.Contracts.Person.v1_0_0.IPerson>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; set; } = IndexProvisioningModeType.Dynamic;
    public ContractRepositoryType RepositoryType { get; set; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; set; } =
        StoragePolicy.Default with { MaxConcurrency = Environment.ProcessorCount };
}
