using System.IO;

namespace TransactionManager.Abstraction.Dtos
{
    public interface IStoragePolicy
    {
        int MaxConcurrentOperations { get; }
        int PreferredBufferSize { get; }
        FileOptions FileOptions { get; }
        bool UseAsync { get; }
    }
}
