using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Transport.Repositories;

/// <summary>Trusted, account-bound authority. Implementations authenticate discovery and own their notification connection.</summary>
public interface IRepositoryVersionAuthority
{
    string Scope { get; }
    string SelectionIdentity => string.Empty;
    /// <summary>Last authenticated approval, if the authority persists one. Offline activation must match it.</summary>
    CompatibilityManifest? LastApprovedManifest => null;
    ValueTask<CompatibilityManifest> DiscoverAsync(CancellationToken cancellationToken = default);
    /// <summary>Wait for authority changes. Return to request rediscovery; throw on failed observation. Never return repeatedly without waiting.</summary>
    ValueTask ObserveAsync(CompatibilityManifest manifest, CancellationToken cancellationToken = default);
    ValueTask ValidateAccessAsync(bool writing, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Deployed code for one baseline. Provider creation and migration are supplied through this interface.</summary>
public interface IRepositoryVersionPlan<T> where T : class, IStoredContract<T>
{
    CompatibilityManifest Manifest { get; }
    ValueTask PrepareAsync(RepositoryVersionContext context, CancellationToken cancellationToken);
    ValueTask<RepositoryLease<T>> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Compatibility adapter for the original TCP version-authority API.</summary>
public sealed class TcpRepositoryVersionAuthority(TcpStoreClientOptions options) : IRepositoryVersionAuthority
{
    public string Scope => options.Host + "|" + options.Port + "|" + options.StoreAlias
        + "|data-policy-v1|" + System.Text.Json.JsonSerializer.Serialize(new[] { options.RequestedScope, options.ExpectedScopeIdentity });
    public ValueTask<CompatibilityManifest> DiscoverAsync(CancellationToken cancellationToken = default)
        => TcpEncodedEnvelopeStore.DiscoverAsync(options, cancellationToken);
    public async ValueTask ObserveAsync(CompatibilityManifest manifest, CancellationToken cancellationToken = default)
    {
        await using var listener = await TcpEncodedEnvelopeStore.ConnectCoreAsync(options with
        {
            Manifest = manifest,
            RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate | EncodedStoreCapabilities.DurableCommits
        }, true, cancellationToken).ConfigureAwait(false);
        try { await listener.ReadChangesAsync(() => { }, cancellationToken).ConfigureAwait(false); }
        catch (RemoteStoreException error) when (error.Code == RemoteErrorCode.AuthenticationExpired)
        {
            // A normal return requests immediate authenticated rediscovery. It does not authorize
            // an expired session or label expected renewal as an infrastructure outage.
        }
    }
}
