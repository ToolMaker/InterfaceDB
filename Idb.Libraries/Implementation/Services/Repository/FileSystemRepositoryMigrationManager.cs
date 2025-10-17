using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository.FileRepository;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Services.Migrations;
using Microsoft.Extensions.Logging;

namespace Idb.Libraries.Implementation.Services.Repository;

internal sealed class FileSystemRepositoryMigrationManager : IMigrationManager
{
    private readonly ILocalScopeService _scopeService;
    private readonly IObjectSerializer _serializer;
    private readonly ILogger<FileSystemRepositoryMigrationManager> _logger;
    private readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, IStorageMigration>> _migrations;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    private bool _initialized;

    public FileSystemRepositoryMigrationManager(
        ILocalScopeService scopeService,
        IObjectSerializer serializer,
        IEnumerable<IStorageMigration> migrations,
        ILogger<FileSystemRepositoryMigrationManager> logger)
    {
        _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _migrations = (migrations ?? Enumerable.Empty<IStorageMigration>())
            .GroupBy(m => m.ContractType)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, IStorageMigration>)g.ToDictionary(m => m.SourceVersionTag, m => m, StringComparer.Ordinal),
                EqualityComparer<Type>.Default);
    }

    public MigrationStrategyType MigrationStrategyType { get; set; } = MigrationStrategyType.EagerStrict;

    public async ValueTask InitializeAsync(IRepository repository, CancellationToken cancellationToken = default)
    {
        if (repository is null)
        {
            throw new ArgumentNullException(nameof(repository));
        }

        if (_initialized || _migrations.Count == 0)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized || _migrations.Count == 0)
            {
                return;
            }

            await RunMigrationsAsync(repository, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task RunMigrationsAsync(IRepository repository, CancellationToken cancellationToken)
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

                var context = new StorageMigrationContext(scope, legacy.Name, targetVersion, _serializer, repository);

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

