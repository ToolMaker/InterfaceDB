using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Describes a repository index that can be provisioned for a contract.
/// </summary>
/// <typeparam name="TContract">Contract interface the index targets.</typeparam>
public interface IIndex<TContract>
    where TContract : class
{
    /// <summary>
    /// Logical name of the index (used for configuration and reporting).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Underlying storage strategy used by the index.
    /// </summary>
    IndexTypeEnum IndexType { get; }

    //TODO: Add get and set methods
}

