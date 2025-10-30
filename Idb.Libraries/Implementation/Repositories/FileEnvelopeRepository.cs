using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
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
    private readonly IDbContractConfigDto<TContract, string> _contractConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dataRoot;
    private readonly IMigrationStrategy<TContract, string>? _migrationStrategy;
    private readonly IReaderAndWriter<TContract, string> _readerAndWriter;
    
    private readonly IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract,string>> _envelopes;
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _addedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _removedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _storedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _deletedSubscriptions = new();
    
    public FileEnvelopeRepository(
        IDbContractConfigDto<TContract, string> contractConfig,
        IServiceProvider serviceProvider,
        IReaderAndWriter<TContract, string> readerAndWriter,
        IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract,string>> envelopes)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _migrationStrategy = (IMigrationStrategy<TContract, string>?)_serviceProvider.GetService(typeof(IMigrationStrategy<TContract, string>));
        _readerAndWriter = readerAndWriter ?? throw new ArgumentNullException(nameof(readerAndWriter));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(_dataRoot);
        // Pre-create the active version directory to avoid per-write directory checks in the hot path
        var activeVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        var activeVersionRoot = Path.Combine(_dataRoot, activeVersion);
        Directory.CreateDirectory(activeVersionRoot);
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

        var found = _envelopes.TryGetValue(cacheKey, out var existing) && existing is not null && existing.ObjectInstance is not null;
        envelope = found ? existing!.ObjectInstance! : null!;
        return found;
    }

    public async ValueTask<TContract> WriteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
    {
        objectInstance = await ReaderAndWriter.Writer.WriteAsync(objectInstance, cancellationToken).ConfigureAwait(false);
        var envelope = objectInstance.Envelope ?? throw new InvalidOperationException("Object instance must have a valid envelope after writing.");

        var notify = _contractConfig.StoragePolicy?.RaiseNotifications ?? StoragePolicy.Default.RaiseNotifications;
        if (notify)
        {
            await ContractStored(envelope, cancellationToken).ConfigureAwait(false);
            await ContractAdded(envelope, cancellationToken).ConfigureAwait(false);
        }

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

        //TODO Convert to Parallel For Each
        var snapshot = _envelopes.Values.ToArray();

        foreach (var envelope in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (envelope is null)
            {
                continue;
            }

            var instance = envelope.ObjectInstance;
            if (instance is null)
            {
                // Hydrate on demand for predicate evaluation
                if (!string.IsNullOrWhiteSpace(envelope.Location))
                {
                    var file = new FileInfo(envelope.Location);
                    var hydrated = await ReaderAndWriter.Reader.ReadAsync(file, cancellationToken).ConfigureAwait(false);
                    instance = hydrated;
                    envelope.ObjectInstance = hydrated;
                }
            }

            if (!filterFunction(instance))
            {
                continue;
            }

            yield return envelope;

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
                        _envelopes[migrated.Envelope.EnvelopeCacheKey] = migrated.Envelope;
                    }
                }
            }
            catch when (tolerant)
            {
                // Tolerant mode keeps legacy payloads accessible when eager migration encounters issues.
            }
        }

        await foreach (var _ in LoadAsync(_dataRoot, cancellationToken).ConfigureAwait(false))
        {
            // LoadAsync handles cache population and notifications; iteration ensures the sequence is fully materialized.
        }

        // Hydrate all cached envelopes in parallel according to storage policy (loads contracts into memory)
        var effectivePolicy = _contractConfig.StoragePolicy ?? StoragePolicy.Default;
        if (effectivePolicy.HydrateOnInitialize)
        {
            await HydrateCacheAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HydrateCacheAsync(CancellationToken cancellationToken)
    {
        var policy = _contractConfig.StoragePolicy ?? StoragePolicy.Default;
        var dop = policy.NormalizedConcurrency;
        var snapshot = _envelopes.Values.ToArray();

        await Parallel.ForEachAsync(snapshot, new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        }, async (envelope, ct) =>
        {
            try
            {
                if (envelope is null)
                {
                    return;
                }

                if (envelope.ObjectInstance is not null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(envelope.Location))
                {
                    return;
                }

                var file = new FileInfo(envelope.Location);
                if (!file.Exists)
                {
                    return;
                }

                // Reader.ReadAsync will hydrate the cached envelope's ObjectInstance when found in cache
                await ReaderAndWriter.Reader.ReadAsync(file, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort hydration; continue on failures
            }
        }).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, string>> LoadAsync(
        string rootLocationOfContract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootLocationOfContract))
        {
            throw new ArgumentNullException(nameof(rootLocationOfContract));
        }

        await foreach (var envelope in EnumerateSkeletonsAsync(rootLocationOfContract, cancellationToken).ConfigureAwait(false))
        {
            if (envelope is null)
            {
                continue;
            }

            _envelopes[envelope.EnvelopeCacheKey] = envelope;
            yield return envelope;
        }
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract, string>> EnumerateSkeletonsAsync(
        string directory,
        [EnumeratorCancellation] CancellationToken token)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            yield break;
        }

        var expectedExtension = _contractConfig.FileExtension.NormalizeExtension();

        // Helper local function to create skeleton
        IObjectEnvelope<TContract, string> CreateSkeleton(FileInfo file, string? version)
        {
            var env = _serviceProvider.GetService<IObjectEnvelope<TContract, string>>()
                ?? throw new InvalidOperationException("Failed to resolve envelope instance from service provider.");
            env.EnvelopeCacheKey = Path.GetFileNameWithoutExtension(file.Name);
            env.Location = file.FullName;
            env.VersionTag = string.IsNullOrWhiteSpace(version) ? (_contractConfig.ActiveVersion ?? "1.0.0") : version!;
            env.ObjectInstance = null;
            return env;
        }

        // Root files (back-compat)
        foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return CreateSkeleton(file, _contractConfig.ActiveVersion);
            await Task.Yield();
        }

        // Versioned subfolders
        foreach (var subdir in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            var version = subdir.Name;
            foreach (var file in subdir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return CreateSkeleton(file, version);
                await Task.Yield();
            }
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

