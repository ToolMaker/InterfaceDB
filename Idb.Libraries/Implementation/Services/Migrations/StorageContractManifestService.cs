using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository.FileRepository;

namespace Idb.Libraries.Implementation.Services.Migrations;

public sealed class StorageContractManifestService : IStorageContractManifestService
{
    private readonly ILocalScopeService _scopeService;
    private readonly StorageContractManifestBuilder _builder;
    private readonly StorageContractManifestStore _store;

    public StorageContractManifestService(
        ILocalScopeService scopeService,
        StorageContractManifestBuilder builder,
        StorageContractManifestStore store)
    {
        _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IStorageContractManifest> SnapshotAsync<TContract>(CancellationToken cancellationToken = default)
        => SnapshotAsync(typeof(TContract), cancellationToken);

    public async Task<IStorageContractManifest> SnapshotAsync(Type contractType, CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope(contractType);
        var manifest = _builder.Build(contractType, scope.VersionTag);
        await _store.SaveAsync(scope, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public Task<IStorageContractDiff> CompareWithLatestAsync<TContract>(CancellationToken cancellationToken = default)
        => CompareWithLatestAsync(typeof(TContract), cancellationToken);

    public async Task<IStorageContractDiff> CompareWithLatestAsync(Type contractType, CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope(contractType);
        var current = _builder.Build(contractType, scope.VersionTag);
        var previous = await _store.LoadLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        return new StorageContractDiff(current, previous);
    }

    private IScopeDescriptor ResolveScope(Type contractType)
    {
        if (contractType is null)
        {
            throw new ArgumentNullException(nameof(contractType));
        }

        var scope = _scopeService.GetScope(contractType);
        if (scope is null)
        {
            throw new InvalidOperationException($"No storage scope registered for interface '{contractType.FullName}'.");
        }

        return scope;
    }

    private sealed class StorageContractDiff : IStorageContractDiff
    {
        public StorageContractDiff(IStorageContractManifest current, IStorageContractManifest? previous)
        {
            Current = current;
            Previous = previous;
        }

        public IStorageContractManifest Current { get; }
        public IStorageContractManifest? Previous { get; }
        public bool HasDifferences => Previous is null || !string.Equals(Current.Hash, Previous.Hash, StringComparison.Ordinal);
    }
}
