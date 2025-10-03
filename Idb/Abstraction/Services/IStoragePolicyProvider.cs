using System.IO;
using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Abstraction.Services
{
    public interface IStoragePolicyProvider
    {
        IStoragePolicy GetPolicy(DirectoryInfo rootDirectory);
    }
}
