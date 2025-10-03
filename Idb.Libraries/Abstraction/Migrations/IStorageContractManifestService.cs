namespace Idb.Libraries.Abstractions.Migrations
{
    public interface IStorageContractManifestService
    {
        Task<IStorageContractManifest> SnapshotAsync<TContract>(CancellationToken cancellationToken = default);
        Task<IStorageContractManifest> SnapshotAsync(Type contractType, CancellationToken cancellationToken = default);
        Task<IStorageContractDiff> CompareWithLatestAsync<TContract>(CancellationToken cancellationToken = default);
        Task<IStorageContractDiff> CompareWithLatestAsync(Type contractType, CancellationToken cancellationToken = default);
    }

    public interface IStorageContractDiff
    {
        IStorageContractManifest Current { get; }
        IStorageContractManifest? Previous { get; }
        bool HasDifferences { get; }
    }
}
