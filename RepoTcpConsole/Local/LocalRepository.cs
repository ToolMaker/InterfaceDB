using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Options;
using Idb.Libraries.Implementation.Services.Caching;
using Idb.Libraries.Implementation.Services.Repository;
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

        services.AddScoped(sp =>
        {
            var writer = sp.GetRequiredService<IObjectWriter>();
            var reader = sp.GetRequiredService<IObjectReader>();
            var migrationManager = sp.GetRequiredService<IMigrationManager>();
            var repository = new FileSystemRepository(writer, reader, migrationManager);
            return new CachingRepositoryNode(repository);
        });

        var provider = services.BuildServiceProvider();
        var cacheInstance = provider.GetRequiredService<CachingRepositoryNode>();
        cacheInstance.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new LocalRepository(provider, cacheInstance, root);
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
        => _cache.InitializeAsync(cancellationToken);

    public async Task SaveAsync(Person person, CancellationToken cancellationToken)
    {
        var stored = new StoredPerson(person.Id, person.FirstName, person.LastName, person.Age);
        await _cache.SaveAsync<IStoredPerson>(stored, person.Id, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IObjectReadResult<IStoredPerson>> LoadAsync(string id, CancellationToken cancellationToken)
        => _cache.LoadAsync<IStoredPerson>(p => p.Id == id, cancellationToken);

    public ValueTask<bool> ExistsAsync(Person person, CancellationToken cancellationToken)
        => _cache.ExistsAsync<IStoredPerson>(new StoredPerson(person.Id, person.FirstName, person.LastName, person.Age), cancellationToken);

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
