using System.Collections.Generic;
using System.IO;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace InterfaceDb.ConsoleApp;

public sealed class PersonContractConfig : IDbContractConfigDto<Models.IPerson, FileInfo>
{
    public string Key { get; set; } = string.Empty;
    public string ContractName { get; set; } = string.Empty;
    public string? ImplementationsPath { get; set; }
    public string? ContractPath { get; set; }
    public string? MigrationsPath { get; set; }
    public string? DataRoot { get; set; }
    public string? ActiveVersion { get; set; }
    public IIndex<Models.IPerson>[]? Indexes { get; set; }
    public MigrationStrategyType MigrationStrategy { get; set; } = MigrationStrategyType.None;
    public IndexProvisioningModeType IndexProvisioningMode { get; set; } = IndexProvisioningModeType.None;
    public ContractRepositoryType RepositoryType { get; set; } = ContractRepositoryType.FileSystem;
}
