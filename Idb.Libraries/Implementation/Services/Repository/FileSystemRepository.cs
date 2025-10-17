using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Idb.Libraries.Implementation.Services.Repository;

public sealed class FileSystemRepository : IRepository
{
    private readonly IObjectWriter _writer;
    private readonly IObjectReader _reader;
    private readonly IRepositoryIndexManager? _indexManager;
    private readonly IMigrationManager? _migrationManager;
    private readonly ILogger<FileSystemRepository> _logger;

    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, object>> _entities = new();
    private readonly ConcurrentDictionary<IndexSubscription, ConcurrentDictionary<Action<IndexChange>, byte>> _subscriptions = new();

    private static readonly ConcurrentDictionary<Type, EntityMetadata> MetadataCache = new();

    public FileSystemRepository(
        IObjectWriter writer,
        IObjectReader reader)
        : this(writer, reader, indexManager: null, migrationManager: null, logger: null)
    {
    }

    public FileSystemRepository(
        IObjectWriter writer,
        IObjectReader reader,
        IMigrationManager migrationManager)
        : this(writer, reader, indexManager: null, migrationManager: migrationManager, logger: null)
    {
    }

    public FileSystemRepository(
        IObjectWriter writer,
        IObjectReader reader,
        IRepositoryIndexManager? indexManager = null,
        IMigrationManager? migrationManager = null,
        ILogger<FileSystemRepository>? logger = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _indexManager = indexManager;
        _migrationManager = migrationManager;
        _logger = logger ?? NullLogger<FileSystemRepository>.Instance;
    }

    public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_migrationManager is null)
        {
            return ValueTask.CompletedTask;
        }

        return _migrationManager.InitializeAsync(this, cancellationToken);
    }

    public ValueTask<IObjectWriteResult> SaveAsync<TContract>(TContract entityToSave, CancellationToken cancellationToken = default)
        where TContract : class
        => SaveAsync(entityToSave, id: null, cancellationToken);

    public async ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entityToSave,
        string? id,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (entityToSave is null)
        {
            throw new ArgumentNullException(nameof(entityToSave));
        }

        if (!typeof(TContract).IsInterface)
        {
            throw new InvalidOperationException($"Contract type '{typeof(TContract).FullName}' must be an interface.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var metadata = GetMetadata(typeof(TContract));
        var desiredId = !string.IsNullOrWhiteSpace(id) ? id : metadata.GetId(entityToSave);

        var result = await _writer.WriteAsync(entityToSave, envelope: null, id: desiredId, cancellationToken).ConfigureAwait(false);

        if (metadata.CanWriteId && string.IsNullOrWhiteSpace(metadata.GetId(entityToSave)) && !string.IsNullOrWhiteSpace(result.Id))
        {
            metadata.SetId(entityToSave, result.Id);
        }

        TrackEntity(typeof(TContract), result.Id, entityToSave);

        var indexKeys = _indexManager?.UpdateIndexes(entityToSave) ?? new Dictionary<string, object?>();
        PublishIndexChanges(typeof(TContract), entityToSave, result.Id, indexKeys);

        _logger.LogDebug(
            "Saved entity of contract {Contract} with id {Id}.",
            typeof(TContract).FullName,
            result.Id);

        return result;
    }

    public async ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        if (TryExtractIdFromPredicate(predicate, out var id, out var version))
        {
            return await LoadAsync<TContract>(id, version, cancellationToken).ConfigureAwait(false);
        }

        throw new NotSupportedException("Only simple equality predicates against an Id property are supported.");
    }

    public ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return _reader.ReadAsync<TContract>(id.Trim(), versionTag, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync<TContract>(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (entityToCheck is null)
        {
            throw new ArgumentNullException(nameof(entityToCheck));
        }

        var metadata = GetMetadata(typeof(TContract));
        var id = metadata.GetId(entityToCheck);
        if (string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult(false);
        }

        return ExistsAsync<TContract>(id!, versionTag: null, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _reader.ExistsAsync<TContract>(id.Trim(), versionTag, cancellationToken);
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

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query.IndexName))
        {
            return ValueTask.FromResult(GetTrackedEntities<TContract>());
        }

        if (query.Key is null)
        {
            return ValueTask.FromResult(GetTrackedEntities<TContract>());
        }

        var results = QueryByKey<TContract>(query.IndexName!, query.Key);
        return ValueTask.FromResult(results);
    }

    public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
    {
        _indexManager?.ConfigureIndex(configuration);
    }

    public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
    {
        _indexManager?.ConfigureIndexes(configurations);
    }

    public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
    {
        if (contractType is null)
        {
            throw new ArgumentNullException(nameof(contractType));
        }

        return _indexManager?.GetIndexConfigurations(contractType) ?? Array.Empty<IRepositoryIndexConfiguration>();
    }

    public bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull
    {
        if (_indexManager is null)
        {
            index = null!;
            return false;
        }

        return _indexManager.TryGetSortedListIndex<TContract, TKey>(name, out index);
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

        var handlers = _subscriptions.GetOrAdd(subscription, static _ => new ConcurrentDictionary<Action<IndexChange>, byte>());
        handlers[handler] = 0;
    }

    public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
    {
        if (subscription is null || handler is null)
        {
            return;
        }

        if (_subscriptions.TryGetValue(subscription, out var handlers))
        {
            handlers.TryRemove(handler, out _);
            if (handlers.IsEmpty)
            {
                _subscriptions.TryRemove(subscription, out _);
            }
        }
    }

    private IReadOnlyList<TContract> QueryByKey<TContract>(string indexName, object key)
        where TContract : class
    {
        var keyType = key.GetType();

        var method = typeof(FileSystemRepository)
            .GetMethod(nameof(QueryByKeyInternal), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(TContract), keyType);

        var result = method.Invoke(this, new[] { indexName, key });
        return result as IReadOnlyList<TContract> ?? Array.Empty<TContract>();
    }

    private IReadOnlyList<TContract> QueryByKeyInternal<TContract, TKey>(string indexName, TKey key)
        where TContract : class
        where TKey : notnull
    {
        if (_indexManager is not null &&
            _indexManager.TryGetSortedListIndex<TContract, TKey>(indexName, out var index) &&
            index.TryGetValue(key, out var bucket))
        {
            return bucket.ToList();
        }

        return Array.Empty<TContract>();
    }

    private void PublishIndexChanges(Type contractType, object entity, string id, IReadOnlyDictionary<string, object?> indexKeys)
    {
        if (_subscriptions.IsEmpty || indexKeys.Count == 0)
        {
            return;
        }

        foreach (var subscription in _subscriptions)
        {
            if (subscription.Key.ContractType != contractType)
            {
                continue;
            }

            foreach (var indexEntry in indexKeys)
            {
                if (!string.Equals(subscription.Key.IndexName, indexEntry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (subscription.Key.Key is not null && !Equals(subscription.Key.Key, indexEntry.Value))
                {
                    continue;
                }

                var change = new IndexChange(contractType, indexEntry.Key, IndexChangeKind.Updated, entity, indexEntry.Value, id);

                foreach (var handler in subscription.Value.Keys)
                {
                    try
                    {
                        handler(change);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Index change handler threw an exception.");
                    }
                }
            }
        }
    }

    private IReadOnlyList<TContract> GetTrackedEntities<TContract>()
        where TContract : class
    {
        if (_entities.TryGetValue(typeof(TContract), out var map))
        {
            return map.Values.OfType<TContract>().ToList();
        }

        return Array.Empty<TContract>();
    }

    private void TrackEntity(Type contractType, string id, object entity)
    {
        var map = _entities.GetOrAdd(contractType, static _ => new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase));
        map[id] = entity;
    }

    private static bool TryExtractIdFromPredicate<TContract>(
        Expression<Func<TContract, bool>> predicate,
        out string id,
        out string? version)
        where TContract : class
    {
        id = string.Empty;
        version = null;

        ExtractPredicate(predicate.Body, ref id, ref version);

        return !string.IsNullOrWhiteSpace(id);
    }

    private static void ExtractPredicate(Expression expression, ref string id, ref string? version)
    {
        switch (expression)
        {
            case BinaryExpression binary when binary.NodeType == ExpressionType.AndAlso:
                ExtractPredicate(binary.Left, ref id, ref version);
                ExtractPredicate(binary.Right, ref id, ref version);
                break;

            case BinaryExpression binary when binary.NodeType == ExpressionType.Equal:
                ProcessEquality(binary.Left, binary.Right, ref id, ref version);
                ProcessEquality(binary.Right, binary.Left, ref id, ref version);
                break;
        }
    }

    private static void ProcessEquality(Expression memberExpression, Expression valueExpression, ref string id, ref string? version)
    {
        if (memberExpression is not MemberExpression member || member.Member is not PropertyInfo property)
        {
            return;
        }

        if (property.PropertyType != typeof(string))
        {
            return;
        }

        if (!TryEvaluateString(valueExpression, out var stringValue))
        {
            return;
        }

        if (stringValue is null)
        {
            return;
        }

        if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
        {
            id = stringValue;
        }
        else if (string.Equals(property.Name, "VersionTag", StringComparison.OrdinalIgnoreCase))
        {
            version = stringValue;
        }
    }

    private static bool TryEvaluateString(Expression expression, out string? value)
    {
        value = null;

        try
        {
            var converted = Expression.Convert(expression, typeof(string));
            var lambda = Expression.Lambda<Func<string?>>(converted);
            value = lambda.Compile().Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static EntityMetadata GetMetadata(Type contractType)
        => MetadataCache.GetOrAdd(contractType, EntityMetadata.Create);

    private sealed class EntityMetadata
    {
        private EntityMetadata(Func<object, string?> getter, Action<object, string>? setter)
        {
            GetId = getter;
            SetId = setter ?? ((_, _) => { });
            CanWriteId = setter is not null;
        }

        public Func<object, string?> GetId { get; }
        public Action<object, string> SetId { get; }
        public bool CanWriteId { get; }

        public static EntityMetadata Create(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var property = type.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (property is null || property.PropertyType != typeof(string))
            {
                return new EntityMetadata(_ => null, null);
            }

            var instance = Expression.Parameter(typeof(object), "instance");
            var castInstance = Expression.Convert(instance, type);
            var propertyAccess = Expression.Property(castInstance, property);
            var convertResult = Expression.Convert(propertyAccess, typeof(string));
            var getter = Expression.Lambda<Func<object, string?>>(convertResult, instance).Compile();

            Action<object, string>? setter = null;
            if (property.CanWrite)
            {
                var valueParameter = Expression.Parameter(typeof(string), "value");
                var assign = Expression.Assign(propertyAccess, valueParameter);
                setter = Expression.Lambda<Action<object, string>>(assign, instance, valueParameter).Compile();
            }

            return new EntityMetadata(getter, setter);
        }
    }
}
