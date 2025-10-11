using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Services.Indexes;
using System.Collections.Concurrent;

namespace Idb.Libraries.Implementation.Services.Repository;

public sealed class FileSystemRepository : IRepositoryNode
{
    private readonly IObjectWriter _writer;
    private readonly IObjectReader _reader;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, IRepositoryIndexConfiguration>> _indexConfigurations = new();
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, ISortedListIndex>> _sortedListIndexes = new();
    private readonly ConcurrentDictionary<IndexSubscription, ConcurrentDictionary<Action<IndexChange>, byte>> _subscribers = new();

    public FileSystemRepository(IObjectWriter writer, IObjectReader reader)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entity,
        string? id = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        EnsureContractIsInterface<TContract>();

        var result = await _writer.WriteAsync(entity, id, cancellationToken).ConfigureAwait(false);

        var indexKeys = UpdateIndexes(entity);
        PublishIndexChanges(typeof(TContract), result.Id, entity!, indexKeys);

        return result;
    }

    public ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        EnsureContractIsInterface<TContract>();
        return _reader.ReadAsync<TContract>(id, versionTag, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        EnsureContractIsInterface<TContract>();
        return _reader.ExistsAsync<TContract>(id, versionTag, cancellationToken);
    }

    public ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        EnsureContractTypeIsInterface(query.ContractType);

        if (query.ContractType != typeof(TContract))
        {
            throw new InvalidOperationException($"Query contract type '{query.ContractType.FullName}' does not match requested type '{typeof(TContract).FullName}'.");
        }

        if (string.IsNullOrWhiteSpace(query.IndexName))
        {
            throw new ArgumentException("Index name must be provided for query.", nameof(query));
        }

        if (_sortedListIndexes.TryGetValue(query.ContractType, out var registry) &&
            registry.TryGetValue(query.IndexName, out var index))
        {
            var snapshot = index.Snapshot(query.Key);
            var typed = snapshot.Cast<TContract>().ToList();
            return ValueTask.FromResult<IReadOnlyList<TContract>>(typed);
        }

        return ValueTask.FromResult<IReadOnlyList<TContract>>(Array.Empty<TContract>());
    }

    public void Subscribe(IndexSubscription subscription, Action<IndexChange> handler)
    {
        if (subscription is null)
        {
            throw new ArgumentNullException(nameof(subscription));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        EnsureContractTypeIsInterface(subscription.ContractType);

        var handlers = _subscribers.GetOrAdd(subscription, _ => new ConcurrentDictionary<Action<IndexChange>, byte>());
        handlers[handler] = 0;
    }

    public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
    {
        if (subscription is null || handler is null)
        {
            return;
        }

        if (_subscribers.TryGetValue(subscription, out var handlers))
        {
            handlers.TryRemove(handler, out _);
            if (handlers.IsEmpty)
            {
                _subscribers.TryRemove(subscription, out _);
            }
        }
    }

    public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var contractType = configuration.ContractType ?? throw new ArgumentException("Index configuration must specify a contract type.", nameof(configuration));
        EnsureContractTypeIsInterface(contractType);

        var name = configuration.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index configuration must have a name.", nameof(configuration));
        }

        var registry = _indexConfigurations.GetOrAdd(contractType, _ => new ConcurrentDictionary<string, IRepositoryIndexConfiguration>(StringComparer.OrdinalIgnoreCase));
        registry[name] = configuration;

        if (configuration.Kind == RepositoryIndexKind.SortedList)
        {
            if (configuration is not ISortedListIndexFactory factory)
            {
                throw new InvalidOperationException($"Configuration '{configuration.GetType().FullName}' must implement {nameof(ISortedListIndexFactory)} to register a sorted-list index.");
            }

            var sortedRegistry = _sortedListIndexes.GetOrAdd(contractType, _ => new ConcurrentDictionary<string, ISortedListIndex>(StringComparer.OrdinalIgnoreCase));
            sortedRegistry[name] = factory.CreateIndex();
        }
    }

    public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
    {
        if (configurations is null)
        {
            throw new ArgumentNullException(nameof(configurations));
        }

        foreach (var configuration in configurations)
        {
            ConfigureIndex(configuration);
        }
    }

    public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
    {
        if (contractType is null)
        {
            throw new ArgumentNullException(nameof(contractType));
        }

        return _indexConfigurations.TryGetValue(contractType, out var registry)
            ? registry.Values.ToArray()
            : Array.Empty<IRepositoryIndexConfiguration>();
    }

        public bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name cannot be null or whitespace.", nameof(name));
        }

        EnsureContractIsInterface<TContract>();

        if (_sortedListIndexes.TryGetValue(typeof(TContract), out var registry) &&
            registry.TryGetValue(name, out var raw) &&
            raw.TryAs<TContract, TKey>(out index))
        {
            return true;
        }

        index = null!;
        return false;
    }
    private Dictionary<string, object?> UpdateIndexes<TContract>(TContract entity)
        where TContract : class
    {
        var keys = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (_sortedListIndexes.TryGetValue(typeof(TContract), out var registry))
        {
            foreach (var (name, index) in registry)
            {
                var key = index.Add(entity!);
                keys[name] = key;
            }
        }

        return keys;
    }

    private void PublishIndexChanges(Type contractType, string id, object entity, Dictionary<string, object?> indexKeys)
    {
        if (indexKeys.Count == 0 || _subscribers.IsEmpty)
        {
            return;
        }

        foreach (var (indexName, key) in indexKeys)
        {
            var change = new IndexChange(contractType, indexName, IndexChangeKind.Updated, entity, key, id);
            NotifySubscribers(change);
        }
    }

    private void NotifySubscribers(IndexChange change)
    {
        foreach (var kvp in _subscribers.ToArray())
        {
            var subscription = kvp.Key;
            if (subscription.ContractType != change.ContractType)
            {
                continue;
            }

            if (!string.Equals(subscription.IndexName, change.IndexName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (subscription.Key is not null && !Equals(subscription.Key, change.Key))
            {
                continue;
            }

            foreach (var handler in kvp.Value.Keys)
            {
                handler(change);
            }
        }
    }

    private static void EnsureContractIsInterface<TContract>()
    {
        EnsureContractTypeIsInterface(typeof(TContract));
    }

    private static void EnsureContractTypeIsInterface(Type contractType)
    {
        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException($"Storage contract '{contractType.FullName}' must be an interface marked with [StorageScope].");
        }
    }
}

