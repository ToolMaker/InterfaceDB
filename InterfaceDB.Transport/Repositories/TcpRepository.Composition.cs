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
            await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var identity = ((BoundTokenProvider)_tokens).Identity;
            var fields = new[]
            {
                _options.Connection.Host, _options.Connection.Port.ToString(CultureInfo.InvariantCulture),
                _options.Connection.TargetHost, _options.Connection.StoreAlias,
                _options.Connection.Manifest.ContractIdentity, _options.Connection.Manifest.ContractVersion,
                identity.Issuer, identity.Subject
            };
            // Keep existing cache identities stable; new formats must not reuse their pending-write namespace.
            if (_options.SerializerEnvelopeFormat != TcpRepositoryOptions<TContract>.EnvelopeFormat)
                fields = [.. fields, _options.SerializerEnvelopeFormat];
            var descriptor = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fields);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(descriptor));
        }
        finally { _gate.Release(); }
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
        RemoteStoreException remote => remote.Code is RemoteErrorCode.Busy or RemoteErrorCode.DeadlineExceeded,
        SocketException or TimeoutException or RepositoryEndpointUnavailableException => true,
        OperationCanceledException => true, // Composition first checks the caller's cancellation token.
        EndOfStreamException => true,
        IOException io when io.InnerException is SocketException => true,
        _ => false
    };
}
