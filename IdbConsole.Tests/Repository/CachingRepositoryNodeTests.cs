using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Repository;
using Idb.Libraries.Abstractions.Serialization;
using Idb.Libraries.Implementation.Repository;
using Xunit;

namespace IdbConsole.Tests.Repository;

public class CachingRepositoryNodeTests
{
    [Fact]
    public async Task SaveAsync_CachesEntityAfterInnerSave()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);

        cache.ConfigureIndex(new SortedListIndexConfiguration<ITestContract, int>("Length", c => c.Id.Length));

        var entity = new TestContract("abc");
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

        cache.ConfigureIndex(new SortedListIndexConfiguration<ITestContract, int>("Length", c => c.Id.Length));

        var entity = new TestContract("alpha");
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

        cache.ConfigureIndex(new SortedListIndexConfiguration<ITestContract, int>("Length", c => c.Id.Length));

        IndexChange? observed = null;
        cache.Subscribe(new IndexSubscription(typeof(ITestContract), "Length"), change => observed = change);

        Assert.True(inner.SubscribeCalled);

        var entity = new TestContract("item-1");
        await inner.SaveAsync<ITestContract>(entity, entity.Id);

        Assert.NotNull(observed);
        Assert.Equal(entity.Id, ((ITestContract)observed!.Entity!).Id);

        var cached = await cache.LoadAsync<ITestContract>(entity.Id);

        Assert.Equal(entity.Id, cached.Value.Id);
        Assert.Equal(0, inner.ReadCount);
    }

    private interface ITestContract
    {
        string Id { get; }
    }

    private sealed record TestContract(string Id) : ITestContract;

    private sealed class RecordingRepositoryNode : IRepositoryNode
    {
        private readonly ConcurrentDictionary<(Type Contract, string Id), object> _entities = new();
        private readonly StubWriter _writer;
        private readonly StubReader _reader;
        private readonly FileSystemRepository _inner;
        private int _queryCallCount;

        public RecordingRepositoryNode()
        {
            _writer = new StubWriter(_entities);
            _reader = new StubReader(_entities);
            _inner = new FileSystemRepository(_writer, _reader);
        }

        public int ReadCount => _reader.ReadCount;
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
        private int _readCount;
        private int _existsCount;

        public StubReader(ConcurrentDictionary<(Type Contract, string Id), object> entities)
        {
            _entities = entities;
        }

        public int ReadCount => _readCount;
        public int ExistsCount => _existsCount;

        public ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _existsCount);
            return ValueTask.FromResult(_entities.ContainsKey((typeof(T), id)));
        }

        public ValueTask<IObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);

            if (_entities.TryGetValue((typeof(T), id), out var value) && value is T typed)
            {
                return ValueTask.FromResult<IObjectReadResult<T>>(new FakeReadResult<T>(id, "v1", typed));
            }

            throw new FileNotFoundException($"Entity '{id}' of type '{typeof(T).Name}' was not found.");
        }
    }

    private sealed class FakeWriteResult : IObjectWriteResult
    {
        public FakeWriteResult(string id, string versionTag, Type objectType)
        {
            Id = id;
            VersionTag = versionTag;
            ObjectType = objectType;
            File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin"));
            Envelope = new FakeEnvelope();
        }

        public string Id { get; }
        public string VersionTag { get; }
        public Type ObjectType { get; }
        public FileInfo File { get; }
        public IObjectEnvelope Envelope { get; }
    }

    private sealed class FakeReadResult<T> : IObjectReadResult<T>
    {
        public FakeReadResult(string id, string versionTag, T value)
        {
            Id = id;
            VersionTag = versionTag;
            Value = value;
            File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin"));
            Envelope = new FakeEnvelope();
        }

        public string Id { get; }
        public string VersionTag { get; }
        public FileInfo File { get; }
        public IObjectEnvelope Envelope { get; }
        public T Value { get; }
    }

    private sealed class FakeEnvelope : IObjectEnvelope
    {
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string VersionTag { get; set; } = "v1";
        public string? TypeName { get; set; } = typeof(TestContract).AssemblyQualifiedName;
        public ObjectFingerprint Fingerprint { get; set; } = new(1, 1);
    }
}
