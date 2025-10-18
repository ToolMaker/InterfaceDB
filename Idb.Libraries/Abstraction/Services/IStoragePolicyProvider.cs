using System.IO;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services
{
    /// <summary>
    /// Resolves storage policies for a given repository root.
    /// </summary>
    public interface IStoragePolicyProvider
    {
        /// <summary>
        /// Retrieves the storage policy that should be applied to the supplied root directory.
        /// </summary>
        /// <param name="rootDirectory">Directory the repository uses to persist payloads.</param>
        /// <returns>Policy describing the IO characteristics for the directory.</returns>
        IStoragePolicy GetPolicy(DirectoryInfo rootDirectory);
    }
}

