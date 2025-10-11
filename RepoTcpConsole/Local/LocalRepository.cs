using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Repository;
using Idb.Libraries.Abstractions.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Repository;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using RepoTcpHost.Domain;

namespace RepoTcpConsoleApp.Local;

internal sealed class LocalRepository : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CachingRepositoryNode _cache;

    private LocalRepository(ServiceProvider provider, CachingRepositoryNode cache, string rootPath)
    {
        _provider = provider;
        _cache = cache;
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public IReadOnlyCollection<IRepositoryIndexConfiguration> Indexes
        => _cache.GetIndexConfigurations(typeof(IStoredPerson));

    public static LocalRepository Create(string? rootOverride)
    {
        var root = ResolveRoot(rootOverride);
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalScopeStorage(builder =>
        {
            builder.RootDirectory = new DirectoryInfo(root);
            builder.AssembliesToScan = new[] { typeof(IStoredPerson).Assembly };
        });

        services.AddDefaultObjectWriterAndReader();

        services.AddSingleton(sp =>
        {
            var writer = sp.GetRequiredService<IObjectWriter>();
            var reader = sp.GetRequiredService<IObjectReader>();
            var inner = new FileSystemRepository(writer, reader);
            var indexes = new IRepositoryIndexConfiguration[]
            {
                new SortedListIndexConfiguration<IStoredPerson, string>("ByLastName", p => p.LastName),
                new SortedListIndexConfiguration<IStoredPerson, string>("ByFirstName", p => p.FirstName),
                new SortedListIndexConfiguration<IStoredPerson, int>("ByAge", p => p.Age),
            };

            var cache = new CachingRepositoryNode(inner);
            cache.ConfigureIndexes(indexes);
            return cache;
        });

        var provider = services.BuildServiceProvider();
        var cacheInstance = provider.GetRequiredService<CachingRepositoryNode>();
        return new LocalRepository(provider, cacheInstance, root);
    }

    public async Task SaveAsync(Person person, CancellationToken cancellationToken)
    {
        var stored = new StoredPerson(person.Id, person.FirstName, person.LastName, person.Age);
        await _cache.SaveAsync<IStoredPerson>(stored, stored.Id, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IObjectReadResult<IStoredPerson>> LoadAsync(string id, CancellationToken cancellationToken)
        => _cache.LoadAsync<IStoredPerson>(id, cancellationToken: cancellationToken);

    public ValueTask<bool> ExistsAsync(string id, CancellationToken cancellationToken)
        => _cache.ExistsAsync<IStoredPerson>(id, cancellationToken: cancellationToken);

    public ValueTask<IReadOnlyList<IStoredPerson>> QueryAsync(string indexName, object key, CancellationToken cancellationToken)
        => _cache.QueryAsync<IStoredPerson>(new IndexQuery(typeof(IStoredPerson), indexName, key), cancellationToken);

    public bool TryGetSortedListIndex<TKey>(
        string name,
        out SortedList<TKey, List<IStoredPerson>> index)
        where TKey : notnull
        => _cache.TryGetSortedListIndex<IStoredPerson, TKey>(name, out index);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveRoot(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(AppContext.BaseDirectory, "TcpConsoleStorage");
    }

    [StorageScope(VersionTag = "v1")]
    public interface IStoredPerson
    {
        string Id { get; }
        string FirstName { get; }
        string LastName { get; }
        int Age { get; }
    }

    [MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
    public sealed record StoredPerson(
        [property: Key(0)] string Id,
        [property: Key(1)] string FirstName,
        [property: Key(2)] string LastName,
        [property: Key(3)] int Age) : IStoredPerson;
}
