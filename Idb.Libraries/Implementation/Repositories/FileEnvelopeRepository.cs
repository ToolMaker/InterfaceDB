using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Implementation.Extentions;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// File-system backed envelope repository that manages serialization, persistence, and caching for contract instances.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class FileEnvelopeRepository<TContract> : IEnvelopeRepository<TContract, string>
    where TContract : class, IRepoDto<TContract, string>
{
    private readonly IEnvelopeKeyAndLocationFactory<TContract, string> _fileNameFactory;
    
    private readonly IDbContractConfigDto<TContract, string> _contractConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dataRoot;
    private readonly IMigrationStrategy<TContract, string>? _migrationStrategy;
    private readonly IReaderAndWriter<TContract, string> _readerAndWriter;
    
    private readonly ConcurrentDictionary<string, TContract> _envelopes = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _addedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _removedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _storedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _deletedSubscriptions = new();
    
    public FileEnvelopeRepository(
        IEnvelopeKeyAndLocationFactory<TContract, string> fileNameFactory,
        IDbContractConfigDto<TContract, string> contractConfig,
        IServiceProvider serviceProvider,
        IReaderAndWriter<TContract, string> readerAndWriter)
    {
        _fileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _migrationStrategy = (IMigrationStrategy<TContract, string>?)_serviceProvider.GetService(typeof(IMigrationStrategy<TContract, string>));
        _readerAndWriter = readerAndWriter ?? throw new ArgumentNullException(nameof(readerAndWriter));

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(_dataRoot);
    }
    public IReaderAndWriter<TContract, string> ReaderAndWriter => _readerAndWriter;

    public bool ContainsEnvelope(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        return _envelopes.ContainsKey(cacheKey);
    }

    public bool TryGetEnvelope(string cacheKey, out TContract envelope)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        var found = _envelopes.TryGetValue(cacheKey, out var existing);
        envelope = existing!;
        return found;
    }

    public async ValueTask<TContract> WriteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
    {
        objectInstance = await ReaderAndWriter.Writer.WriteAsync(objectInstance, cancellationToken).ConfigureAwait(false);
        var envelope = objectInstance.Envelope ?? throw new InvalidOperationException("Object instance must have a valid envelope after writing.");

        _envelopes[envelope.EnvelopeCacheKey] = objectInstance;

        await ContractStored(envelope, cancellationToken).ConfigureAwait(false);
        await ContractAdded(envelope, cancellationToken).ConfigureAwait(false);

        return objectInstance;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, string>> QueryAsync(
        Func<TContract, bool> filterFunction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (filterFunction is null)
        {
            throw new ArgumentNullException(nameof(filterFunction));
        }

        //TODO COnvert to Parallel For Each
        var snapshot = _envelopes.Values.ToArray();

        foreach (var objectInstance in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (objectInstance is null)
            {
                continue;
            }

            if (!filterFunction(objectInstance))
            {
                continue;
            }

            var envelope = objectInstance.Envelope;
            if (envelope is not null)
            {
                yield return envelope;
            }

            await Task.Yield();
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_migrationStrategy is not null && IsEagerStrategy(_migrationStrategy.StrategyType))
        {
            var tolerant = _migrationStrategy.StrategyType == MigrationStrategyType.EagerTolerant;
            try
            {
                await foreach (var migrated in _migrationStrategy.OnInitializeAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (migrated is not null && migrated.Envelope is not null)
                    {
                        _envelopes[migrated.Envelope.EnvelopeCacheKey] = migrated;
                    }
                }
            }
            catch when (tolerant)
            {
                // Tolerant mode keeps legacy payloads accessible when eager migration encounters issues.
            }
        }

        //await foreach (var _ in LoadAsync(_dataRoot, cancellationToken).ConfigureAwait(false))
        //{
        //    // LoadAsync handles cache population and notifications; iteration ensures the sequence is fully materialized.
        //}
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, string>> LoadAsync(
        string rootLocationOfContract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootLocationOfContract))
        {
            throw new ArgumentNullException(nameof(rootLocationOfContract));
        }

        await foreach (var objectInstance in EnumerateDirectoryAsync(rootLocationOfContract, cancellationToken).ConfigureAwait(false))
        {
            var resolved = ResolveEnvelopeVersion(objectInstance);
            if (resolved is null)
            {
                continue;
            }

            var envelope = resolved.Envelope;
            if (envelope is null)
            {
                continue;
            }

            _envelopes[envelope.EnvelopeCacheKey] = resolved;
            yield return envelope;
        }
    }

    private async IAsyncEnumerable<TContract> EnumerateDirectoryAsync(
        string directory,
        [EnumeratorCancellation] CancellationToken token)
    {
        DirectoryInfo directoryInfo = new(directory);
        if (!directoryInfo.Exists)
        {
            yield break;
        }

        var expectedExtension = _contractConfig.FileExtension.NormalizeExtension();

        foreach (var file in directoryInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();

            if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var objectInstance = await ReaderAndWriter.Reader.ReadAsync(file).ConfigureAwait(false);
            yield return objectInstance;
        }
    }

    private TContract? ResolveEnvelopeVersion(
        TContract objectInstance)
    {
        if (objectInstance is null)
        {
            throw new ArgumentNullException(nameof(objectInstance));
        }

        if (string.Equals(objectInstance.Envelope.VersionTag, _contractConfig.ActiveVersion, StringComparison.OrdinalIgnoreCase))
        {
            return objectInstance;
        }

        if (_migrationStrategy is null)
        {
            return objectInstance;
        }

        //if (_migrationStrategy.StrategyType == MigrationStrategyType.None)
        //{
        //    return envelope;
        //}

        return _migrationStrategy.StrategyType switch
        {
            MigrationStrategyType.Lazy => _migrationStrategy.Migrate(objectInstance, CancellationToken.None) ?? objectInstance,
            MigrationStrategyType.EagerStrict => null,
            MigrationStrategyType.EagerTolerant => TryMigrateTolerant(objectInstance),
            _ => objectInstance
        };
    }

    private TContract? TryMigrateTolerant(TContract objectInstance)
    {
        if (_migrationStrategy is null)
        {
            return objectInstance;
        }

        try
        {
            return _migrationStrategy.Migrate(objectInstance, CancellationToken.None) ?? objectInstance;
        }
        catch
        {
            return objectInstance;
        }
    }

    private static bool IsEagerStrategy(MigrationStrategyType strategy)
        => strategy is MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant;

    #region PubSub Area
    public IContractSubscription SubscribeToAdded(
        Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_addedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_removedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_storedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_deletedSubscriptions, handler, keepAlive);

    public ValueTask ContractAdded(IObjectEnvelope<TContract, string> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_addedSubscriptions, @event, cancellationToken);

    public ValueTask ContractRemoved(IObjectEnvelope<TContract, string> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_removedSubscriptions, @event, cancellationToken);

    public ValueTask ContractStored(IObjectEnvelope<TContract, string> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_storedSubscriptions, @event, cancellationToken);

    public ValueTask ContractDeleted(IObjectEnvelope<TContract, string> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_deletedSubscriptions, @event, cancellationToken);

    private IContractSubscription RegisterSubscription(
        ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
        Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var entry = new SubscriptionEntry(this, bucket, handler, keepAlive);
        bucket[entry.Id] = entry;
        return entry;
    }

    private async ValueTask PublishAsync(
        ConcurrentDictionary<Guid, SubscriptionEntry> subscriptions,
        IObjectEnvelope<TContract, string> payload,
        CancellationToken cancellationToken)
    {
        var snapshot = subscriptions.ToArray();
        foreach (var kvp in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subscription = kvp.Value;
            if (subscription.IsDisposed)
            {
                subscriptions.TryRemove(subscription.Id, out _);
                continue;
            }

            try
            {
                if (subscription.KeepAlive is not null)
                {
                    var alive = await subscription.KeepAlive().ConfigureAwait(false);
                    if (!alive)
                    {
                        subscription.Dispose();
                        continue;
                    }
                }

                await subscription.Handler(payload).ConfigureAwait(false);
            }
            catch
            {
                subscription.Dispose();
            }
        }
    }

    private void RemoveSubscription(
        ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
        Guid id) => bucket.TryRemove(id, out _);


    private sealed class SubscriptionEntry : IContractSubscription
    {
        private readonly FileEnvelopeRepository<TContract> _owner;
        private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _bucket;
        private readonly Func<IObjectEnvelope<TContract, string>, ValueTask> _handler;
        private readonly Func<ValueTask<bool>>? _keepAlive;
        private int _disposed;

        internal SubscriptionEntry(
            FileEnvelopeRepository<TContract> owner,
            ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
            Func<IObjectEnvelope<TContract, string>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive)
        {
            _owner = owner;
            _bucket = bucket;
            _handler = handler;
            _keepAlive = keepAlive;
            Id = Guid.NewGuid();
        }

        internal Guid Id { get; }
        internal Func<IObjectEnvelope<TContract, string>, ValueTask> Handler => _handler;
        internal Func<ValueTask<bool>>? KeepAlive => _keepAlive;

        public bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.RemoveSubscription(_bucket, Id);
            }
        }
    } 
    #endregion
}

