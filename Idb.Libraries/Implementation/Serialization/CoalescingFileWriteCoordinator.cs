using System.Globalization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Coordinates last-write-wins file replacement across processes.
/// </summary>
public sealed class CoalescingFileWriteCoordinator<TContract> : IFileWriteCoordinator
    where TContract : class, IStoredContract<TContract>
{
    private const string PendingMarker = ".pending.";
    private const string WritingSuffix = ".writing";
    private const string ReadySuffix = ".ready";
    private const string DeleteReadySuffix = ".delete.ready";
    private readonly IFileWriteLock _fileWriteLock;
    private readonly StoragePolicy _storagePolicy;
    private readonly string _storageRoot;
    private int _localMigrationScopeActive;

    public CoalescingFileWriteCoordinator(
        IFileWriteLock fileWriteLock,
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy)
    {
        _fileWriteLock = fileWriteLock ?? throw new ArgumentNullException(nameof(fileWriteLock));
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _storageRoot = Path.GetFullPath(contractConfig?.DataRoot
            ?? throw new InvalidOperationException("Contract configuration must supply a DataRoot directory."));
    }

    public async ValueTask<IAsyncDisposable> AcquireMigrationScopeAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_storageRoot);
        var migrationLockPath = Path.Combine(_storageRoot, ".idb-migration.lock");
        var migrationLock = await AcquireExclusiveLeaseAsync(migrationLockPath, cancellationToken).ConfigureAwait(false);

        try
        {
            var deadline = DateTimeOffset.UtcNow + _storagePolicy.NormalizedWriteLockTimeout;
            while (HasActiveWriterLeases())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("Timed out waiting for active file writers before migration.");
                }

                await Task.Delay(_storagePolicy.NormalizedWriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _localMigrationScopeActive, 1);
            return new MigrationLease(this, migrationLock);
        }
        catch
        {
            await migrationLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> PublishAndCommitAsync(
        string payloadPath,
        bool requireNewPayload,
        Func<string, CancellationToken, ValueTask> writeProposalAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new ArgumentException("Payload path cannot be null or whitespace.", nameof(payloadPath));
        }

        ArgumentNullException.ThrowIfNull(writeProposalAsync);

        await using var writerLease = Volatile.Read(ref _localMigrationScopeActive) != 0
            ? NoopLease.Instance
            : await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(payloadPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (requireNewPayload)
        {
            return await CreateNewPayloadAsync(
                payloadPath,
                writeProposalAsync,
                cancellationToken).ConfigureAwait(false);
        }

        var writingPath = payloadPath + PendingMarker + Guid.NewGuid().ToString("N") + WritingSuffix;
        try
        {
            await writeProposalAsync(writingPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var readyPath = CreateReadyPath(payloadPath);
            File.Move(writingPath, readyPath);
            var canonicalProposal = await DrainPendingWritesCoreAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            return string.Equals(canonicalProposal, readyPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            TryDelete(writingPath);
            throw;
        }
    }

    public async ValueTask<bool> DeleteAndCommitAsync(
        string payloadPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new ArgumentException("Payload path cannot be null or whitespace.", nameof(payloadPath));
        }

        await using var writerLease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(payloadPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var hadActiveStorage = File.Exists(payloadPath)
            || EnumerateReadyFiles(payloadPath).Any(path => !IsDeleteProposal(path));
        var deleteProposal = CreateDeleteReadyPath(payloadPath);
        var writingPath = payloadPath + PendingMarker + Guid.NewGuid().ToString("N") + ".delete" + WritingSuffix;
        try
        {
            using (var stream = new FileStream(
                writingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough))
            {
                stream.Flush(flushToDisk: true);
            }

            File.Move(writingPath, deleteProposal);
        }
        catch
        {
            TryDelete(writingPath);
            throw;
        }

        try
        {
            var canonicalProposal = await DrainPendingWritesCoreAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            return hadActiveStorage
                && string.Equals(canonicalProposal, deleteProposal, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            TryDelete(deleteProposal);
            throw;
        }
    }

    public async ValueTask<bool> ReplaceIfUnchangedAsync(
        string payloadPath,
        ReadOnlyMemory<byte> expectedPayload,
        ReadOnlyMemory<byte> replacementPayload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new ArgumentException("Payload path cannot be null or whitespace.", nameof(payloadPath));
        }

        await using var writerLease = Volatile.Read(ref _localMigrationScopeActive) != 0
            ? NoopLease.Instance
            : await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await _fileWriteLock
            .AcquireAsync(payloadPath, cancellationToken)
            .ConfigureAwait(false);

        CommitReadyFilesUnderLock(payloadPath);
        if (!File.Exists(payloadPath))
        {
            return false;
        }

        var currentPayload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (!currentPayload.AsSpan().SequenceEqual(expectedPayload.Span))
        {
            return false;
        }

        var writingPath = payloadPath + PendingMarker + Guid.NewGuid().ToString("N") + WritingSuffix;
        try
        {
            await File.WriteAllBytesAsync(writingPath, replacementPayload, cancellationToken).ConfigureAwait(false);
            using (var handle = File.OpenHandle(writingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                RandomAccess.FlushToDisk(handle);
            }

            var readyPath = CreateReadyPath(payloadPath);
            File.Move(writingPath, readyPath);
            CommitProposal(readyPath, payloadPath);
            return true;
        }
        catch
        {
            TryDelete(writingPath);
            throw;
        }
    }

    private async ValueTask<bool> CreateNewPayloadAsync(
        string payloadPath,
        Func<string, CancellationToken, ValueTask> writeProposalAsync,
        CancellationToken cancellationToken)
    {
        await using var writeLock = await _fileWriteLock
            .AcquireAsync(payloadPath, cancellationToken)
            .ConfigureAwait(false);

        CommitReadyFilesUnderLock(payloadPath);
        if (File.Exists(payloadPath))
        {
            throw new StorageObjectAlreadyExistsException(payloadPath);
        }

        var writingPath = payloadPath + PendingMarker + Guid.NewGuid().ToString("N") + WritingSuffix;
        try
        {
            await writeProposalAsync(writingPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var readyPath = CreateReadyPath(payloadPath);
            File.Move(writingPath, readyPath);
            CommitProposal(readyPath, payloadPath);
            return true;
        }
        catch
        {
            TryDelete(writingPath);
            throw;
        }
    }

    public async ValueTask DrainPendingWritesAsync(
        string storageRoot,
        string payloadExtension,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageRoot) || !Directory.Exists(storageRoot))
        {
            return;
        }

        var extension = payloadExtension.StartsWith(".", StringComparison.Ordinal)
            ? payloadExtension
            : "." + payloadExtension;

        var payloadPaths = Directory
            .EnumerateFiles(storageRoot, "*" + ReadySuffix, SearchOption.AllDirectories)
            .Select(TryGetPayloadPath)
            .Where(path => path is not null && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        foreach (var payloadPath in payloadPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainPendingWritesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        }

        CleanupStaleWritingFiles(storageRoot);
    }

    public async ValueTask DrainPendingWritesAsync(
        string payloadPath,
        CancellationToken cancellationToken = default)
        => _ = await DrainPendingWritesCoreAsync(payloadPath, cancellationToken).ConfigureAwait(false);

    private async ValueTask<string?> DrainPendingWritesCoreAsync(
        string payloadPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            throw new ArgumentException("Payload path cannot be null or whitespace.", nameof(payloadPath));
        }

        await using var writeLock = await _fileWriteLock
            .AcquireAsync(payloadPath, cancellationToken)
            .ConfigureAwait(false);

        string? canonicalProposal = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readyFiles = EnumerateReadyFiles(payloadPath);
            if (readyFiles.Length == 0)
            {
                return canonicalProposal;
            }

            var newest = readyFiles[^1];
            CommitProposal(newest, payloadPath);
            canonicalProposal = newest;

            foreach (var obsolete in readyFiles)
            {
                if (!string.Equals(obsolete, newest, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(obsolete);
                }
            }
        }
    }

    private static string? CommitReadyFilesUnderLock(string payloadPath)
    {
        var readyFiles = EnumerateReadyFiles(payloadPath);
        if (readyFiles.Length == 0)
        {
            return null;
        }

        var newest = readyFiles[^1];
        CommitProposal(newest, payloadPath);
        foreach (var obsolete in readyFiles)
        {
            if (!string.Equals(obsolete, newest, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(obsolete);
            }
        }

        return newest;
    }

    private static string CreateReadyPath(string payloadPath)
    {
        var publishedTicks = DateTimeOffset.UtcNow.UtcTicks
            .ToString("D19", CultureInfo.InvariantCulture);
        return payloadPath + PendingMarker + publishedTicks + "." + Guid.NewGuid().ToString("N") + ReadySuffix;
    }

    private static string CreateDeleteReadyPath(string payloadPath)
    {
        var publishedTicks = DateTimeOffset.UtcNow.UtcTicks
            .ToString("D19", CultureInfo.InvariantCulture);
        return payloadPath + PendingMarker + publishedTicks + "." + Guid.NewGuid().ToString("N") + DeleteReadySuffix;
    }

    private async ValueTask<IAsyncDisposable> AcquireWriterLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storageRoot);
        var migrationLockPath = Path.Combine(_storageRoot, ".idb-migration.lock");
        var deadline = DateTimeOffset.UtcNow + _storagePolicy.NormalizedWriteLockTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for migration to release the storage write scope.");
            }

            if (File.Exists(migrationLockPath))
            {
                TryDeleteStaleLease(migrationLockPath);
                await Task.Delay(_storagePolicy.NormalizedWriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var writerLockPath = Path.Combine(_storageRoot, ".idb-writer." + Guid.NewGuid().ToString("N") + ".lock");
            var writerLease = await AcquireExclusiveLeaseAsync(writerLockPath, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(migrationLockPath))
            {
                return writerLease;
            }

            await writerLease.DisposeAsync().ConfigureAwait(false);

            await Task.Delay(_storagePolicy.NormalizedWriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireExclusiveLeaseAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _storagePolicy.NormalizedWriteLockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return new FileLease(lockPath, stream);
            }
            catch (IOException)
            {
                TryDeleteStaleLease(lockPath);
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out acquiring storage lease '{lockPath}'.");
                }

                await Task.Delay(_storagePolicy.NormalizedWriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool HasActiveWriterLeases()
    {
        var active = false;
        foreach (var path in Directory.EnumerateFiles(_storageRoot, ".idb-writer.*.lock", SearchOption.TopDirectoryOnly))
        {
            TryDeleteStaleLease(path);
            if (File.Exists(path))
            {
                active = true;
            }
        }

        return active;
    }

    private void TryDeleteStaleLease(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists
                && DateTimeOffset.UtcNow - info.LastWriteTimeUtc >= _storagePolicy.NormalizedStaleWriteLockTimeout)
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
                File.Delete(path);
            }
        }
        catch
        {
            // A live process may still hold the lease.
        }
    }

    private static string[] EnumerateReadyFiles(string payloadPath)
    {
        var directory = Path.GetDirectoryName(payloadPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var pattern = Path.GetFileName(payloadPath) + PendingMarker + "*" + ReadySuffix;
        return Directory
            .EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
    }

    private static string? TryGetPayloadPath(string proposalPath)
    {
        var markerIndex = proposalPath.LastIndexOf(PendingMarker, StringComparison.OrdinalIgnoreCase);
        return markerIndex <= 0 ? null : proposalPath[..markerIndex];
    }

    private void CleanupStaleWritingFiles(string storageRoot)
    {
        var staleBefore = DateTimeOffset.UtcNow - _storagePolicy.NormalizedStaleWriteLockTimeout;
        foreach (var path in Directory.EnumerateFiles(storageRoot, "*" + WritingSuffix, SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) <= staleBefore.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A live writer may still own the file.
            }
        }
    }

    private static void CommitProposal(string proposalPath, string payloadPath)
    {
        if (IsDeleteProposal(proposalPath))
        {
            TryDelete(payloadPath);
            TryDelete(proposalPath);
            return;
        }

        if (File.Exists(payloadPath))
        {
            File.Move(proposalPath, payloadPath, overwrite: true);
            return;
        }

        File.Move(proposalPath, payloadPath);
    }

    private static bool IsDeleteProposal(string proposalPath)
        => proposalPath.EndsWith(DeleteReadySuffix, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class FileLease : IAsyncDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private int _disposed;

        public FileLease(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            TryDelete(_path);
        }
    }

    private sealed class MigrationLease : IAsyncDisposable
    {
        private readonly CoalescingFileWriteCoordinator<TContract> _owner;
        private readonly IAsyncDisposable _inner;
        private int _disposed;

        public MigrationLease(CoalescingFileWriteCoordinator<TContract> owner, IAsyncDisposable inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _owner._localMigrationScopeActive, 0);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
