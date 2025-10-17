using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Repository.FileRepository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Services.Migrations;

public sealed class StorageMigrationRunner : IStorageMigrationRunner
{
    private readonly ILocalScopeService _scopeService;
    private readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, IStorageMigration>> _migrations;
    private readonly IObjectSerializer _serializer;
    private readonly IRepository _repository;
    private readonly ILogger<StorageMigrationRunner> _logger;

    public StorageMigrationRunner(
        ILocalScopeService scopeService,
        IEnumerable<IStorageMigration> migrations,
        IObjectSerializer serializer,
        IRepository repository,
        ILogger<StorageMigrationRunner>? logger = null)
    {
        _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageMigrationRunner>.Instance;

        _migrations = (migrations ?? Enumerable.Empty<IStorageMigration>())
            .GroupBy(m => m.ContractType)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, IStorageMigration>)group.ToDictionary(m => m.SourceVersionTag, m => m, StringComparer.Ordinal),
                EqualityComparer<Type>.Default);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var scope in _scopeService.GetScopes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_migrations.TryGetValue(scope.ContractType, out var contractMigrations) || contractMigrations.Count == 0)
            {
                continue;
            }

            var targetVersion = scope.VersionTag;
            var legacyDirectories = scope.Directory.Exists
                ? scope.Directory.EnumerateDirectories()
                      .Where(d => !string.Equals(d.Name, targetVersion, StringComparison.Ordinal))
                      .OrderBy(d => d.Name, StringComparer.Ordinal)
                      .ToArray()
                : Array.Empty<DirectoryInfo>();

            foreach (var legacy in legacyDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!contractMigrations.TryGetValue(legacy.Name, out var migration))
                {
                    _logger.LogWarning(
                        "No migration registered for contract {Contract} from version {Source} to {Target}. Existing data will stay untouched.",
                        scope.ContractType.FullName,
                        legacy.Name,
                        targetVersion);
                    continue;
                }

                if (!string.Equals(migration.TargetVersionTag, targetVersion, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Migration {Migration} targets version {DeclaredTarget}, but current scope version for {Contract} is {Target}. Skipping.",
                        migration.GetType().FullName,
                        migration.TargetVersionTag,
                        scope.ContractType.FullName,
                        targetVersion);
                    continue;
                }

                var context = new StorageMigrationContext(scope, legacy.Name, targetVersion, _serializer, _repository);

                _logger.LogInformation(
                    "Running migration {Migration} for contract {Contract} from {Source} to {Target}.",
                    migration.GetType().Name,
                    scope.ContractType.FullName,
                    legacy.Name,
                    targetVersion);

                await migration.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
