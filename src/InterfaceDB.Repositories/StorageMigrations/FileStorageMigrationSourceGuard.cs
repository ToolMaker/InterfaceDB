using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Uses a dedicated coordinator so ordinary File writers do not inherit a migration-writer exemption.</summary>
public sealed class FileStorageMigrationSourceGuard<T> : IStorageMigrationSourceGuard where T : class, IStoredContract<T>
{
    private readonly IFileWriteCoordinator _guard;
    private readonly string _root;
    private readonly bool _sourceExisted;
    private readonly string _extension;
    public FileStorageMigrationSourceGuard(IDbContractConfigDto<T> sourceConfig, StoragePolicy sourcePolicy,
        IStorageWriteLock writeLock, IFilePublication publication, bool? sourceExisted = null, IFileLeaseValidation? leaseValidation = null)
    {
        if (!sourcePolicy.UseSidecarWriteLocks)
            throw new ArgumentException("Migration source requires sidecar writer exclusion.");
        _root = Path.GetFullPath(sourceConfig.DataRoot ?? throw new ArgumentException("Source DataRoot is required."));
        _sourceExisted = sourceExisted ?? Directory.Exists(_root);
        _extension = sourceConfig.FileExtension;
        if (!_sourceExisted || !Directory.Exists(_root))
            throw new DirectoryNotFoundException("Storage migration source is missing; refusing an empty migration.");
        _guard = new CoalescingFileWriteCoordinator<T>(writeLock, sourceConfig, sourcePolicy, publication, leaseValidation);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!_sourceExisted || !Directory.Exists(_root)) throw new DirectoryNotFoundException("Storage migration source is missing; refusing an empty migration.");
        var lease = await _guard.AcquireMigrationScopeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StorageMigrationPaths.ValidateTree(_root, cancellationToken);
            await _guard.DrainPendingWritesAsync(_root, _extension, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

}
