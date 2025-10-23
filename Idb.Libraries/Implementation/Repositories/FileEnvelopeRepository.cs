using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// File-system backed envelope repository that manages serialization, persistence, and caching for contract instances.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class FileEnvelopeRepository<TContract> : IEnvelopeRepository<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IFileNameFactory<TContract, FileInfo> _fileNameFactory;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IDbContractConfigDto<TContract, FileInfo> _contractConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dataRoot;
    private readonly IMigrationStrategy<TContract, FileInfo>? _migrationStrategy;
    private readonly StoragePolicy _storagePolicy;
    private readonly SemaphoreSlim? _writeSemaphore;
    private readonly bool _raiseNotifications;

    private readonly ConcurrentDictionary<string, IObjectEnvelope<TContract, FileInfo>> _envelopes = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _addedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _removedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _storedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _deletedSubscriptions = new();

    public FileEnvelopeRepository(
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IObjectSerializer<TContract> serializer,
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        IServiceProvider serviceProvider)
    {
        _fileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        _migrationStrategy = (IMigrationStrategy<TContract, FileInfo>?)_serviceProvider.GetService(typeof(IMigrationStrategy<TContract, FileInfo>));
        _storagePolicy = contractConfig.StoragePolicy ?? StoragePolicy.Default;
        var normalizedConcurrency = _storagePolicy.NormalizedConcurrency;
        _writeSemaphore = normalizedConcurrency > 1 ? new SemaphoreSlim(normalizedConcurrency, normalizedConcurrency) : null;
        _raiseNotifications = _storagePolicy.RaiseNotifications;

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(_dataRoot);
    }

    public IObjectSerializer<TContract> Serializer => _serializer;

    public bool ContainsEnvelope(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        return _envelopes.ContainsKey(cacheKey);
    }

    public bool TryGetEnvelope(string cacheKey, out IObjectEnvelope<TContract, FileInfo>? envelope)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        var found = _envelopes.TryGetValue(cacheKey, out var existing);
        envelope = existing;
        return found;
    }

    public async ValueTask<IObjectEnvelope<TContract, FileInfo>> WriteAsync(
        TContract obj,
        CancellationToken cancellationToken = default)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        var placeholder = obj.Envelope;
        if (placeholder is null)
        {
            placeholder = (IObjectEnvelope<TContract, FileInfo>?)_serviceProvider.GetService(typeof(IObjectEnvelope<TContract, FileInfo>));
            if (placeholder is not null)
            {
                obj.Envelope = placeholder;
            }
        }

        var existingEnvelope = obj.Envelope as FileBackedObjectEnvelope<TContract>;
        var existingLocation = existingEnvelope?.Location;
        var normalizedId = existingEnvelope?.CacheKey
            ?? placeholder?.CacheKey
            ?? Guid.NewGuid().ToString("N");

        var fileNameSegment = _fileNameFactory.Create(obj, existingLocation, normalizedId);
        if (string.IsNullOrWhiteSpace(fileNameSegment))
        {
            throw new InvalidOperationException("The file-name factory returned an empty identifier.");
        }

        var extension = NormalizeExtension(_serializer.FileExtension);
        var targetPath = Path.Combine(_dataRoot, fileNameSegment + extension);
        var targetFile = new FileInfo(targetPath);

        var semaphore = _writeSemaphore;
        if (semaphore is not null)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            obj.Envelope = null;
            var payload = _serializer.Serialize(obj);

            var envelope = CreateEnvelope(targetFile, obj, payload);
            _envelopes[envelope.CacheKey] = envelope;
            obj.Envelope = envelope;

            if (_raiseNotifications)
            {
                await ContractAdded(obj, cancellationToken).ConfigureAwait(false);
            }

            _ = PersistEnvelopeAsync(targetFile, payload, envelope, semaphore);

            return envelope;
        }
        catch
        {
            semaphore?.Release();
            throw;
        }
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> QueryAsync(
        Func<TContract, bool> filterFunction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (filterFunction is null)
        {
            throw new ArgumentNullException(nameof(filterFunction));
        }

        // Snapshot the current cache to avoid issues with concurrent mutations while streaming.
        var snapshot = _envelopes.Values.ToArray();

        foreach (var envelope in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instance = envelope.ObjectInstance;
            if (instance is null)
            {
                continue;
            }

            if (!filterFunction(instance))
            {
                continue;
            }

            yield return envelope;

            // Ensure we remain asynchronously friendly even though the operation is CPU-only.
            await Task.Yield();
        }
    }

    private FileBackedObjectEnvelope<TContract> CreateEnvelope(
        FileInfo file,
        TContract contract,
        byte[] payload,
        string? versionOverride = null)
    {
        var version = !string.IsNullOrWhiteSpace(versionOverride)
            ? versionOverride!
            : _contractConfig.ActiveVersion ?? "1.0.0";
        return new FileBackedObjectEnvelope<TContract>(
            file,
            version,
            contract,
            payload,
            static (_, info) => info.FullName); //TODO: this comes from the IFileNameFactory that should be defined in the StorageScopeAttribute on the TContract interface
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_migrationStrategy is not null && IsEagerStrategy(_migrationStrategy.StrategyType))
        {
            var tolerant = _migrationStrategy.StrategyType == MigrationStrategyType.EagerTolerant;
            try
            {
                await foreach (var migratedEnvelope in _migrationStrategy.OnInitializeAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (migratedEnvelope is not null)
                    {
                        await ProcessEnvelopeAsync(migratedEnvelope, cancellationToken, notify: false).ConfigureAwait(false);
                    }
                }
            }
            catch when (tolerant)
            {
                // Tolerant mode keeps legacy payloads accessible when eager migration encounters issues.
            }
        }

        var rootLocation = new FileInfo(_dataRoot);
        await foreach (var _ in LoadAsync(rootLocation, cancellationToken).ConfigureAwait(false))
        {
            // LoadAsync handles cache population and notifications; iteration ensures the sequence is fully materialized.
        }
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> LoadAsync(
        FileInfo rootLocationOfContract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (rootLocationOfContract is null)
        {
            throw new ArgumentNullException(nameof(rootLocationOfContract));
        }

        var rootDirectory = new DirectoryInfo(rootLocationOfContract.FullName);
        if (!rootDirectory.Exists)
        {
            yield break;
        }

        var activeVersion = _contractConfig.ActiveVersion ?? "1.0.0";

        await foreach (var envelope in EnumerateDirectoryAsync(rootDirectory, activeVersion, activeVersion, cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }

        foreach (var versionDirectory in rootDirectory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versionTag = versionDirectory.Name;

            await foreach (var envelope in EnumerateDirectoryAsync(versionDirectory, versionTag, activeVersion, cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> EnumerateDirectoryAsync(
        DirectoryInfo directory,
        string versionTag,
        string activeVersion,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (!directory.Exists)
        {
            yield break;
        }

        var expectedExtension = NormalizeExtension(_serializer.FileExtension);

        foreach (var file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();

            if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = await File.ReadAllBytesAsync(file.FullName, token).ConfigureAwait(false);
            var contract = _serializer.Deserialize(payload);
            var envelope = CreateEnvelope(file, contract, payload, versionTag);

            var resolvedEnvelope = ResolveEnvelopeVersion(envelope, versionTag, activeVersion);
            if (resolvedEnvelope is null)
            {
                continue;
            }

            await ProcessEnvelopeAsync(resolvedEnvelope, token).ConfigureAwait(false);
            yield return resolvedEnvelope;
        }
    }

    private IObjectEnvelope<TContract, FileInfo>? ResolveEnvelopeVersion(
        IObjectEnvelope<TContract, FileInfo> envelope,
        string sourceVersion,
        string activeVersion)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        if (string.Equals(sourceVersion, activeVersion, StringComparison.OrdinalIgnoreCase))
        {
            return envelope;
        }

        if (_migrationStrategy is null)
        {
            return envelope;
        }

        if (_migrationStrategy.StrategyType == MigrationStrategyType.None)
        {
            return envelope;
        }

        return _migrationStrategy.StrategyType switch
        {
            MigrationStrategyType.Lazy => _migrationStrategy.Migrate(envelope, CancellationToken.None) ?? envelope,
            MigrationStrategyType.EagerStrict => null,
            MigrationStrategyType.EagerTolerant => TryMigrateTolerant(envelope),
            _ => envelope
        };
    }

    private IObjectEnvelope<TContract, FileInfo>? TryMigrateTolerant(IObjectEnvelope<TContract, FileInfo> envelope)
    {
        if (_migrationStrategy is null)
        {
            return envelope;
        }

        try
        {
            return _migrationStrategy.Migrate(envelope, CancellationToken.None) ?? envelope;
        }
        catch
        {
            return envelope;
        }
    }

    private async ValueTask ProcessEnvelopeAsync(
        IObjectEnvelope<TContract, FileInfo> envelope,
        CancellationToken cancellationToken,
        bool notify = true)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        var contractInstance = envelope.ObjectInstance ?? _serializer.Deserialize(envelope.Payload);
        contractInstance.Envelope = envelope;
        _envelopes[envelope.CacheKey] = envelope;

        if (notify && _raiseNotifications)
        {
            await ContractAdded(contractInstance, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsEagerStrategy(MigrationStrategyType strategy)
        => strategy is MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant;

    private async ValueTask PersistPayloadAsync(FileInfo targetFile, byte[] payload, CancellationToken cancellationToken)
    {
        if (targetFile.Directory is not null && !targetFile.Directory.Exists)
        {
            targetFile.Directory.Create();
        }

        await File.WriteAllBytesAsync(targetFile.FullName, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistEnvelopeAsync(
        FileInfo targetFile,
        byte[] payload,
        FileBackedObjectEnvelope<TContract> envelope,
        SemaphoreSlim? semaphore)
    {
        try
        {
            await PersistPayloadAsync(targetFile, payload, CancellationToken.None).ConfigureAwait(false);

            if (_raiseNotifications)
            {
                var instance = envelope.ObjectInstance ?? _serializer.Deserialize(payload);
                await ContractStored(instance, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // TODO: asynchronous storage pipeline should capture and surface persistence failures.
        }
        finally
        {
            semaphore?.Release();
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }

    #region PubSub Area
    public IContractSubscription SubscribeToAdded(
    Func<TContract, ValueTask> handler,
    Func<ValueTask<bool>>? keepAlive = null)
    => RegisterSubscription(_addedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_removedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_storedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_deletedSubscriptions, handler, keepAlive);

    public ValueTask ContractAdded(TContract @event, CancellationToken cancellationToken = default)
        => PublishAsync(_addedSubscriptions, @event, cancellationToken);

    public ValueTask ContractRemoved(TContract @event, CancellationToken cancellationToken = default)
        => PublishAsync(_removedSubscriptions, @event, cancellationToken);

    public ValueTask ContractStored(TContract @event, CancellationToken cancellationToken = default)
        => PublishAsync(_storedSubscriptions, @event, cancellationToken);

    public ValueTask ContractDeleted(TContract @event, CancellationToken cancellationToken = default)
        => PublishAsync(_deletedSubscriptions, @event, cancellationToken);

    private IContractSubscription RegisterSubscription(
        ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
        Func<TContract, ValueTask> handler,
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
        TContract payload,
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
        private readonly Func<TContract, ValueTask> _handler;
        private readonly Func<ValueTask<bool>>? _keepAlive;
        private int _disposed;

        internal SubscriptionEntry(
            FileEnvelopeRepository<TContract> owner,
            ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
            Func<TContract, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive)
        {
            _owner = owner;
            _bucket = bucket;
            _handler = handler;
            _keepAlive = keepAlive;
            Id = Guid.NewGuid();
        }

        internal Guid Id { get; }
        internal Func<TContract, ValueTask> Handler => _handler;
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

