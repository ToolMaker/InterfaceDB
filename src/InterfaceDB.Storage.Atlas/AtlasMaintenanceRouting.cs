namespace Idb.Storage.AtlasCore;
public sealed partial class AtlasCoreStore
{
    private string MaintenanceGroupDirectoryName(int group) => _placement.GroupDirectoryName(group);
}
