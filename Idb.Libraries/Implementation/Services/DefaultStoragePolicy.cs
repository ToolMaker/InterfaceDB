using System.IO;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Implementation.Services
{
    internal sealed class DefaultStoragePolicy : IStoragePolicy
    {
        public static IStoragePolicy Instance { get; } = new DefaultStoragePolicy();

        private DefaultStoragePolicy()
        {
        }

        public int MaxConcurrentOperations => 8;
        public int PreferredBufferSize => 64 * 1024;
        public FileOptions FileOptions => FileOptions.SequentialScan;
        public bool UseAsync => true;
    }
}

