using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Emits debounced file-system changes for committed envelope payload files.
/// </summary>
public sealed class FileEnvelopeStorageChangeSource<TContract> : IEnvelopeStorageChangeSource<TContract>, IDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly StoragePolicy _storagePolicy;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localSuppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PayloadStamp> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _rescanGate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private int _started;

    public FileEnvelopeStorageChangeSource(
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
    }

    public event Func<EnvelopeStorageChange, ValueTask>? Changed;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        var dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(dataRoot);
        CaptureSnapshot(dataRoot);

        if (!_storagePolicy.WatchStorageChanges)
        {
            return ValueTask.CompletedTask;
        }

        _watcher = new FileSystemWatcher(dataRoot)
        {
            IncludeSubdirectories = true,
            Filter = "*",
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        foreach (var debounce in _debounces.Values)
        {
            debounce.Cancel();
            debounce.Dispose();
        }

        _debounces.Clear();
        _localSuppressions.Clear();
        _snapshot.Clear();
        _rescanGate.Dispose();
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Created, null, e.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Changed, null, e.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Deleted, e.FullPath, null));

    private void OnRenamed(object sender, RenamedEventArgs e)
        => Debounce(
            e.OldFullPath + "\0" + e.FullPath,
            e.FullPath,
            new EnvelopeStorageChange(EnvelopeStorageChangeType.Renamed, e.OldFullPath, e.FullPath));

    private void OnError(object sender, ErrorEventArgs e)
        => _ = RecoverFromErrorAsync(e.GetException());

    public void AcknowledgeLocalChange(string providerLocator)
    {
        if (string.IsNullOrWhiteSpace(providerLocator))
        {
            return;
        }

        _localSuppressions[Path.GetFullPath(providerLocator)] =
            DateTimeOffset.UtcNow + _storagePolicy.NormalizedLocalStorageChangeSuppression;
    }

    public async ValueTask RescanAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            return;
        }

        await _rescanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
            var current = EnumerateSnapshot(dataRoot);

            foreach (var prior in _snapshot.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.ContainsKey(prior.Key))
                {
                    _snapshot.TryRemove(prior.Key, out _);
                    await PublishAsync(new EnvelopeStorageChange(
                        EnvelopeStorageChangeType.Deleted,
                        prior.Key,
                        null)).ConfigureAwait(false);
                }
            }

            foreach (var item in current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_snapshot.TryGetValue(item.Key, out var prior))
                {
                    _snapshot[item.Key] = item.Value;
                    await PublishAsync(new EnvelopeStorageChange(
                        EnvelopeStorageChangeType.Created,
                        null,
                        item.Key)).ConfigureAwait(false);
                }
                else if (!prior.Equals(item.Value))
                {
                    _snapshot[item.Key] = item.Value;
                    await PublishAsync(new EnvelopeStorageChange(
                        EnvelopeStorageChangeType.Changed,
                        null,
                        item.Key)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _rescanGate.Release();
        }
    }

    private void Debounce(string debounceKey, string payloadPath, EnvelopeStorageChange change)
    {
        if (!IsPayloadPath(payloadPath))
        {
            return;
        }

        payloadPath = Path.GetFullPath(payloadPath);
        if (IsLocallySuppressed(payloadPath))
        {
            UpdateSnapshotForSuppressedChange(payloadPath, change.ChangeType);
            return;
        }

        var cts = new CancellationTokenSource();
        var prior = _debounces.AddOrUpdate(debounceKey, cts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return cts;
        });

        if (!ReferenceEquals(prior, cts))
        {
            cts.Dispose();
            return;
        }

        _ = PublishAfterDelayAsync(debounceKey, payloadPath, change, cts);
    }

    private async Task PublishAfterDelayAsync(
        string debounceKey,
        string payloadPath,
        EnvelopeStorageChange change,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_storagePolicy.NormalizedStorageChangeDebounce, cts.Token).ConfigureAwait(false);
            if (IsLocallySuppressed(payloadPath))
            {
                UpdateSnapshotForSuppressedChange(payloadPath, change.ChangeType);
                return;
            }

            if (change.ChangeType == EnvelopeStorageChangeType.Renamed
                && !string.IsNullOrWhiteSpace(change.OldPath))
            {
                _snapshot.TryRemove(Path.GetFullPath(change.OldPath), out _);
            }

            UpdateSnapshot(payloadPath, change.ChangeType);
            await PublishAsync(change).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _debounces.TryRemove(new KeyValuePair<string, CancellationTokenSource>(debounceKey, cts));
            cts.Dispose();
        }
    }

    private async Task RecoverFromErrorAsync(Exception? error)
    {
        await PublishAsync(new EnvelopeStorageChange(
            EnvelopeStorageChangeType.Error,
            null,
            null,
            error)).ConfigureAwait(false);
        try
        {
            await RescanAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private bool IsLocallySuppressed(string path)
    {
        if (!_localSuppressions.TryGetValue(path, out var expires))
        {
            return false;
        }

        if (expires >= DateTimeOffset.UtcNow)
        {
            return true;
        }

        _localSuppressions.TryRemove(path, out _);
        return false;
    }

    private void UpdateSnapshotForSuppressedChange(string path, EnvelopeStorageChangeType changeType)
    {
        UpdateSnapshot(path, changeType);
    }

    private void UpdateSnapshot(string path, EnvelopeStorageChangeType changeType)
    {
        if (changeType == EnvelopeStorageChangeType.Deleted || !File.Exists(path))
        {
            _snapshot.TryRemove(path, out _);
            return;
        }

        var info = new FileInfo(path);
        _snapshot[path] = PayloadStamp.From(info);
    }

    private void CaptureSnapshot(string dataRoot)
    {
        _snapshot.Clear();
        foreach (var item in EnumerateSnapshot(dataRoot))
        {
            _snapshot[item.Key] = item.Value;
        }
    }

    private Dictionary<string, PayloadStamp> EnumerateSnapshot(string dataRoot)
    {
        if (!Directory.Exists(dataRoot))
        {
            return new Dictionary<string, PayloadStamp>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory
            .EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories)
            .Where(IsPayloadPath)
            .Select(path => new FileInfo(path))
            .ToDictionary(
                info => info.FullName,
                PayloadStamp.From,
                StringComparer.OrdinalIgnoreCase);
    }

    private async ValueTask PublishAsync(EnvelopeStorageChange change)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<EnvelopeStorageChange, ValueTask> handler in handlers.GetInvocationList())
        {
            try
            {
                await handler(change).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private bool IsPayloadPath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".pending.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = _contractConfig.FileExtension.NormalizeExtension();
        return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct PayloadStamp(long Length, DateTime LastWriteUtc)
    {
        internal static PayloadStamp From(FileInfo info)
        {
            info.Refresh();
            return new PayloadStamp(info.Length, info.LastWriteTimeUtc);
        }
    }
}
