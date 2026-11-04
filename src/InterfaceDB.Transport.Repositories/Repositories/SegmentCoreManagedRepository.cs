using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Transport.Repositories;

/// <summary>Compatibility entry point. New integrations use LocalRemoteRepositoryFactory and provider interfaces.</summary>
public static class SegmentCoreManagedRepository
{
    public static LocalRemoteRepository<T> Create<T>(IEnumerable<TcpRepositoryOptions<T>> deployedVersions,
        string accountDirectory, string accountScope, IResilientEntityMapper<T> mapper,
        RepositoryReadPreference preference, IEnumerable<IMigration>? migrations = null,
        Func<CancellationToken, ValueTask>? validateAccessAsync = null, TimeSpan? retryDelay = null)
        where T : class, IStoredContract<T>
    {
        var options = deployedVersions.ToArray();
        if (options.Length == 0) throw new ArgumentException("Supply the deployed contract versions.", nameof(deployedVersions));
        foreach (var option in options)
        {
            option.Validate();
            if (option.Connection.Host != options[0].Connection.Host || option.Connection.Port != options[0].Connection.Port
                || option.Connection.TargetHost != options[0].Connection.TargetHost || option.Connection.StoreAlias != options[0].Connection.StoreAlias)
                throw new ArgumentException("All versions must use the same server authority and store alias.");
        }
        var scopes = new Idb.Libraries.Repositories.FileRepositoryCursorStore(Path.Combine(accountDirectory, "remote-scope-approval.json"));
        options = options.Select(option => option with { ScopeApprovalStore = option.ScopeApprovalStore ?? scopes }).ToArray();
        return LocalRemoteRepositoryFactory.Create(options.Select(ManagedRepositoryModel<T>.FromTcp),
            [new SegmentCoreManagedProvider<T>()], new TcpManagedRemoteRepositoryProvider<T>(model =>
                options.Single(option => option.Connection.Manifest == model.Manifest)),
            new TcpRepositoryVersionAuthority(options[0].Connection), new ManagedRepositorySettings("segment-core", preference),
            accountDirectory, accountScope, mapper, migrations, validateAccessAsync, retryDelay);
    }
}
