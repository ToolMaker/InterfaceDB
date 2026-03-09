
namespace Idb.Libraries.Abstractions.Dtos
{
    /// <summary>
    /// Provides runtime details about a storage scope discovered via <see cref="Idb.Libraries.Abstractions.Attributes.StorageScopeAttribute"/>.
    /// </summary>
    /// <typeparam name="TContract">Contract interface associated with the scope.</typeparam>
    public interface IScopeDescriptor<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        /// <summary>
        /// Contract interface type that defines the scope.
        /// </summary>
        Type ContractType { get; }

        /// <summary>
        /// Root folder containing contract implementations and persisted payloads.
        /// </summary>
        DirectoryInfo Directory { get; }

        /// <summary>
        /// File name factory used to generate storage identifiers for the contract.
        /// </summary>
        Type FileNameFactoryType { get; }

        /// <summary>
        /// Concrete implementation types discovered for the contract.
        /// </summary>
        IReadOnlyCollection<Type> Implementations { get; }

        /// <summary>
        /// Version tag associated with the scope (matches the contract baseline).
        /// </summary>
        string VersionTag { get; }
    }
}

