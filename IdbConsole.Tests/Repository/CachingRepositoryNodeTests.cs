using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IdbConsole.Tests.Repository.Shared;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Repository;
using Xunit;

namespace IdbConsole.Tests.Repository;

public class CachingRepositoryNodeTests : FileSystemRepositoryTestBase
{
    [Fact]
    public async Task SaveAsync_DelegatesToInner()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);

        var entity = CreateContract("alpha");
        var result = await cache.SaveAsync<ITestContract>(entity, cancellationToken: CancellationToken.None);

        Assert.True(inner.SaveCalled);
        Assert.False(string.IsNullOrWhiteSpace(result.Id));
    }

    [Fact]
    public async Task ConfigureIndex_PassesThrough()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);
        var index = CreateLengthIndex();

        cache.ConfigureIndex(index);

        Assert.True(inner.IndexConfigured);
    }

    [Fact]
    public async Task QueryAsync_PassesThrough()
    {
        var inner = new RecordingRepositoryNode();
        var cache = new CachingRepositoryNode(inner);
        var entity = CreateContract("beta");
        await inner.SaveAsync<ITestContract>(entity, cancellationToken: CancellationToken.None);

        var query = new IndexQuery(typeof(ITestContract), "Length", entity.Id.Length);
        var results = await cache.QueryAsync<ITestContract>(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(entity.Id, results[0].Id);
    }

    private sealed class RecordingRepositoryNode : IRepositoryNode
    {
        private readonly FileSystemRepository _inner;
        private readonly StubObjectWriter _writer;
        private readonly StubObjectReader _reader;

        public RecordingRepositoryNode()
        {
            _writer = new StubObjectWriter(DefaultWriteResult);
            _reader = new StubObjectReader().AddResult(DefaultReadResult, exists: true);
            _inner = new FileSystemRepository(_writer, _reader);
            _inner.ConfigureIndex(CreateLengthIndex());
        }

        public bool SaveCalled { get; private set; }
        public bool IndexConfigured { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
            => _inner.InitializeAsync(cancellationToken);

        public ValueTask<IObjectWriteResult> SaveAsync<TContract>(TContract entity, string? id = null, CancellationToken cancellationToken = default)
            where TContract : class
        {
            SaveCalled = true;
            return _inner.SaveAsync(entity, id, cancellationToken);
        }

        public ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(IndexQuery query, CancellationToken cancellationToken)
            where TContract : class
            => _inner.QueryAsync<TContract>(query, cancellationToken);

        public ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(string id, string? versionTag = null, CancellationToken cancellationToken = default)
            where TContract : class
            => _inner.LoadAsync<TContract>(id, versionTag, cancellationToken);

        public ValueTask<bool> ExistsAsync<TContract>(string id, string? versionTag = null, CancellationToken cancellationToken = default)
            where TContract : class
            => _inner.ExistsAsync<TContract>(id, versionTag, cancellationToken);

        public void Subscribe(IndexSubscription subscription, Action<IndexChange> handler)
            => _inner.Subscribe(subscription, handler);

        public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
            => _inner.Unsubscribe(subscription, handler);

        public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
        {
            IndexConfigured = true;
            _inner.ConfigureIndex(configuration);
        }

        public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
            => _inner.ConfigureIndexes(configurations);

        public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
            => _inner.GetIndexConfigurations(contractType);

        public bool TryGetSortedListIndex<TContract, TKey>(string name, out SortedList<TKey, List<TContract>> index)
            where TContract : class
            where TKey : notnull
            => _inner.TryGetSortedListIndex<TContract, TKey>(name, out index);
    }
}

