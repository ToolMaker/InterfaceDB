using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Transport.Repositories;

public sealed record TcpRepositoryOptions<TContract> where TContract : class, IStoredContract<TContract>
{
    public const string EnvelopeFormat = "IDB6-TCP-Repository-v1";
    /// <summary>The server access policy determines data scope. RequestedScope selects an authorized group/account when required.</summary>
    public required TcpStoreClientOptions Connection { get; init; }
    public required IDbContractConfigDto<TContract> Contract { get; init; }
    public required StoredRuntimeTypeAllowlist<TContract> RuntimeTypes { get; init; }
    public IEncryptor Encryptor { get; init; } = new EncryptionNone();
    /// <summary>Borrowed, fully configured serializer. Null selects the backward-compatible MessagePack default.
    /// Must preserve revisions; encrypted serializers must expose authenticated context and use Encryptor.
    /// The repository does not dispose this service. Shared instances must support concurrent callers.</summary>
    public IObjectSerializer<TContract>? Serializer { get; init; }
    /// <summary>Explicit format identity for the selected serializer, checked against the server manifest.</summary>
    public string SerializerEnvelopeFormat { get; init; } = EnvelopeFormat;
    public IEnvelopeStorageIdentityFactory<TContract>? StorageIdentityFactory { get; init; }
    /// <summary>Optional account-scoped metadata persistence for the last server-approved data boundary.
    /// Enables offline cache startup after a process restart for JWT identities. Native Provider mode
    /// first requires online account authentication. This is not an authorization grant.
    /// Reconnect always requires the server to approve this same boundary.</summary>
    public Idb.Libraries.Repositories.IRepositoryCursorStore? ScopeApprovalStore { get; init; }
    public StoragePolicy StoragePolicy { get; init; } = StoragePolicy.Default;
    public int MaximumSnapshotRecords { get; init; } = 10_000;
    public long MaximumSnapshotBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan AdmissionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    /// <summary>Borrowed admission-only retry policy; the total wait is bounded by AdmissionTimeout.</summary>
    public ITcpAdmissionRetryPolicy AdmissionRetryPolicy { get; init; } = TcpAdmissionRetryPolicy.Instance;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Connection);
        TransportValidation.Capabilities(Connection.RequiredCapabilities);
        ArgumentNullException.ThrowIfNull(Connection.Manifest);
        ArgumentNullException.ThrowIfNull(Connection.Limits);
        Connection.Limits.Validate();
        if (Connection.Limits.MaximumBatchItems < 2 || Connection.Limits.MaximumKeyBytes < 64)
            throw new ArgumentException("Typed repositories need at least two batch slots and 64 key bytes.");
        ArgumentNullException.ThrowIfNull(Contract);
        ArgumentNullException.ThrowIfNull(RuntimeTypes);
        ArgumentNullException.ThrowIfNull(Encryptor);
        ArgumentNullException.ThrowIfNull(StoragePolicy);
        ArgumentNullException.ThrowIfNull(AdmissionRetryPolicy);
        if (!typeof(TContract).IsInterface) throw new ArgumentException("The stored contract must be an interface.");
        if (Connection.AuthenticationMode is not (TcpStoreAuthenticationMode.JwtBearer or TcpStoreAuthenticationMode.Provider))
            throw new ArgumentException("Typed repositories require an authenticated account over TLS.");
        if (Connection.AuthenticationMode == TcpStoreAuthenticationMode.Provider) ArgumentNullException.ThrowIfNull(Connection.CredentialsProvider);
        else ArgumentNullException.ThrowIfNull(Connection.TokenProvider);
        TransportValidation.ControlString(SerializerEnvelopeFormat, false);
        if (Connection.Manifest.EnvelopeFormat != SerializerEnvelopeFormat)
            throw new ArgumentException("The manifest must match the selected serializer envelope format.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StoragePolicy.MaximumStoredObjectBytes);
        if (Connection.Manifest.ContractVersion != Contract.ActiveVersion)
            throw new ArgumentException("The manifest and contract baseline versions must match.");
        if (StoragePolicy.RetainedVersions is not null || StoragePolicy.RepositoryNotifications is not null)
            throw new NotSupportedException("Remote retained editing and entity notification snapshots are unsupported. Use SubscribeChanges for disposable refresh hints.");
        if (MaximumSnapshotRecords < 1 || MaximumSnapshotBytes < 16)
            throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotRecords));
        TransportValidation.Duration(AdmissionTimeout, nameof(AdmissionTimeout));
        TransportValidation.Duration(BusyRetryDelay, nameof(BusyRetryDelay));
    }

    internal IObjectSerializer<TContract> ResolveSerializer()
    {
        var serializer = Serializer;
        if (serializer is null)
        {
            if (SerializerEnvelopeFormat != EnvelopeFormat)
                throw new ArgumentException("A non-default envelope format requires an explicitly configured serializer.");
            var binary = new BinaryObjectSerializer<TContract>(
                maximumPayloadBytes: Math.Min(StoragePolicy.MaximumStoredObjectBytes, Connection.Limits.MaximumPayloadBytes),
                runtimeTypeAllowlist: RuntimeTypes);
            serializer = Encryptor is EncryptionNone ? binary : new EncryptingObjectSerializer<TContract>(binary, Encryptor);
        }
        if (serializer is not IRevisionObjectSerializer<TContract>)
            throw new ArgumentException("The configured serializer must implement IRevisionObjectSerializer.", nameof(Serializer));
        if (serializer is IContextualObjectSerializer<TContract> contextual)
        {
            if (!ReferenceEquals(contextual.Encryptor, Encryptor))
                throw new ArgumentException("The serializer and repository must use the same encryptor instance.", nameof(Serializer));
        }
        else if (Encryptor is not EncryptionNone)
            throw new ArgumentException("An encrypted repository requires a configured IContextualObjectSerializer.", nameof(Serializer));
        return serializer;
    }
}

/// <summary>The caller must reconcile these keys before deciding whether another mutation is safe.</summary>
public sealed class RemoteRepositoryCommitOutcomeUnknownException : IOException
{
    public IReadOnlyDictionary<string, long?> AttemptedRevisions { get; }
    public RemoteRepositoryCommitOutcomeUnknownException(IReadOnlyDictionary<string, long?> attemptedRevisions, Exception inner)
        : base("The repository batch may have committed. Read fresh state and reconcile the attempted keys; do not blindly retry.", inner)
        => AttemptedRevisions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long?>(
            new Dictionary<string, long?>(attemptedRevisions, StringComparer.Ordinal));
}
