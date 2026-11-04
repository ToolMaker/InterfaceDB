using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Startup gate and explicit availability contract composed around a repository.</summary>
public sealed class StorageMigrationRepository<T>(IStorageMigration<T> migration) :
    IEnvelopeMetadataRepository<T>, IRevisionEditingRepository<T>, IRepositoryNotifications<T>,
    IStorageMigrationProgress<T>, IRepositoryDataAvailability
    where T : class, IStoredContract<T>
{
    private IRepository<T>? _repository;
    public StorageMigrationProgress Progress => (migration as IStorageMigrationProgress)?.Progress
        ?? new(Volatile.Read(ref _repository) is null ? StorageMigrationPhase.NotStarted : StorageMigrationPhase.Complete, 0, 0, 0);
    public bool IsDataComplete => Progress.Phase == StorageMigrationPhase.Complete;
    public ValueTask RetryAsync(CancellationToken cancellationToken = default)
        => (migration as IStorageMigrationProgress ?? throw new NotSupportedException("Migration does not support retry.")).RetryAsync(cancellationToken);
    private IRepository<T> Ready => Volatile.Read(ref _repository)
        ?? throw new InvalidOperationException("Await repository.InitializeAsync before inspecting provider capabilities.");
    private IStorageMigrationCapabilities Configuration => migration as IStorageMigrationCapabilities
        ?? throw new InvalidOperationException("This migration does not expose configuration-time capabilities; initialize it first.");
    public ContractRepositoryType RepositoryType => Volatile.Read(ref _repository)?.RepositoryType ?? Configuration.RepositoryType;
    public string? RunningVersion => Volatile.Read(ref _repository)?.RunningVersion ?? Configuration.RunningVersion;
    public StorageDurabilityMode CommitDurability => Volatile.Read(ref _repository) is null ? Configuration.CommitDurability : Capability<IEnvelopeMetadataRepository<T>>().CommitDurability;
    public RetainedVersionCapability RetainedVersions => Volatile.Read(ref _repository) is null ? Configuration.RetainedVersions : Capability<IRevisionEditingRepository<T>>().RetainedVersions;
    public RetainedVersionStatistics RetentionStatistics => Volatile.Read(ref _repository) is null ? Configuration.RetentionStatistics : Capability<IRevisionEditingRepository<T>>().RetentionStatistics;
    private TC Capability<TC>() where TC : class => Ready as TC ?? throw new NotSupportedException($"Selected repository does not provide {typeof(TC).Name}.");
    private async ValueTask<IRepository<T>> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        (migration as IStorageMigrationAccess)?.EnsureReadable();
        if (Volatile.Read(ref _repository) is { } ready) return ready;
        var selected = await migration.OpenAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _repository, selected);
        return selected;
    }
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) => await GetAsync(cancellationToken).ConfigureAwait(false);
    private async ValueTask<IAsyncDisposable?> WriteAsync(IEnumerable<string?> keys, CancellationToken ct)
        => migration is IStorageMigrationAccess access ? await access.AcquireWriteAsync(keys, ct).ConfigureAwait(false) : null;
    public async ValueTask<T> SaveAsync(T entityToSave, CancellationToken cancellationToken = default)
    {
        var repository = await GetAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriteAsync([entityToSave.Envelope?.EnvelopeCacheKey], cancellationToken).ConfigureAwait(false);
        return await repository.SaveAsync(entityToSave, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<T>> SaveManyAsync(IEnumerable<T> entitiesToSave, CancellationToken cancellationToken = default)
    {
        var repository = await GetAsync(cancellationToken).ConfigureAwait(false);
        if (migration is not IStorageMigrationAccess)
            return await repository.SaveManyAsync(entitiesToSave, cancellationToken).ConfigureAwait(false);
        var entities = entitiesToSave.ToArray();
        await using var lease = await WriteAsync(entities.Select(e => e.Envelope?.EnvelopeCacheKey), cancellationToken).ConfigureAwait(false);
        return await repository.SaveManyAsync(entities, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<bool> DeleteAsync(T entityToDelete, CancellationToken cancellationToken = default)
    {
        var repository = await GetAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriteAsync([entityToDelete.Envelope?.EnvelopeCacheKey], cancellationToken).ConfigureAwait(false);
        return await repository.DeleteAsync(entityToDelete, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<bool> ExistsAsync(T entityToCheck, CancellationToken cancellationToken = default)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).ExistsAsync(entityToCheck, cancellationToken).ConfigureAwait(false);
    public async ValueTask<T?> QueryAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryAsync(filterFunction, cancellationToken).ConfigureAwait(false);
    public async ValueTask<TI?> QueryAsync<TI>(Func<TI, bool> filterFunction, CancellationToken cancellationToken = default) where TI : class, T
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryAsync(filterFunction, cancellationToken).ConfigureAwait(false);
    public async IAsyncEnumerable<T> QueryManyAsync(Func<T, bool> filterFunction, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var item in (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryManyAsync(filterFunction, cancellationToken).ConfigureAwait(false)) yield return item; }
    public async IAsyncEnumerable<TI> QueryManyAsync<TI>(Func<TI, bool> filterFunction, [EnumeratorCancellation] CancellationToken cancellationToken = default) where TI : class, T
    { await foreach (var item in (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryManyAsync(filterFunction, cancellationToken).ConfigureAwait(false)) yield return item; }
    public async IAsyncEnumerable<T> SearchManyAsync(Expression<Func<T, bool>> filterExpression, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var item in (await GetAsync(cancellationToken).ConfigureAwait(false)).SearchManyAsync(filterExpression, cancellationToken).ConfigureAwait(false)) yield return item; }
    public async IAsyncEnumerable<TI> SearchManyAsync<TI>(Expression<Func<TI, bool>> filterExpression, [EnumeratorCancellation] CancellationToken cancellationToken = default) where TI : class, T
    { await foreach (var item in (await GetAsync(cancellationToken).ConfigureAwait(false)).SearchManyAsync(filterExpression, cancellationToken).ConfigureAwait(false)) yield return item; }
    public async IAsyncEnumerable<T> QueryManyByStorageKeyAsync(string storageKey, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var item in (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryManyByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false)) yield return item; }
    public async ValueTask<T?> QueryFirstAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false);
    public async ValueTask<TI?> QueryFirstAsync<TI>(Func<TI, bool> filterFunction, CancellationToken cancellationToken = default) where TI : class, T
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false);
    public async ValueTask<T?> SearchFirstAsync(Expression<Func<T, bool>> filterExpression, CancellationToken cancellationToken = default)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).SearchFirstAsync(filterExpression, cancellationToken).ConfigureAwait(false);
    public async ValueTask<TI?> SearchFirstAsync<TI>(Expression<Func<TI, bool>> filterExpression, CancellationToken cancellationToken = default) where TI : class, T
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).SearchFirstAsync(filterExpression, cancellationToken).ConfigureAwait(false);
    public async ValueTask<T?> QueryFirstByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QueryFirstByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false);
    public async ValueTask<T> QuerySingleAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory).ConfigureAwait(false);
    public async ValueTask<TI> QuerySingleAsync<TI>(Func<TI, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null) where TI : class, T
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory).ConfigureAwait(false);
    public async ValueTask<T?> QuerySingleOrDefaultAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? multipleExceptionFactory = null)
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory).ConfigureAwait(false);
    public async ValueTask<TI?> QuerySingleOrDefaultAsync<TI>(Func<TI, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? multipleExceptionFactory = null) where TI : class, T
        => await (await GetAsync(cancellationToken).ConfigureAwait(false)).QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory).ConfigureAwait(false);
    public async ValueTask<T> SaveWithExtensionMetadataAsync(T detachedEntity, string? existingEnvelopeCacheKey, byte[]? extensionMetadata, EnvelopeWriteCondition condition, CancellationToken cancellationToken = default)
    {
        await GetAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriteAsync([existingEnvelopeCacheKey, detachedEntity.Envelope?.EnvelopeCacheKey], cancellationToken).ConfigureAwait(false);
        return await Capability<IEnvelopeMetadataRepository<T>>().SaveWithExtensionMetadataAsync(detachedEntity, existingEnvelopeCacheKey, extensionMetadata, condition, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<T> SaveWithExtensionMetadataAsync(T detachedEntity, string? existingEnvelopeCacheKey, byte[]? extensionMetadata, CancellationToken cancellationToken = default)
    {
        await GetAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriteAsync([existingEnvelopeCacheKey, detachedEntity.Envelope?.EnvelopeCacheKey], cancellationToken).ConfigureAwait(false);
        return await Capability<IEnvelopeMetadataRepository<T>>().SaveWithExtensionMetadataAsync(detachedEntity, existingEnvelopeCacheKey, extensionMetadata, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<IRevisionEditSession<T>> BeginEditAsync(string physicalKey, CancellationToken cancellationToken = default)
    {
        await GetAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriteAsync([physicalKey], cancellationToken).ConfigureAwait(false);
        var session = await Capability<IRevisionEditingRepository<T>>().BeginEditAsync(physicalKey, cancellationToken).ConfigureAwait(false);
        return migration is IStorageMigrationAccess access ? new GatedEditSession(session, access, physicalKey) : session;
    }

    private sealed class GatedEditSession(IRevisionEditSession<T> inner, IStorageMigrationAccess access, string key) : IRevisionEditSession<T>
    {
        public T Working => inner.Working;
        public RetainedVersionReference OriginalVersion => inner.OriginalVersion;
        public long ExpectedRevision => inner.ExpectedRevision;
        public bool IsCompleted => inner.IsCompleted;
        public async ValueTask<T> SaveAsync(CancellationToken cancellationToken = default)
        {
            await using var lease = await access.AcquireWriteAsync([key], cancellationToken).ConfigureAwait(false);
            return await inner.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        public ValueTask<T> ReadOriginalAsync(CancellationToken cancellationToken = default)
        { access.EnsureReadable(); return inner.ReadOriginalAsync(cancellationToken); }
        public void Renew(TimeSpan duration) { access.EnsureReadable(); inner.Renew(duration); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    public IRepositorySubscription SubscribeCreated(Func<T, ValueTask> handler, Expression<Func<T, bool>>? filter = null,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => Subscribe(p => p.SubscribeCreated(handler, filter, lifetime, keepAlive, receiveOwnWrites), lifetime);
    public IRepositorySubscription SubscribeChanged(Func<RepositoryChange<T>, ValueTask> handler, Expression<Func<T, bool>> filter,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => Subscribe(p => p.SubscribeChanged(handler, filter, lifetime, keepAlive, receiveOwnWrites), lifetime);
    public IRepositorySubscription SubscribeDeleted(Func<T, ValueTask> handler, Expression<Func<T, bool>>? filter = null,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => Subscribe(p => p.SubscribeDeleted(handler, filter, lifetime, keepAlive, receiveOwnWrites), lifetime);

    private IRepositorySubscription Subscribe(Func<IRepositoryNotifications<T>, IRepositorySubscription> subscribe, CancellationToken lifetime)
        => new MigrationSubscription(async ct =>
        {
            await GetAsync(ct).ConfigureAwait(false);
            return subscribe(Capability<IRepositoryNotifications<T>>());
        }, lifetime);
}

internal sealed class MigrationSubscription : IRepositorySubscription
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RepositorySubscriptionEndReason> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRepositorySubscription? _inner;
    private bool _disposed;
    public MigrationSubscription(Func<CancellationToken, ValueTask<IRepositorySubscription>> create, CancellationToken lifetime)
    { _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime); _ = StartAsync(create); }
    public Task Ready => _ready.Task;
    public Task<RepositorySubscriptionEndReason> Completion => _completion.Task;
    public bool IsDisposed { get { lock (_gate) return _disposed || _inner?.IsDisposed == true; } }
    public long DeliveredCount => _inner?.DeliveredCount ?? 0;
    public long FilteredCount => _inner?.FilteredCount ?? 0;
    public IDisposable BeginWriteScope() => Ready.IsCompletedSuccessfully && _inner is { } inner
        ? inner.BeginWriteScope() : throw new InvalidOperationException("Await subscription.Ready before opening a write scope.");
    public async ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default)
    { await Ready.WaitAsync(cancellationToken).ConfigureAwait(false); await _inner!.WaitForIdleAsync(cancellationToken).ConfigureAwait(false); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _inner?.Dispose();
        }
    }
    private async Task StartAsync(Func<CancellationToken, ValueTask<IRepositorySubscription>> create)
    {
        try
        {
            var inner = await create(_stop.Token).ConfigureAwait(false);
            lock (_gate) { _inner = inner; if (_disposed || _stop.IsCancellationRequested) inner.Dispose(); }
            await inner.Ready.ConfigureAwait(false);
            _ready.TrySetResult();
            _completion.TrySetResult(await inner.Completion.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        { _ready.TrySetCanceled(); _completion.TrySetResult(_disposed ? RepositorySubscriptionEndReason.Disposed : RepositorySubscriptionEndReason.Cancelled); }
        catch (Exception error)
        { _inner?.Dispose(); _ready.TrySetException(error); _completion.TrySetResult(RepositorySubscriptionEndReason.ObservationFailed); }
        finally { lock (_gate) { _disposed = true; _stop.Dispose(); } }
    }
}
