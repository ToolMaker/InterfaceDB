using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Repository;
using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Implementation.Repository;

public sealed class CachingRepositoryNode : IRepositoryNode
{
    private readonly IRepositoryNode _inner;
    private readonly ConcurrentDictionary<(Type Contract, string Id), object> _entities = new();
    private readonly ConcurrentDictionary<IndexSubscription, ConcurrentDictionary<Action<IndexChange>, byte>> _handlers = new();
    private readonly ConcurrentDictionary<(IndexSubscription Subscription, Action<IndexChange> Handler), Action<IndexChange>> _wrappedHandlers = new();

    public CachingRepositoryNode(IRepositoryNode inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entity,
        string? id = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        var result = await _inner.SaveAsync(entity, id, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Id))
        {
            _entities[(typeof(TContract), result.Id)] = entity!;
        }

        return result;
    }

    public async ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (_entities.TryGetValue((typeof(TContract), id), out var cached) && cached is TContract typed)
        {
            return new CachedReadResult<TContract>(id, typed);
        }

        var result = await _inner.LoadAsync<TContract>(id, versionTag, cancellationToken).ConfigureAwait(false);
        if (result.Value is not null)
        {
            _entities[(typeof(TContract), result.Id)] = result.Value;
        }

        return result;
    }

    public async ValueTask<bool> ExistsAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        if (_entities.ContainsKey((typeof(TContract), id)))
        {
            return true;
        }

        return await _inner.ExistsAsync<TContract>(id, versionTag, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class
    {
        var results = await _inner.QueryAsync<TContract>(query, cancellationToken).ConfigureAwait(false);
        foreach (var item in results)
        {
            var identifier = ExtractId(item);
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                _entities[(typeof(TContract), identifier!)] = item!;
            }
        }

        return results;
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

        var wrapped = new Action<IndexChange>(change =>
        {
            if (!string.IsNullOrWhiteSpace(change.Id) && change.Entity is not null)
            {
                _entities[(change.ContractType, change.Id)] = change.Entity;
            }

            handler(change);
        });

        _wrappedHandlers[(subscription, handler)] = wrapped;
        _handlers.GetOrAdd(subscription, _ => new ConcurrentDictionary<Action<IndexChange>, byte>())[handler] = 0;
        _inner.Subscribe(subscription, wrapped);
    }

    public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
    {
        if (subscription is null || handler is null)
        {
            return;
        }

        if (_wrappedHandlers.TryRemove((subscription, handler), out var wrapped))
        {
            _inner.Unsubscribe(subscription, wrapped);
        }

        if (_handlers.TryGetValue(subscription, out var handlers))
        {
            handlers.TryRemove(handler, out _);
            if (handlers.IsEmpty)
            {
                _handlers.TryRemove(subscription, out _);
            }
        }
    }

    public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
        => _inner.ConfigureIndex(configuration);

    public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
        => _inner.ConfigureIndexes(configurations);

    public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
        => _inner.GetIndexConfigurations(contractType);

    public bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull
        => _inner.TryGetSortedListIndex<TContract, TKey>(name, out index);

    private static string? ExtractId(object? entity)
    {
        if (entity is null)
        {
            return null;
        }

        var prop = entity.GetType().GetProperty("Id");
        return prop?.GetValue(entity) as string;
    }

    private sealed class CachedReadResult<T> : IObjectReadResult<T>
    {
        public CachedReadResult(string id, T value)
        {
            Id = id;
            Value = value;
        }

        public string Id { get; }
        public string VersionTag => string.Empty;
        public FileInfo File => new(Path.Combine(Path.GetTempPath(), Id + ".cache"));
        public IObjectEnvelope Envelope => new CachedEnvelope();
        public T Value { get; }
    }

    private sealed class CachedEnvelope : IObjectEnvelope
    {
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string VersionTag { get; set; } = string.Empty;
        public string? TypeName { get; set; } = typeof(CachedEnvelope).AssemblyQualifiedName;
        public ObjectFingerprint Fingerprint { get; set; } = new ObjectFingerprint(0, 0);
    }
}
