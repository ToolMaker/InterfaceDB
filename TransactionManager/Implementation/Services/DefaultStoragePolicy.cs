using System.IO;
using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Implementation.Services
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
