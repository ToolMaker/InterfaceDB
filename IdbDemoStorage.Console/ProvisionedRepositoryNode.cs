using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstraction.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;

namespace IdbDemoStorage.ConsoleHost;

internal sealed class ProvisionedRepositoryNode : IRepositoryNode
{
    private readonly IRepositoryNode _inner;
    private readonly ContractIndexProvisioningModeType _mode;
    private readonly string _contractDisplayName;
    private readonly Type _contractType;
    private readonly HashSet<string> _configuredIndexNames;
    private readonly bool _allowDynamicRegistrations;

    public ProvisionedRepositoryNode(
        IRepositoryNode inner,
        IDbContractOptions option,
        Type contractType)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (option is null)
        {
            throw new ArgumentNullException(nameof(option));
        }

        _contractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
        _mode = option.IndexProvisioningMode;
        _allowDynamicRegistrations = _mode is ContractIndexProvisioningModeType.Dynamic
                                              or ContractIndexProvisioningModeType.Mixed
                                              or ContractIndexProvisioningModeType.None;

        _contractDisplayName = string.IsNullOrWhiteSpace(option.ContractName)
            ? option.Key
            : option.ContractName;

        _configuredIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (option.Indexes is { Length: > 0 })
        {
            ApplyConfiguredIndexes(option.Indexes);
        }
    }

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

    public ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class
        => _inner.QueryAsync<TContract>(query, cancellationToken);

    public void Subscribe(IndexSubscription subscription, Action<IndexChange> handler)
        => _inner.Subscribe(subscription, handler);

    public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
        => _inner.Unsubscribe(subscription, handler);

    public void ConfigureIndex(IRepositoryIndexConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        EnsureContractType(configuration.ContractType);

        if (!_allowDynamicRegistrations && !_configuredIndexNames.Contains(configuration.Name))
        {
            throw new InvalidOperationException(
                $"Contract '{_contractDisplayName}' uses provisioning mode {_mode} and does not permit runtime index '{configuration.Name}' registrations.");
        }

        _inner.ConfigureIndex(configuration);
        _configuredIndexNames.Add(configuration.Name);
    }

    public void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
    {
        if (configurations is null)
        {
            throw new ArgumentNullException(nameof(configurations));
        }

        foreach (var configuration in configurations)
        {
            ConfigureIndex(configuration);
        }
    }

    public IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType)
        => _inner.GetIndexConfigurations(contractType);

    public bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull
        => _inner.TryGetSortedListIndex<TContract, TKey>(name, out index);

    private void ApplyConfiguredIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations)
    {
        foreach (var configuration in configurations)
        {
            if (configuration is null)
            {
                continue;
            }

            if (!IsApplicableConfiguration(configuration))
            {
                continue;
            }

            _inner.ConfigureIndex(configuration);
            _configuredIndexNames.Add(configuration.Name);
        }
    }

    private bool IsApplicableConfiguration(IRepositoryIndexConfiguration configuration)
        => configuration.ContractType == _contractType || _contractType.IsAssignableFrom(configuration.ContractType);

    private void EnsureContractType(Type requestedContract)
    {
        if (_contractType != requestedContract && !_contractType.IsAssignableFrom(requestedContract))
        {
            throw new InvalidOperationException(
                $"Index configuration contract '{requestedContract.FullName}' does not match repository contract '{_contractType.FullName}'.");
        }
    }
}
