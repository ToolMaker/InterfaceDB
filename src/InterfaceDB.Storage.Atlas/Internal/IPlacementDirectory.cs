namespace Idb.Storage.AtlasCore.Internal;

internal interface IPlacementDirectory
{
    long Generation { get; }
    int SplitCount { get; }
    long PublicationFailures { get; }
    int KnownGroupCount { get; }
    string GroupDirectoryName(int group);
    PlacementRoute Route(string key);
    PlacementRoute RouteForWrite(string key);
    ValueTask<PlacementRoute> GetWriteRouteAsync(string key, CancellationToken cancellationToken);
    void Observe(string key, PlacementRoute route);
    ValueTask FlushMaintenanceAsync(CancellationToken cancellationToken = default);
}
