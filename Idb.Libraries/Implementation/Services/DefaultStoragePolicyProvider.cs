using System;
using System.IO;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

namespace Idb.Libraries.Implementation.Services
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

