using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using IdbConsole.Tests.Repository.Shared;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Indexes;
using Idb.Libraries.Implementation.Services.Repository;
using Xunit;

namespace IdbConsole.Tests.Repository.Cache;

public class CachingRepositoryNodeTests : FileSystemRepositoryTestBase
{
    [Fact]
    public async Task SaveAsync_CachesEntityAfterInnerSave()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);
        cache.ConfigureIndex(CreateLengthIndex());

        var entity = CreateContract("abc");
        await cache.SaveAsync<ITestContract>(entity, entity.Id);

        var result = await cache.LoadAsync<ITestContract>(entity.Id);

        Assert.Equal(entity.Id, result.Id);
        Assert.Equal(entity.Id, result.Value.Id);
        Assert.Equal(0, inner.ReadCount);
    }

    [Fact]
    public async Task QueryAsync_PopulatesCacheFromInnerResults()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);
        cache.ConfigureIndex(CreateLengthIndex());

        var entity = CreateContract("alpha");
        await inner.SaveAsync<ITestContract>(entity, entity.Id);

        var query = new IndexQuery(typeof(ITestContract), "Length", entity.Id.Length);
        var results = await cache.QueryAsync<ITestContract>(query);

        Assert.Single(results);
        Assert.Equal(entity.Id, results[0].Id);
        Assert.Equal(1, inner.QueryCallCount);

        var cached = await cache.LoadAsync<ITestContract>(entity.Id);

        Assert.Equal(entity.Id, cached.Value.Id);
        Assert.Equal(0, inner.ReadCount);
    }

    [Fact]
    public async Task Subscribe_ForwardsChangesAndCachesEntities()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);
        cache.ConfigureIndex(CreateLengthIndex());

        IndexChange? observed = null;
        cache.Subscribe(new IndexSubscription(typeof(ITestContract), "Length"), change => observed = change);

        Assert.True(inner.SubscribeCalled);

        var entity = CreateContract("item-1");
        await inner.SaveAsync<ITestContract>(entity, entity.Id);

        Assert.NotNull(observed);
        Assert.Equal(entity.Id, ((ITestContract)observed!.Entity!).Id);

        var cached = await cache.LoadAsync<ITestContract>(entity.Id);

        Assert.Equal(entity.Id, cached.Value.Id);
        Assert.Equal(0, inner.ReadCount);
    }

    private sealed class RecordingRepositoryNode : IRepositoryNode
    {
        private readonly ConcurrentDictionary<(Type Contract, string Id), object> _entities = new();
        private readonly StubWriter _writer;
        private readonly StubReader _reader;
        private readonly FileSystemRepository _inner;
        private int _queryCallCount;
        private int _readCount;

        public RecordingRepositoryNode()
        {
            _writer = new StubWriter(_entities);
            _reader = new StubReader(_entities, () => _readCount++);
            _inner = new FileSystemRepository(_writer, _reader);
            _inner.ConfigureIndex(CreateLengthIndex());
        }

        public int ReadCount => _readCount;
        public int QueryCallCount => _queryCallCount;
        public bool SubscribeCalled { get; private set; }
        public bool UnsubscribeCalled { get; private set; }

        public ValueTask<IObjectWriteResult> SaveAsync<TContract>(
            TContract entity,
            string? id = null,
            CancellationToken cancellationToken = default)
            where TContract : class
            => _inner.SaveAsync(entity, id, cancellationToken);

        public ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
            where TContract : class
            => _inner.LoadAsync<TContract>(id, versionTag, cancellationToken);

        public ValueTask<bool> ExistsAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
            where TContract : class
            => _inner.ExistsAsync<TContract>(id, versionTag, cancellationToken);

        public async ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
            IndexQuery query,
            CancellationToken cancellationToken = default)
            where TContract : class
        {
            Interlocked.Increment(ref _queryCallCount);
            return await _inner.QueryAsync<TContract>(query, cancellationToken).ConfigureAwait(false);
        }

        public void Subscribe(IndexSubscription subscription, Action<IndexChange> handler)
        {
            SubscribeCalled = true;
            _inner.Subscribe(subscription, handler);
        }

        public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
        {
            UnsubscribeCalled = true;
            _inner.Unsubscribe(subscription, handler);
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
    }

    private sealed class StubWriter : IObjectWriter
    {
        private readonly ConcurrentDictionary<(Type Contract, string Id), object> _entities;

        public StubWriter(ConcurrentDictionary<(Type Contract, string Id), object> entities)
        {
            _entities = entities;
        }

        public ValueTask<IObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            CancellationToken cancellationToken = default)
        {
            var identifier = id ?? Guid.NewGuid().ToString("N");
            _entities[(typeof(T), identifier)] = obj!;
            return ValueTask.FromResult<IObjectWriteResult>(
                new FakeWriteResult(identifier, "v1", typeof(T)));
        }
    }

    private sealed class StubReader : IObjectReader
    {
        private readonly ConcurrentDictionary<(Type Contract, string Id), object> _entities;
        private readonly Action _onRead;

        public StubReader(
            ConcurrentDictionary<(Type Contract, string Id), object> entities,
            Action onRead)
        {
            _entities = entities;
            _onRead = onRead;
        }

        public ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_entities.ContainsKey((typeof(T), id)));

        public ValueTask<IObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            _onRead();

            if (_entities.TryGetValue((typeof(T), id), out var value) && value is T typed)
            {
                return ValueTask.FromResult<IObjectReadResult<T>>(new FakeReadResult<T>(id, "v1", typed));
            }

            throw new FileNotFoundException();
        }
    }
}
