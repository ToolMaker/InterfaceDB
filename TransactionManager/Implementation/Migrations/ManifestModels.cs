using TransactionManager.Abstraction.Migrations;

namespace TransactionManager.Implementation.Migrations
{
    public sealed class StorageContractManifest : IStorageContractManifest
    {
        public required string ContractTypeName { get; init; }
        public required string ContractAssemblyName { get; init; }
        public required string VersionTag { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required IReadOnlyList<IStorageContractMember> Members { get; init; }
        public required string Hash { get; init; }
    }

    public sealed class StorageContractMember : IStorageContractMember
    {
        public StorageContractMember(string name, string memberTypeFullName, bool isCollection, bool isNullable)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            MemberTypeFullName = memberTypeFullName ?? throw new ArgumentNullException(nameof(memberTypeFullName));
            IsCollection = isCollection;
            IsNullable = isNullable;
        }

        public string Name { get; }
        public string MemberTypeFullName { get; }
        public bool IsCollection { get; }
        public bool IsNullable { get; }
    }

    public sealed class StorageContractDiff : IStorageContractDiff
    {
        public StorageContractDiff(IStorageContractManifest current, IStorageContractManifest? previous, bool hasDifferences)
        {
            Current = current ?? throw new ArgumentNullException(nameof(current));
            Previous = previous;
            HasDifferences = hasDifferences;
        }

        public IStorageContractManifest Current { get; }
        public IStorageContractManifest? Previous { get; }
        public bool HasDifferences { get; }
    }
}
