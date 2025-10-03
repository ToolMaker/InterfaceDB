namespace TransactionManager.Abstraction.Migrations
{
    public interface IStorageContractManifest
    {
        string ContractTypeName { get; }
        string ContractAssemblyName { get; }
        string VersionTag { get; }
        DateTimeOffset CreatedAt { get; }
        IReadOnlyList<IStorageContractMember> Members { get; }
        string Hash { get; }
    }

    public interface IStorageContractMember
    {
        string Name { get; }
        string MemberTypeFullName { get; }
        bool IsCollection { get; }
        bool IsNullable { get; }
    }
}
