using System.IO;

namespace Idb.Libraries.Abstractions.Dtos
{
    public interface IStoragePolicy
    {
        int MaxConcurrentOperations { get; }
        int PreferredBufferSize { get; }
        FileOptions FileOptions { get; }
        bool UseAsync { get; }
    }
}
