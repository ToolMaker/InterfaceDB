using Microsoft.Extensions.Logging;
using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Migrations;
using TransactionManager.Abstraction.Services;

namespace TransactionManager.Implementation.Migrations
{
    public sealed class StorageContractManifestService : IStorageContractManifestService
    {
        private readonly StorageContractManifestBuilder _builder;
        private readonly StorageContractManifestStore _store;
        private readonly ILocalScopeService _scopeService;
        private readonly ILogger<StorageContractManifestService> _logger;

        public StorageContractManifestService(
            StorageContractManifestBuilder builder,
            StorageContractManifestStore store,
            ILocalScopeService scopeService,
            ILogger<StorageContractManifestService> logger)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<IStorageContractManifest> SnapshotAsync<TContract>(CancellationToken cancellationToken = default)
            => SnapshotAsync(typeof(TContract), cancellationToken);

        public async Task<IStorageContractManifest> SnapshotAsync(Type contractType, CancellationToken cancellationToken = default)
        {
            var scope = ResolveScope(contractType);
            var manifest = (StorageContractManifest)_builder.Build(scope.ContractType, scope.VersionTag);

            await _store.SaveAsync(scope, manifest, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Snapshot persisted for {Contract} version {Version} (hash {Hash}).",
                scope.ContractType.FullName,
                scope.VersionTag,
                manifest.Hash);

            return manifest;
        }

        public Task<IStorageContractDiff> CompareWithLatestAsync<TContract>(CancellationToken cancellationToken = default)
            => CompareWithLatestAsync(typeof(TContract), cancellationToken);

        public Task<IStorageContractDiff> CompareWithLatestAsync(Type contractType, CancellationToken cancellationToken = default)
        {
            var scope = ResolveScope(contractType);
            return CompareInternalAsync(scope, cancellationToken);
        }

        private IScopeDescriptor ResolveScope(Type contractType)
        {
            if (contractType is null)
            {
                throw new ArgumentNullException(nameof(contractType));
            }

            var scope = _scopeService.GetScope(contractType)
                        ?? throw new InvalidOperationException($"No storage scope registered for interface '{contractType.FullName}'.");

            return scope;
        }

        private Task<IStorageContractDiff> CompareInternalAsync(IScopeDescriptor scope, CancellationToken cancellationToken)
        {
            var latest = _store.LoadLatest(scope);
            var current = _builder.Build(scope.ContractType, scope.VersionTag);

            if (latest is null)
            {
                return Task.FromResult<IStorageContractDiff>(new StorageContractDiff(current, null, hasDifferences: false));
            }

            if (string.Equals(latest.Hash, current.Hash, StringComparison.Ordinal))
            {
                return Task.FromResult<IStorageContractDiff>(new StorageContractDiff(current, latest, hasDifferences: false));
            }

            return Task.FromResult<IStorageContractDiff>(new StorageContractDiff(current, latest, hasDifferences: true));
        }
    }
}





