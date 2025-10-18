using System.IO;

namespace Idb.Libraries.Abstractions.Dtos
{
    /// <summary>
    /// Describes low-level IO preferences used by repositories when interacting with the file system.
    /// </summary>
    public interface IStoragePolicy
    {
        /// <summary>
        /// Maximum number of operations that can run concurrently against the storage backend.
        /// </summary>
        int MaxConcurrentOperations { get; }

        /// <summary>
        /// Preferred buffer size for streaming payloads to and from storage.
        /// </summary>
        int PreferredBufferSize { get; }

        /// <summary>
        /// File system options applied when reading and writing data.
        /// </summary>
        FileOptions FileOptions { get; }

        /// <summary>
        /// Indicates whether async IO APIs should be preferred over synchronous ones.
        /// </summary>
        bool UseAsync { get; }
    }
}

