namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Coordinates repository indexes for write-through updates and read-time selection.
/// </summary>
public interface IRepositoryIndexManager
{
    /// <summary>
    /// Registers a single index configuration.
    /// </summary>
    /// <param name="configuration">The configuration to register.</param>
    void ConfigureIndex(IRepositoryIndexConfiguration configuration);

    /// <summary>
    /// Registers multiple index configurations.
    /// </summary>
    /// <param name="configurations">The configurations to register.</param>
    void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations);

    /// <summary>
    /// Gets the configured index metadata for the specified contract type.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType);

    /// <summary>
    /// Updates all indexes for the supplied entity and returns the produced keys.
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <param name="entity">The entity that was persisted.</param>
    /// <returns>A mapping of index name to generated key.</returns>
    Dictionary<string, object?> UpdateIndexes<TContract>(TContract entity)
        where TContract : class;

    /// <summary>
    /// Attempts to retrieve a typed sorted list index.
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="name">The index name.</param>
    /// <param name="index">The typed sorted list.</param>
    /// <returns><c>true</c> when the index is present; otherwise <c>false</c>.</returns>
    bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull;

    /// <summary>
    /// Attempts to resolve the most efficient index for the given selection request.
    /// </summary>
    /// <param name="request">The selection request.</param>
    /// <returns>The resolved selection, or <c>null</c> when no suitable index is available.</returns>
    RepositoryIndexSelection? SelectBestIndex(IndexSelectionRequest request);

    /// <summary>
    /// Attempts to locate a specific index by contract type and name.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="name">The index name.</param>
    /// <param name="index">The located index.</param>
    /// <returns><c>true</c> when the index exists; otherwise <c>false</c>.</returns>
    bool TryGetIndex(Type contractType, string name, out IRepositoryIndex index);
}

