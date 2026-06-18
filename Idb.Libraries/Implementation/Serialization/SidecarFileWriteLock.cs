using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Uses an adjacent .lock file to coordinate writes to a file payload across repository instances.
/// </summary>
public sealed class SidecarFileWriteLock<TContract> : IStorageWriteLock
    where TContract : class, IStoredContract<TContract>
{
    private static readonly string ProcessOwnerId = Guid.NewGuid().ToString("N");
    private readonly StoragePolicy _storagePolicy;

    public SidecarFileWriteLock(StoragePolicy storagePolicy)
    {
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string providerLocator,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerLocator))
        {
            throw new ArgumentException("Provider locator cannot be null or whitespace.", nameof(providerLocator));
        }

        if (!_storagePolicy.UseSidecarWriteLocks)
        {
            return NoopWriteLock.Instance;
        }

        var lockPath = providerLocator + ".lock";
        var timeout = _storagePolicy.NormalizedWriteLockTimeout;
        var retryDelay = _storagePolicy.NormalizedWriteLockRetryDelay;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out acquiring write lock '{lockPath}' after {timeout}.");
            }

            try
            {
                var lockStream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 256,
                    FileOptions.WriteThrough);

                await WriteOwnerMetadataAsync(lockStream, cancellationToken).ConfigureAwait(false);
                return new SidecarWriteLock(lockPath, lockStream);
            }
            catch (IOException)
            {
                TryRemoveStaleLock(lockPath);
                await Task.Delay(GetRetryDelay(retryDelay, deadline), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                TryRemoveStaleLock(lockPath);
                await Task.Delay(GetRetryDelay(retryDelay, deadline), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan GetRetryDelay(TimeSpan configuredDelay, DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < configuredDelay ? remaining : configuredDelay;
    }

    private static async ValueTask WriteOwnerMetadataAsync(
        FileStream lockStream,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata =
            $"ownerId={ProcessOwnerId}{Environment.NewLine}" +
            $"machine={Environment.MachineName}{Environment.NewLine}" +
            $"processId={Environment.ProcessId}{Environment.NewLine}" +
            $"createdUtc={now:O}{Environment.NewLine}";

        var bytes = Encoding.UTF8.GetBytes(metadata);
        await lockStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await lockStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void TryRemoveStaleLock(string lockPath)
    {
        try
        {
            var lockInfo = new FileInfo(lockPath);
            if (!lockInfo.Exists)
            {
                return;
            }

            var age = DateTimeOffset.UtcNow - lockInfo.LastWriteTimeUtc;
            if (age < _storagePolicy.NormalizedStaleWriteLockTimeout)
            {
                return;
            }

            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            File.Delete(lockPath);
        }
        catch
        {
            // Another process may still hold the lock. The acquisition loop will retry until timeout.
        }
    }

    private sealed class SidecarWriteLock : IAsyncDisposable
    {
        private readonly string _lockPath;
        private readonly FileStream _lockStream;
        private int _disposed;

        public SidecarWriteLock(string lockPath, FileStream lockStream)
        {
            _lockPath = lockPath;
            _lockStream = lockStream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lockStream.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(_lockPath);
            }
            catch
            {
                // Lock cleanup is best effort; stale-lock handling covers process exits and cleanup failures.
            }
        }
    }

    private sealed class NoopWriteLock : IAsyncDisposable
    {
        public static NoopWriteLock Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
