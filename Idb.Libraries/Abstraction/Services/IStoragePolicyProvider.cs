using System.IO;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services
{
    public interface IStoragePolicyProvider
    {
        IStoragePolicy GetPolicy(DirectoryInfo rootDirectory);
    }
}

