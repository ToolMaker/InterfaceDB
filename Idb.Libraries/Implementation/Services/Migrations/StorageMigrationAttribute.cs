namespace Idb.Libraries.Implementation.Services.Migrations
{
    /// <summary>
    /// Decorates a migration implementation with the contract type and version hop it handles.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class StorageMigrationAttribute : Attribute
    {
        public StorageMigrationAttribute(Type contractType, string sourceVersionTag, string targetVersionTag)
        {
            ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
            SourceVersionTag = string.IsNullOrWhiteSpace(sourceVersionTag)
                ? throw new ArgumentException("Source version tag cannot be null or empty.", nameof(sourceVersionTag))
                : sourceVersionTag;
            TargetVersionTag = string.IsNullOrWhiteSpace(targetVersionTag)
                ? throw new ArgumentException("Target version tag cannot be null or empty.", nameof(targetVersionTag))
                : targetVersionTag;
        }

        public Type ContractType { get; }
        public string SourceVersionTag { get; }
        public string TargetVersionTag { get; }
    }
}

