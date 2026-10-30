using System.Globalization;
using System.Net.Sockets;
using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Transport.Repositories;

public sealed partial class TcpRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    public async ValueTask<string> GetCacheIdentityAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _gate.WaitAsync(_options.AdmissionTimeout, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("Timed out validating the repository account.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_tokens is not null) await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            else await DiscoverProviderAccountAsync(cancellationToken).ConfigureAwait(false);
            await EnsureScopeAsync(cancellationToken).ConfigureAwait(false);
            var identity = _tokens is BoundTokenProvider bound ? bound.Identity : (_accountIdentity!.Authority, _accountIdentity.Subject);
            var fields = new[]
            {
                _options.Connection.Host, _options.Connection.Port.ToString(CultureInfo.InvariantCulture),
                _options.Connection.TargetHost, _options.Connection.StoreAlias,
                _options.Connection.Manifest.ContractIdentity, _options.Connection.Manifest.ContractVersion,
                identity.Item1, identity.Item2, "data-policy-v1", Volatile.Read(ref _scopeIdentity)
            };
            // Keep existing cache identities stable; new formats must not reuse their pending-write namespace.
            if (_options.SerializerEnvelopeFormat != TcpRepositoryOptions<TContract>.EnvelopeFormat)
                fields = [.. fields, _options.SerializerEnvelopeFormat];
            var descriptor = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fields);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(descriptor));
        }
        finally { _gate.Release(); }
    }

    private readonly SemaphoreSlim _scopeGate = new(1, 1);
    private bool _scopeLoaded;

    private async ValueTask EnsureScopeAsync(CancellationToken token)
    {
        await _scopeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_scopeLoaded) return;
            if (_tokens is not null) await _tokens.GetAccessTokenAsync(token).ConfigureAwait(false);
            else await DiscoverProviderAccountAsync(token).ConfigureAwait(false);
            var actor = _tokens is BoundTokenProvider bound ? bound.Identity : (_accountIdentity!.Authority, _accountIdentity.Subject);
            var keyBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] {
                "scope-approval-v1", _options.Connection.Host, _options.Connection.Port.ToString(CultureInfo.InvariantCulture),
                _options.Connection.TargetHost, _options.Connection.StoreAlias, _options.Connection.Manifest.ContractIdentity,
                actor.Item1, actor.Item2, _options.Connection.RequestedScope });
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(keyBytes));
            var persistence = _options.ScopeApprovalStore;
            if (persistence is not null && await persistence.ReadAsync(key, token).ConfigureAwait(false) is { } remembered)
            {
                if (remembered != DataAccessScope.Unrestricted.Identity
                    && (!ScopedEncodedStore.TryGetPrefix(remembered + "x", out var prefix) || prefix != remembered))
                    throw new InvalidDataException("Invalid stored data scope identity.");
                BindScope(remembered);
            }
            if (Volatile.Read(ref _scopeIdentity) is null)
            {
                var approval = await TcpEncodedEnvelopeStore.DiscoverStoreAsync(_options.Connection with
                    { TokenProvider = _tokens }, token).ConfigureAwait(false);
                BindScope(approval.ScopeIdentity);
            }
            if (persistence is not null) await persistence.WriteAsync(key, _scopeIdentity!, token).ConfigureAwait(false);
            _scopeLoaded = true;
        }
        finally { _scopeGate.Release(); }
    }

    private async ValueTask DiscoverProviderAccountAsync(CancellationToken token)
    {
        var approval = await TcpEncodedEnvelopeStore.DiscoverStoreAsync(_options.Connection with
            { ExpectedAccountIdentity = Volatile.Read(ref _accountIdentity), ExpectedScopeIdentity = Volatile.Read(ref _scopeIdentity) }, token).ConfigureAwait(false);
        BindAccount(approval.AccountIdentity);
        BindScope(approval.ScopeIdentity);
    }

    public StorageDurabilityMode CommitDurability => StorageDurabilityMode.Durable;

    public ValueTask<TContract> SaveWithExtensionMetadataAsync(TContract detachedEntity, string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata, EnvelopeWriteCondition condition, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.SaveWithExtensionMetadataAsync(detachedEntity,
            existingEnvelopeCacheKey, extensionMetadata, condition, cancellationToken), cancellationToken);

    public ValueTask<TContract> SaveWithExtensionMetadataAsync(TContract detachedEntity, string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.SaveWithExtensionMetadataAsync(detachedEntity,
            existingEnvelopeCacheKey, extensionMetadata, cancellationToken), cancellationToken);

}

/// <summary>Conservative read fallback classification. Authentication, corruption and uncertain commits are never outages.</summary>
public static class TcpRepositoryAvailability
{
    public static bool IsUnavailable(Exception exception) => exception switch
    {
        RemoteRepositoryCommitOutcomeUnknownException => false,
        Idb.Authentication.TokenAcquisitionException token => token.Failure is
            Idb.Authentication.TokenAcquisitionFailure.Timeout or Idb.Authentication.TokenAcquisitionFailure.Network,
        RemoteStoreException remote => remote.Code is RemoteErrorCode.Busy or RemoteErrorCode.DeadlineExceeded,
        SocketException or TimeoutException or RepositoryEndpointUnavailableException => true,
        OperationCanceledException => true, // Composition first checks the caller's cancellation token.
        EndOfStreamException => true,
        IOException io when io.InnerException is SocketException => true,
        _ => false
    };
}
