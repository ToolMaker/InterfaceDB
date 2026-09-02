using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchIndexManagerInitializationTests
{
    [TestMethod]
    public async Task InitializeAsync_ConcurrentAndRepeatedCallersShareOneInitialization()
    {
        var repository = new BlockingEnvelopeRepository(CreateRecord("row-1", "alpha"));
        await using var manager = new SearchIndexManager<InitializationRecord>(
            repository,
            StoragePolicy.Default);

        var first = manager.InitializeAsync().AsTask();
        await repository.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var concurrent = Enumerable.Range(0, 31)
            .Select(_ => manager.InitializeAsync().AsTask())
            .ToArray();
        using var canceledWait = new CancellationTokenSource();
        var canceledCaller = manager.InitializeAsync(canceledWait.Token).AsTask();
        canceledWait.Cancel();
        try
        {
            _ = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceledCaller);
            Assert.IsFalse(first.IsCompleted, "Canceling one waiter must not cancel shared initialization.");
        }
        finally
        {
            repository.ReleaseInitialization();
        }

        await Task.WhenAll(concurrent.Prepend(first));
        await manager.InitializeAsync();

        Assert.AreEqual(1, repository.InitializationEnumerationCount);
        Assert.AreEqual(1, repository.InitializedEnvelopeCount);
        Assert.IsFalse(repository.InitializationTokenCanBeCanceled);
        Assert.AreEqual(2, repository.SubscriptionCount);

        var matches = new List<InitializationRecord>();
        await foreach (var match in manager.SearchAsync(record => record.Name == "alpha"))
        {
            matches.Add(match);
        }

        Assert.HasCount(1, matches);
        Assert.AreEqual("row-1", matches[0].Id);
    }

    [TestMethod]
    public async Task InitializeAsync_ReplaysStoredMutationPublishedDuringSnapshotRebuild()
    {
        var repository = new BlockingEnvelopeRepository(CreateRecord("row-1", "alpha"));
        await using var manager = new SearchIndexManager<InitializationRecord>(
            repository,
            StoragePolicy.Default);

        var initialization = manager.InitializeAsync().AsTask();
        await repository.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // The initialization stream already captured the old envelope. This publication must
            // therefore be replayed after that stale snapshot is indexed.
            await repository.PublishStoredAsync(CreateRecord("row-1", "beta"));
        }
        finally
        {
            repository.ReleaseInitialization();
        }

        await initialization;

        var oldMatches = new List<InitializationRecord>();
        await foreach (var match in manager.SearchAsync(record => record.Name == "alpha"))
        {
            oldMatches.Add(match);
        }

        var newMatches = new List<InitializationRecord>();
        await foreach (var match in manager.SearchAsync(record => record.Name == "beta"))
        {
            newMatches.Add(match);
        }

        Assert.IsEmpty(oldMatches);
        Assert.HasCount(1, newMatches);
        Assert.AreEqual("row-1", newMatches[0].Id);
    }

    [TestMethod]
    public async Task InitializeAsync_CoalescesDeleteAndRecreationPublishedDuringSnapshotRebuild()
    {
        var repository = new BlockingEnvelopeRepository(CreateRecord("row-1", "alpha"));
        await using var manager = new SearchIndexManager<InitializationRecord>(
            repository,
            StoragePolicy.Default);

        var initialization = manager.InitializeAsync().AsTask();
        await repository.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await repository.PublishStoredAsync(CreateRecord("row-1", "beta"));
            await repository.PublishDeletedAsync("row-1");
            await repository.PublishStoredAsync(CreateRecord("row-1", "gamma"));
        }
        finally
        {
            repository.ReleaseInitialization();
        }

        await initialization;

        Assert.IsEmpty(await SearchByNameAsync(manager, "alpha"));
        Assert.IsEmpty(await SearchByNameAsync(manager, "beta"));
        var matches = await SearchByNameAsync(manager, "gamma");
        Assert.HasCount(1, matches);
        Assert.AreEqual("row-1", matches[0].Id);
    }

    [TestMethod]
    public async Task InitializeAsync_ReplaysFinalDeletePublishedDuringSnapshotRebuild()
    {
        var repository = new BlockingEnvelopeRepository(CreateRecord("row-1", "alpha"));
        await using var manager = new SearchIndexManager<InitializationRecord>(
            repository,
            StoragePolicy.Default);

        var initialization = manager.InitializeAsync().AsTask();
        await repository.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await repository.PublishStoredAsync(CreateRecord("row-1", "beta"));
            await repository.PublishDeletedAsync("row-1");
        }
        finally
        {
            repository.ReleaseInitialization();
        }

        await initialization;

        Assert.IsEmpty(await SearchByNameAsync(manager, "alpha"));
        Assert.IsEmpty(await SearchByNameAsync(manager, "beta"));
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForInitializationThenDisposesSubscriptionsAndRejectsInitialization()
    {
        var repository = new BlockingEnvelopeRepository(CreateRecord("row-1", "alpha"));
        var manager = new SearchIndexManager<InitializationRecord>(repository, StoragePolicy.Default);

        var initialization = manager.InitializeAsync().AsTask();
        await repository.InitializationStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = manager.DisposeAsync().AsTask();
        var concurrentDisposal = manager.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposal.IsCompleted, "Disposal must not tear down indexes while they are being rebuilt.");
            Assert.AreSame(disposal, concurrentDisposal);
        }
        finally
        {
            repository.ReleaseInitialization();
        }

        await initialization;
        await Task.WhenAll(disposal, concurrentDisposal);

        Assert.AreEqual(1, repository.InitializationEnumerationCount);
        Assert.AreEqual(2, repository.DisposedSubscriptionCount);
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => manager.InitializeAsync().AsTask());
    }

    private static InitializationRecord CreateRecord(string id, string name)
    {
        var record = new InitializationRecord { Id = id, Name = name };
        var envelope = new FileObjectEnvelope<InitializationRecord>
        {
            EnvelopeCacheKey = id,
            VersionTag = "1.0.0",
            ObjectInstance = record,
            ImplementationTypeName = typeof(InitializationRecord).AssemblyQualifiedName,
            State = StorageObjectState.Active
        };
        record.Attach(envelope);
        return record;
    }

    private static async Task<List<InitializationRecord>> SearchByNameAsync(
        SearchIndexManager<InitializationRecord> manager,
        string name)
    {
        var matches = new List<InitializationRecord>();
        await foreach (var match in manager.SearchAsync(record => record.Name == name))
        {
            matches.Add(match);
        }

        return matches;
    }

    [SearchIndex]
    private class InitializationRecord : IStoredContract<InitializationRecord>
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Id { get; init; }

        [SearchIndex(SearchIndexMode.Exact)]
        public required string Name { get; init; }

        public IObjectEnvelope<InitializationRecord>? Envelope { get; private set; }

        public void Attach(IObjectEnvelope<InitializationRecord> envelope) => Envelope = envelope;
    }

    private sealed class BlockingEnvelopeRepository :
        IEnvelopeRepository<InitializationRecord>,
        IEnvelopeRepositoryInitializationStream<InitializationRecord>
    {
        private readonly ConcurrentDictionary<string, IObjectEnvelope<InitializationRecord>> _envelopes =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _initializationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseInitialization = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initializationEnumerationCount;
        private int _initializedEnvelopeCount;
        private int _subscriptionCount;
        private int _disposedSubscriptionCount;
        private Func<IObjectEnvelope<InitializationRecord>, ValueTask>? _storedHandler;
        private Func<IObjectEnvelope<InitializationRecord>, ValueTask>? _deletedHandler;

        public BlockingEnvelopeRepository(InitializationRecord record)
        {
            _envelopes[record.Id] = record.Envelope!;
        }

        public Task InitializationStarted => _initializationStarted.Task;

        public int InitializationEnumerationCount => Volatile.Read(ref _initializationEnumerationCount);

        public int InitializedEnvelopeCount => Volatile.Read(ref _initializedEnvelopeCount);

        public bool InitializationTokenCanBeCanceled { get; private set; }

        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public int DisposedSubscriptionCount => Volatile.Read(ref _disposedSubscriptionCount);

        public IReaderAndWriter<InitializationRecord> ReaderAndWriter =>
            throw new NotSupportedException();

        public void ReleaseInitialization() => _releaseInitialization.TrySetResult(true);

        public async ValueTask PublishStoredAsync(InitializationRecord record)
        {
            _envelopes[record.Id] = record.Envelope!;
            var handler = _storedHandler;
            if (handler is not null)
            {
                await handler(record.Envelope!);
            }
        }

        public async ValueTask PublishDeletedAsync(string physicalKey)
        {
            if (!_envelopes.TryRemove(physicalKey, out var envelope))
            {
                throw new AssertFailedException($"No envelope exists for '{physicalKey}'.");
            }

            var handler = _deletedHandler;
            if (handler is not null)
            {
                await handler(envelope);
            }
        }

        public bool Exists(string storageKey) =>
            _envelopes.ContainsKey(storageKey);

        public bool TryGet(
            string storageKey,
            out IReadOnlyList<IObjectEnvelope<InitializationRecord>> envelopes)
        {
            envelopes = _envelopes.TryGetValue(storageKey, out var envelope)
                ? [envelope]
                : [];
            return envelopes.Count != 0;
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<IObjectEnvelope<InitializationRecord>> InitializeAndEnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _initializationEnumerationCount);
            InitializationTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            var snapshot = _envelopes.Values.ToArray();
            _initializationStarted.TrySetResult(true);
            await _releaseInitialization.Task.WaitAsync(cancellationToken);
            foreach (var envelope in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _initializedEnvelopeCount);
                yield return envelope;
            }
        }

        public async IAsyncEnumerable<IObjectEnvelope<InitializationRecord>> GetAsync(
            string storageKey,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_envelopes.TryGetValue(storageKey, out var envelope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }

            await Task.CompletedTask;
        }

        public ValueTask<IObjectEnvelope<InitializationRecord>?> GetPhysicalAsync(
            string physicalStorageKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _envelopes.TryGetValue(physicalStorageKey, out var envelope);
            return ValueTask.FromResult(envelope);
        }

        public async IAsyncEnumerable<IObjectEnvelope<InitializationRecord>> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var envelope in _envelopes.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }

            await Task.CompletedTask;
        }

        async IAsyncEnumerable<IObjectEnvelope<InitializationRecord>>
            IEnvelopeRepository<InitializationRecord>.EnumerateByImplementationTypeAsync<TImplementation>(
                [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var envelope in _envelopes.Values.Where(envelope =>
                         envelope.ObjectInstance is TImplementation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }

            await Task.CompletedTask;
        }

        public ValueTask<InitializationRecord> WriteAsync(
            InitializationRecord contractInstance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            InitializationRecord contractInstance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IContractSubscription SubscribeToAdded(
            Func<IObjectEnvelope<InitializationRecord>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive = null) => CreateSubscription();

        public IContractSubscription SubscribeToRemoved(
            Func<IObjectEnvelope<InitializationRecord>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive = null) => CreateSubscription();

        public IContractSubscription SubscribeToStored(
            Func<IObjectEnvelope<InitializationRecord>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive = null)
        {
            _storedHandler += handler;
            return CreateSubscription(() => _storedHandler -= handler);
        }

        public IContractSubscription SubscribeToDeleted(
            Func<IObjectEnvelope<InitializationRecord>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive = null)
        {
            _deletedHandler += handler;
            return CreateSubscription(() => _deletedHandler -= handler);
        }

        public ValueTask ContractAdded(
            IObjectEnvelope<InitializationRecord> @event,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ContractRemoved(
            IObjectEnvelope<InitializationRecord> @event,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ContractStored(
            IObjectEnvelope<InitializationRecord> @event,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ContractDeleted(
            IObjectEnvelope<InitializationRecord> @event,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        private IContractSubscription CreateSubscription(Action? onDispose = null)
        {
            Interlocked.Increment(ref _subscriptionCount);
            return new TestSubscription(() =>
            {
                onDispose?.Invoke();
                Interlocked.Increment(ref _disposedSubscriptionCount);
            });
        }
    }

    private sealed class TestSubscription(Action onDispose) : IContractSubscription
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
