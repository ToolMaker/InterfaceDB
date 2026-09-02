using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Emits debounced file-system changes for committed envelope payload files.
/// </summary>
public sealed class FileEnvelopeStorageChangeSource<TContract> :
    IEnvelopeStorageChangeSource<TContract>,
    IFileEnvelopeStorageChangeBootstrap,
    IDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly StoragePolicy _storagePolicy;
    private readonly StringComparer _pathComparer;
    private readonly ConcurrentDictionary<string, PendingReconciliation> _debounces;
    private readonly ConcurrentDictionary<string, PayloadStamp> _snapshot;
    private readonly SemaphoreSlim _rescanGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly object _bootstrapGate = new();
    private readonly HashSet<string> _bootstrapDirtyPaths;
    private readonly HashSet<string> _bootstrapForcedPaths;
    private readonly Dictionary<string, string> _bootstrapRenameOrigins;
    private FileSystemWatcher? _watcher;
    private Task? _disposalTask;
    private Exception? _bootstrapWatcherError;
    private bool _bootstrapReconcileAll;
    private ChangeSourceState _state;
    private int _started;
    private int _disposed;

    public FileEnvelopeStorageChangeSource(
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _pathComparer = FileSystemPathIdentity.ResolveComparer(contractConfig.DataRoot);
        _debounces = new ConcurrentDictionary<string, PendingReconciliation>(_pathComparer);
        _snapshot = new ConcurrentDictionary<string, PayloadStamp>(_pathComparer);
        _bootstrapDirtyPaths = new HashSet<string>(_pathComparer);
        _bootstrapForcedPaths = new HashSet<string>(_pathComparer);
        _bootstrapRenameOrigins = new Dictionary<string, string>(_pathComparer);
    }

    public event Func<EnvelopeStorageChange, ValueTask>? Changed;

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        StartCore(bufferBootstrapChanges: false, cancellationToken);

    ValueTask IFileEnvelopeStorageChangeBootstrap.StartBufferingAsync(
        CancellationToken cancellationToken) =>
        StartCore(bufferBootstrapChanges: true, cancellationToken);

    private ValueTask StartCore(
        bool bufferBootstrapChanges,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_bootstrapGate)
            {
                if (bufferBootstrapChanges)
                {
                    if (_state == ChangeSourceState.Live)
                    {
                        // A caller started the source independently before repository bootstrap.
                        // Refresh every current path so earlier unobserved publications cannot be
                        // overwritten by the cache walk.
                        _bootstrapReconcileAll = true;
                    }

                    _state = ChangeSourceState.Capturing;
                }
                else if (_state == ChangeSourceState.Dormant)
                {
                    _state = ChangeSourceState.Live;
                }
            }

            if (_started != 0)
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

            // FileSystemWatcher is unsupported on iOS. IsIOS also returns true for
            // Mac Catalyst, where the watcher is supported, so preserve it there.
            if (!_storagePolicy.WatchStorageChanges
                || (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()))
            {
                _started = 1;
                return ValueTask.CompletedTask;
            }

            var watcher = new FileSystemWatcher(dataRoot)
            {
                IncludeSubdirectories = true,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime
            };

            try
            {
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                _started = 1;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    async ValueTask IFileEnvelopeStorageChangeBootstrap.CompleteBootstrapAsync(
        IReadOnlyCollection<string> loadedProviderLocators,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadedProviderLocators);
        string dataRoot;
        lock (_bootstrapGate)
        {
            if (_state == ChangeSourceState.Disposed)
            {
                return;
            }

            if (_state != ChangeSourceState.Capturing)
            {
                throw new InvalidOperationException("The file change source is not capturing a repository bootstrap.");
            }

            _state = ChangeSourceState.Reconciling;
            dataRoot = Path.GetFullPath(_contractConfig.DataRoot
                ?? throw new InvalidOperationException("Contract configuration must supply a DataRoot directory."));
        }

        var current = EnumerateSnapshot(dataRoot);
        Exception? watcherError;
        lock (_bootstrapGate)
        {
            AddSnapshotDifferencesToBootstrapSet(current);
            foreach (var locator in loadedProviderLocators)
            {
                if (string.IsNullOrWhiteSpace(locator))
                {
                    continue;
                }

                var path = Path.GetFullPath(locator);
                if (IsPayloadPath(path) && !current.ContainsKey(path))
                {
                    MarkBootstrapSnapshotDirty(path);
                }
            }

            if (_bootstrapReconcileAll)
            {
                foreach (var path in _snapshot.Keys)
                {
                    MarkBootstrapSnapshotDirty(path, forceCurrentRefresh: true);
                }

                foreach (var locator in loadedProviderLocators
                             .Where(static locator => !string.IsNullOrWhiteSpace(locator))
                             .Select(Path.GetFullPath))
                {
                    MarkBootstrapSnapshotDirty(locator, forceCurrentRefresh: true);
                }

                // Current filesystem spelling is authoritative on case-insensitive volumes.
                foreach (var path in current.Keys)
                {
                    MarkBootstrapSnapshotDirty(path, forceCurrentRefresh: true);
                }

                _bootstrapReconcileAll = false;
            }

            watcherError = _bootstrapWatcherError;
            _bootstrapWatcherError = null;
        }

        if (watcherError is not null)
        {
            _ = await DispatchAsync(new EnvelopeStorageChange(
                EnvelopeStorageChangeType.Error,
                null,
                null,
                watcherError)).ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            string? renameOrigin;
            bool forceCurrentRefresh;
            lock (_bootstrapGate)
            {
                if (_state == ChangeSourceState.Disposed)
                {
                    _bootstrapDirtyPaths.Clear();
                    return;
                }

                if (_bootstrapDirtyPaths.Count == 0)
                {
                    // Watcher callbacks and this hand-off use the same gate. A callback therefore
                    // either entered the dirty set above or observes live mode after this write.
                    _bootstrapForcedPaths.Clear();
                    _bootstrapRenameOrigins.Clear();
                    _state = ChangeSourceState.Live;
                    return;
                }

                path = _bootstrapDirtyPaths.First();
                _bootstrapDirtyPaths.Remove(path);
                forceCurrentRefresh = _bootstrapForcedPaths.Remove(path);
                _bootstrapRenameOrigins.Remove(path, out renameOrigin);
            }

            var reconciled = renameOrigin is null
                ? await ReconcilePathAsync(
                    path,
                    cancellationToken,
                    forceCurrentRefresh).ConfigureAwait(false)
                : await ReconcileRenameAsync(
                    renameOrigin,
                    path,
                    cancellationToken).ConfigureAwait(false);
            if (!reconciled)
            {
                lock (_bootstrapGate)
                {
                    if (renameOrigin is null)
                    {
                        MarkBootstrapDirty(path, forceCurrentRefresh);
                    }
                    else
                    {
                        MarkBootstrapRename(renameOrigin, path);
                    }
                }

                throw new IOException($"Unable to reconcile changed payload '{path}' during repository bootstrap.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_lifecycleGate)
        {
            if (_disposalTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposalTask = DisposeCoreAsync(Interlocked.Exchange(ref _watcher, null));
            }

            disposal = _disposalTask;
        }

        return new ValueTask(disposal);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private Task DisposeCoreAsync(FileSystemWatcher? watcher)
    {
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

        foreach (var debounce in _debounces.Values.Distinct<PendingReconciliation>(
                     ReferenceEqualityComparer.Instance))
        {
            TryCancel(debounce.Cancellation);
        }

        _debounces.Clear();
        _snapshot.Clear();
        lock (_bootstrapGate)
        {
            _state = ChangeSourceState.Disposed;
            _bootstrapDirtyPaths.Clear();
            _bootstrapForcedPaths.Clear();
            _bootstrapRenameOrigins.Clear();
            _bootstrapWatcherError = null;
            _bootstrapReconcileAll = false;
        }

        // Do not wait for or dispose the rescan semaphore here. A Changed handler is allowed to
        // initiate shutdown while RescanAsync owns the gate; waiting would deadlock that handler.
        // SemaphoreSlim has no native resource unless AvailableWaitHandle is requested (it is not).
        return Task.CompletedTask;
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => _ = ObservePath(e.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs e) => _ = ObservePath(e.FullPath);

    private void OnDeleted(object sender, FileSystemEventArgs e) => _ = ObservePath(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        _ = ObserveRename(e.OldFullPath, e.FullPath);

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_bootstrapGate)
        {
            if (_state is ChangeSourceState.Capturing or ChangeSourceState.Reconciling)
            {
                _bootstrapWatcherError = e.GetException();
                _bootstrapReconcileAll = true;
                return;
            }
        }

        _ = RecoverFromErrorAsync(e.GetException());
    }

    public void AcknowledgeLocalChange(string providerLocator)
    {
        // A path alone does not identify the revision committed by the caller. Another process
        // may replace or delete the payload before this method runs; advancing the snapshot here
        // would then suppress that external revision. File change notifications are intentionally
        // at-least-once until the write coordinator can supply an immutable commit fingerprint.
    }

    public async ValueTask RescanAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            return;
        }

        Task gateWait;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            gateWait = _rescanGate.WaitAsync(cancellationToken);
        }

        await gateWait.ConfigureAwait(false);
        try
        {
            var dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
            var current = EnumerateSnapshot(dataRoot);
            string[] dirtyPaths;
            lock (_bootstrapGate)
            {
                dirtyPaths = GetSnapshotDifferences(current);
                if (_state is ChangeSourceState.Capturing or ChangeSourceState.Reconciling)
                {
                    foreach (var path in dirtyPaths)
                    {
                        MarkBootstrapSnapshotDirty(path);
                    }

                    return;
                }
            }

            foreach (var path in dirtyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await ReconcilePathAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _rescanGate.Release();
        }
    }

    internal Task ObservePath(string payloadPath)
    {
        if (!IsPayloadPath(payloadPath))
        {
            return Task.CompletedTask;
        }

        payloadPath = Path.GetFullPath(payloadPath);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Task.CompletedTask;
            }

            lock (_bootstrapGate)
            {
                if (_state is ChangeSourceState.Capturing or ChangeSourceState.Reconciling)
                {
                    // Capture synchronously, before debounce or local-echo suppression. The final
                    // reconciliation reads current truth and coalesces repeated signals by path.
                    if (!RefreshBufferedRename(payloadPath))
                    {
                        MarkBootstrapDirty(payloadPath, forceCurrentRefresh: true);
                    }

                    return Task.CompletedTask;
                }

                if (_state != ChangeSourceState.Live)
                {
                    return Task.CompletedTask;
                }
            }

            if (_debounces.TryGetValue(payloadPath, out var existing)
                && existing.RenameOrigin is not null)
            {
                return ScheduleReconciliation(existing.RenameOrigin, existing.CurrentPath);
            }

            return ScheduleReconciliation(renameOrigin: null, payloadPath);
        }
    }

    internal Task ObserveRename(string oldPayloadPath, string newPayloadPath)
    {
        var oldIsPayload = IsPayloadPath(oldPayloadPath);
        var newIsPayload = IsPayloadPath(newPayloadPath);
        if (!oldIsPayload || !newIsPayload)
        {
            if (oldIsPayload)
            {
                return ObservePath(oldPayloadPath);
            }

            if (newIsPayload)
            {
                return ObservePath(newPayloadPath);
            }

            return Task.CompletedTask;
        }

        oldPayloadPath = Path.GetFullPath(oldPayloadPath);
        newPayloadPath = Path.GetFullPath(newPayloadPath);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Task.CompletedTask;
            }

            lock (_bootstrapGate)
            {
                if (_state is ChangeSourceState.Capturing or ChangeSourceState.Reconciling)
                {
                    MarkBootstrapRename(oldPayloadPath, newPayloadPath);
                    return Task.CompletedTask;
                }

                if (_state != ChangeSourceState.Live)
                {
                    return Task.CompletedTask;
                }
            }

            var renameOrigin = oldPayloadPath;
            if (_debounces.TryGetValue(oldPayloadPath, out var existing)
                && existing.RenameOrigin is not null
                && _pathComparer.Equals(existing.CurrentPath, oldPayloadPath))
            {
                // Coalesce A -> B -> C into one current-state refresh for A -> C.
                renameOrigin = existing.RenameOrigin;
            }

            return ScheduleReconciliation(renameOrigin, newPayloadPath);
        }
    }

    private Task ScheduleReconciliation(string? renameOrigin, string currentPath)
    {
        var superseded = new HashSet<PendingReconciliation>(ReferenceEqualityComparer.Instance);
        if (_debounces.TryGetValue(currentPath, out var currentPending))
        {
            superseded.Add(currentPending);
        }

        if (renameOrigin is not null
            && _debounces.TryGetValue(renameOrigin, out var originPending))
        {
            superseded.Add(originPending);
        }

        foreach (var pending in superseded)
        {
            DetachPendingReconciliation(pending);
            // The delayed publisher owns disposal. Replacers and shutdown only cancel it,
            // which avoids racing a future read of Cancellation.Token.
            TryCancel(pending.Cancellation);
        }

        var replacement = new PendingReconciliation(renameOrigin, currentPath);
        _debounces[currentPath] = replacement;
        if (renameOrigin is not null)
        {
            _debounces[renameOrigin] = replacement;
        }

        _ = ReconcileAfterDelayAsync(replacement);
        return replacement.Settled.Task;
    }

    private async Task ReconcileAfterDelayAsync(PendingReconciliation pending)
    {
        try
        {
            await Task.Delay(
                _storagePolicy.NormalizedStorageChangeDebounce,
                pending.Cancellation.Token).ConfigureAwait(false);
            _ = pending.RenameOrigin is null
                ? await ReconcilePathAsync(
                    pending.CurrentPath,
                    pending.Cancellation.Token,
                    forceCurrentRefresh: true).ConfigureAwait(false)
                : await ReconcileRenameAsync(
                    pending.RenameOrigin,
                    pending.CurrentPath,
                    pending.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lifecycleGate)
            {
                DetachPendingReconciliation(pending);
            }

            pending.Cancellation.Dispose();
            pending.Settled.TrySetResult();
        }
    }

    private void DetachPendingReconciliation(PendingReconciliation pending)
    {
        _debounces.TryRemove(
            new KeyValuePair<string, PendingReconciliation>(pending.CurrentPath, pending));
        if (pending.RenameOrigin is not null)
        {
            _debounces.TryRemove(
                new KeyValuePair<string, PendingReconciliation>(pending.RenameOrigin, pending));
        }
    }

    private async Task RecoverFromErrorAsync(Exception? error)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

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

    private async ValueTask<bool> ReconcilePathAsync(
        string path,
        CancellationToken cancellationToken,
        bool forceCurrentRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        path = Path.GetFullPath(path);
        PayloadStamp? currentStamp;
        try
        {
            currentStamp = File.Exists(path)
                ? PayloadStamp.From(new FileInfo(path))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var hadPriorRevision = _snapshot.TryGetValue(path, out var priorStamp);
        if ((currentStamp is null && !hadPriorRevision)
            || (!forceCurrentRefresh
                && currentStamp is not null
                && hadPriorRevision
                && currentStamp.Value == priorStamp))
        {
            return true;
        }

        var isCasePresentationRename = currentStamp is not null
            && hadPriorRevision
            && _pathComparer.Equals(currentStamp.Value.ProviderLocator, priorStamp.ProviderLocator)
            && !StringComparer.Ordinal.Equals(currentStamp.Value.ProviderLocator, priorStamp.ProviderLocator);
        var change = currentStamp is null
            ? new EnvelopeStorageChange(
                EnvelopeStorageChangeType.Deleted,
                hadPriorRevision ? priorStamp.ProviderLocator : path,
                null)
            : isCasePresentationRename
                ? new EnvelopeStorageChange(
                    EnvelopeStorageChangeType.Renamed,
                    priorStamp.ProviderLocator,
                    currentStamp.Value.ProviderLocator)
                : new EnvelopeStorageChange(
                    hadPriorRevision ? EnvelopeStorageChangeType.Changed : EnvelopeStorageChangeType.Created,
                    null,
                    path);
        if (!await DispatchAsync(change).ConfigureAwait(false))
        {
            return false;
        }

        // The snapshot advances only after every registered handler accepts the current-state
        // refresh. A failed read/apply therefore remains visible to the next signal or rescan.
        ApplySnapshot(path, currentStamp);
        return true;
    }

    private async ValueTask<bool> ReconcileRenameAsync(
        string oldPath,
        string newPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        oldPath = Path.GetFullPath(oldPath);
        newPath = Path.GetFullPath(newPath);
        if (_pathComparer.Equals(oldPath, newPath))
        {
            // Case-only renames share one snapshot key on case-insensitive volumes. The provider
            // locator retained in the stamp still lets ReconcilePathAsync publish Renamed.
            return await ReconcilePathAsync(
                newPath,
                cancellationToken,
                forceCurrentRefresh: true).ConfigureAwait(false);
        }

        PayloadStamp? oldStamp;
        PayloadStamp? newStamp;
        try
        {
            oldStamp = File.Exists(oldPath)
                ? PayloadStamp.From(new FileInfo(oldPath))
                : null;
            newStamp = File.Exists(newPath)
                ? PayloadStamp.From(new FileInfo(newPath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (newStamp is not null)
        {
            var priorLocator = _snapshot.TryGetValue(oldPath, out var priorStamp)
                ? priorStamp.ProviderLocator
                : oldPath;
            if (!await DispatchAsync(new EnvelopeStorageChange(
                    EnvelopeStorageChangeType.Renamed,
                    priorLocator,
                    newStamp.Value.ProviderLocator)).ConfigureAwait(false))
            {
                return false;
            }

            ApplySnapshot(oldPath, currentStamp: null);
            ApplySnapshot(newPath, newStamp);

            // The source name may have been recreated while the rename was debounced. Refresh it
            // after installing the destination so both current payloads remain represented.
            return oldStamp is null
                || await ReconcilePathAsync(
                    oldPath,
                    cancellationToken,
                    forceCurrentRefresh: true).ConfigureAwait(false);
        }

        // A rename followed by deletion (or a rename that was reversed before debounce elapsed)
        // is reconciled from current truth without publishing a stale destination.
        var oldSucceeded = await ReconcilePathAsync(
            oldPath,
            cancellationToken,
            forceCurrentRefresh: true).ConfigureAwait(false);
        var newSucceeded = await ReconcilePathAsync(
            newPath,
            cancellationToken,
            forceCurrentRefresh: true).ConfigureAwait(false);
        return oldSucceeded && newSucceeded;
    }

    private void ApplySnapshot(string path, PayloadStamp? currentStamp)
    {
        if (currentStamp is null)
        {
            _snapshot.TryRemove(path, out _);
            return;
        }

        _snapshot[path] = currentStamp.Value;
    }

    private void AddSnapshotDifferencesToBootstrapSet(
        IReadOnlyDictionary<string, PayloadStamp> current)
    {
        foreach (var path in GetSnapshotDifferences(current))
        {
            MarkBootstrapSnapshotDirty(path);
        }
    }

    private void MarkBootstrapSnapshotDirty(string path, bool forceCurrentRefresh = false)
    {
        if (_bootstrapRenameOrigins.ContainsKey(path))
        {
            MarkBootstrapDirty(path, forceCurrentRefresh);
            return;
        }

        if (_bootstrapRenameOrigins.Any(rename => _pathComparer.Equals(rename.Value, path)))
        {
            // The paired rename refresh owns the old path. Processing it independently first
            // would degrade a single rename into Deleted followed by Renamed/Created.
            return;
        }

        MarkBootstrapDirty(path, forceCurrentRefresh);
    }

    private bool RefreshBufferedRename(string path)
    {
        foreach (var rename in _bootstrapRenameOrigins)
        {
            if (!_pathComparer.Equals(rename.Key, path)
                && !_pathComparer.Equals(rename.Value, path))
            {
                continue;
            }

            MarkBootstrapDirty(rename.Key, forceCurrentRefresh: true);
            return true;
        }

        return false;
    }

    private void MarkBootstrapDirty(string path, bool forceCurrentRefresh = false)
    {
        // Remove first so a case-insensitive set retains the latest filesystem spelling rather
        // than the first spelling observed during a case-only rename.
        _bootstrapDirtyPaths.Remove(path);
        _bootstrapDirtyPaths.Add(path);
        if (forceCurrentRefresh)
        {
            _bootstrapForcedPaths.Add(path);
        }
    }

    private void MarkBootstrapRename(string oldPath, string newPath)
    {
        var renameOrigin = oldPath;
        if (_bootstrapRenameOrigins.Remove(oldPath, out var earlierOrigin))
        {
            renameOrigin = earlierOrigin;
            _bootstrapDirtyPaths.Remove(oldPath);
            _bootstrapForcedPaths.Remove(oldPath);
        }

        _bootstrapRenameOrigins[newPath] = renameOrigin;
        MarkBootstrapDirty(newPath, forceCurrentRefresh: true);
    }

    private string[] GetSnapshotDifferences(IReadOnlyDictionary<string, PayloadStamp> current)
    {
        var dirty = new HashSet<string>(_pathComparer);
        foreach (var prior in _snapshot)
        {
            if (!current.ContainsKey(prior.Key))
            {
                dirty.Add(prior.Key);
            }
        }

        foreach (var item in current)
        {
            if (!_snapshot.TryGetValue(item.Key, out var prior) || prior != item.Value)
            {
                dirty.Add(item.Key);
            }
        }

        return [.. dirty];
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
            return new Dictionary<string, PayloadStamp>(_pathComparer);
        }

        return Directory
            .EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories)
            .Where(IsPayloadPath)
            .Select(path => new FileInfo(path))
            .ToDictionary(
                info => info.FullName,
                PayloadStamp.From,
                _pathComparer);
    }

    private ValueTask<bool> PublishAsync(EnvelopeStorageChange change) => DispatchAsync(change);

    private async ValueTask<bool> DispatchAsync(EnvelopeStorageChange change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return true;
        }

        var succeeded = true;
        foreach (Func<EnvelopeStorageChange, ValueTask> handler in handlers.GetInvocationList())
        {
            try
            {
                await handler(change).ConfigureAwait(false);
            }
            catch
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delayed publisher won the removal/disposal race.
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

    private readonly record struct PayloadStamp(
        long Length,
        DateTime LastWriteUtc,
        string ProviderLocator)
    {
        internal static PayloadStamp From(FileInfo info)
        {
            info.Refresh();
            return new PayloadStamp(info.Length, info.LastWriteTimeUtc, info.FullName);
        }
    }

    private sealed class PendingReconciliation(string? renameOrigin, string currentPath)
    {
        internal string? RenameOrigin { get; } = renameOrigin;

        internal string CurrentPath { get; } = currentPath;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource Settled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum ChangeSourceState
    {
        Dormant,
        Capturing,
        Reconciling,
        Live,
        Disposed
    }
}
