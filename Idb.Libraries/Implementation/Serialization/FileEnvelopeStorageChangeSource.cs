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
    private FileSystemWatcher? _watcher;
    private int _started;

    public FileEnvelopeStorageChangeSource(IDbContractConfigDto<TContract> contractConfig)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _storagePolicy = contractConfig.StoragePolicy ?? StoragePolicy.Default;
    }

    public event Func<EnvelopeStorageChange, ValueTask>? Changed;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_storagePolicy.WatchStorageChanges || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        var dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(dataRoot);

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
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Created, null, e.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Changed, null, e.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs e)
        => Debounce(e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Deleted, e.FullPath, null));

    private void OnRenamed(object sender, RenamedEventArgs e)
        => Debounce(e.FullPath, new EnvelopeStorageChange(EnvelopeStorageChangeType.Renamed, e.OldFullPath, e.FullPath));

    private void OnError(object sender, ErrorEventArgs e)
        => _ = PublishAsync(new EnvelopeStorageChange(EnvelopeStorageChangeType.Error, null, null, e.GetException()));

    private void Debounce(string path, EnvelopeStorageChange change)
    {
        if (!IsPayloadPath(path))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var prior = _debounces.AddOrUpdate(path, cts, (_, existing) =>
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

        _ = PublishAfterDelayAsync(path, change, cts);
    }

    private async Task PublishAfterDelayAsync(
        string path,
        EnvelopeStorageChange change,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_storagePolicy.NormalizedStorageChangeDebounce, cts.Token).ConfigureAwait(false);
            await PublishAsync(change).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _debounces.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts));
            cts.Dispose();
        }
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
}
