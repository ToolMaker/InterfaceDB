namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Optional repository capability used to persist derived search-index values without making
/// the repository's storage format authoritative for query-engine internals.
/// </summary>
public interface IPersistentSearchIndexCatalogRepository
{
    /// <summary>
    /// Directory reserved for rebuildable search-index catalog files.
    /// </summary>
    string SearchIndexCatalogDirectory { get; }

    /// <summary>
    /// Initializes storage without eagerly materializing every payload.
    /// </summary>
    ValueTask InitializeForSearchIndexCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a stable identity for the current physical-key/locator set. A catalog is accepted
    /// only when this identity exactly matches the one captured during publication.
    /// </summary>
    ValueTask<SearchIndexStorageIdentity> GetSearchIndexStorageIdentityAsync(
        CancellationToken cancellationToken = default);
}

public readonly record struct SearchIndexStorageIdentity(
    int EntryCount,
    ReadOnlyMemory<byte> Fingerprint);
