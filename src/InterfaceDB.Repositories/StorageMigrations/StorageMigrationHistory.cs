using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Composition-root factory for historical provider registrations. It must use the
/// same logical database identity, serializer and encryption configuration for the requested version.</summary>
public sealed class StorageMigrationHistory<T>(Func<string, CancellationToken,
    ValueTask<IStorageMigrationHistoryHandle<T>>> open) : IStorageMigrationHistory<T> where T : class, IStoredContract<T>
{
    public ValueTask<IStorageMigrationHistoryHandle<T>> OpenAsync(string version, CancellationToken ct) => open(version, ct);
}

public sealed class StorageMigrationHistoryHandle<T>(IStorageMigrationSource<T> source,
    IStorageMigrationDestination<T> destination, IAsyncDisposable owner) : IStorageMigrationHistoryHandle<T>
    where T : class, IStoredContract<T>
{
    public IStorageMigrationSource<T> Source => source;
    public IStorageMigrationDestination<T> Destination => destination;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
