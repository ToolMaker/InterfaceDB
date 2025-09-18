using System;
using System.IO;
using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Services;

namespace TransactionManager.Implementation.Services
{
    public sealed class DefaultStoragePolicyProvider : IStoragePolicyProvider
    {
        public IStoragePolicy GetPolicy(DirectoryInfo rootDirectory)
        {
            if (rootDirectory is null)
            {
                throw new ArgumentNullException(nameof(rootDirectory));
            }

            return DefaultStoragePolicy.Instance;
        }
    }
}
