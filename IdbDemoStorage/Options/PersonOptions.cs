using System;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace IdbDemoStorage.Options;

public sealed class PersonOptions : IDbContractOptions
{
    public string? ImplementationsPath { get; } = @"Storage\Person\v1_0_0";
    public string? ContractPath { get; } = @"Cont\Person\v1_0_0";
    public string? MigrationsPath { get; } = @"Migrations\Person";
    public string? ActiveVersion { get; } = "1.0.0";
    public IRepositoryIndexConfiguration[]? Indexes { get; } = Array.Empty<IRepositoryIndexConfiguration>();
    public ContractIndexProvisioningMode IndexProvisioningMode { get; } = ContractIndexProvisioningMode.Dynamic;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
}

