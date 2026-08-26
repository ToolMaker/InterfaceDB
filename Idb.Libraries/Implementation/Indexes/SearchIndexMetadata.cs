namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Provider-facing query metadata probe used to select the indexed or passthrough repository path.
/// </summary>
public static class SearchIndexMetadata
{
    public static bool HasIndexes(Type contractType) =>
        SearchIndexDiscovery.Discover(contractType).Count != 0;
}
