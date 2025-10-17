namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Represents a materialized repository index that can participate in read/write coordination.
/// </summary>
public interface IRepositoryIndex
{
    /// <summary>
    /// Gets the contract type that the index tracks.
    /// </summary>
    Type ContractType { get; }

    /// <summary>
    /// Gets the logical name of the index.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the kind of index implementation (e.g. sorted list, hash map).
    /// </summary>
    IndexTypeEnum Kind { get; }

    /// <summary>
    /// Gets the runtime type of the key produced by the index.
    /// </summary>
    Type KeyType { get; }

    /// <summary>
    /// Gets the ordered member path used to derive the key value.
    /// </summary>
    IReadOnlyList<string> KeyMembers { get; }

    /// <summary>
    /// Adds an entity to the index and returns the associated key.
    /// </summary>
    /// <param name="entity">The entity to index.</param>
    /// <returns>The key produced for the entity.</returns>
    object? Add(object entity);

    /// <summary>
    /// Attempts to extract a key from the supplied probe object.
    /// </summary>
    /// <param name="probe">An object containing candidate values for the indexed members.</param>
    /// <param name="key">When the method returns, contains the extracted key if successful.</param>
    /// <param name="matchedMembers">When the method returns, contains the count of index members that were matched.</param>
    /// <returns><c>true</c> if a key was produced; otherwise <c>false</c>.</returns>
    bool TryExtractKey(object probe, out object? key, out int matchedMembers);

    /// <summary>
    /// Retrieves a snapshot of all entities in the index or entities associated with the specific key.
    /// </summary>
    /// <param name="key">An optional key to narrow the snapshot; when <c>null</c>, all entities are returned.</param>
    /// <returns>The snapshot entries.</returns>
    IReadOnlyList<object> Snapshot(object? key = null);
}

