using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Idb.Authentication;

namespace Idb.Transport;

/// <summary>Host-configured compatibility identity. Values are compared ordinally, not trusted as attestation.</summary>
public sealed record CompatibilityManifest
{
    public required string ApiVersion { get; init; }
    public required string StorageEngine { get; init; }
    public required string StorageFormat { get; init; }
    public required string EnvelopeFormat { get; init; }
    public required string ContractIdentity { get; init; }
    public required string ContractVersion { get; init; }
    public required string ContractSchemaHash { get; init; }
    public required string ImplementationManifestHash { get; init; }
    public required string IndexDefinitionHash { get; init; }
    public string? ApprovedBuildIdentity { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { ApiVersion, StorageEngine, StorageFormat, EnvelopeFormat, ContractIdentity,
            ContractVersion, ContractSchemaHash, ImplementationManifestHash, IndexDefinitionHash })
            TransportValidation.ControlString(value, false);
        TransportValidation.ControlString(ApprovedBuildIdentity, true);
    }
}

public sealed record TransportLimits
{
    public int MaximumFrameBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumPayloadBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumKeyBytes { get; init; } = 1024;
    public int MaximumBatchItems { get; init; } = 64;
    public int MaximumPageItems { get; init; } = 128;

    public void Validate()
    {
        if (MaximumFrameBytes is < 16 * 1024 or > 64 * 1024 * 1024
            || MaximumPayloadBytes < 1 || MaximumPayloadBytes > MaximumFrameBytes / 2
            || MaximumKeyBytes is < 1 or > 4096 || MaximumBatchItems is < 1 or > 1024
            || MaximumPageItems is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(TransportLimits), "Invalid transport resource limits.");
    }

    internal TransportLimits Intersect(TransportLimits other) => new()
    {
        MaximumFrameBytes = Math.Min(MaximumFrameBytes, other.MaximumFrameBytes),
        MaximumPayloadBytes = Math.Min(MaximumPayloadBytes, other.MaximumPayloadBytes),
        MaximumKeyBytes = Math.Min(MaximumKeyBytes, other.MaximumKeyBytes),
        MaximumBatchItems = Math.Min(MaximumBatchItems, other.MaximumBatchItems),
        MaximumPageItems = Math.Min(MaximumPageItems, other.MaximumPageItems)
    };
}

public enum RemoteErrorCode
{
    None, InvalidRequest, IncompatibleManifest, Unauthorized, UnknownStore, Busy,
    UnsupportedCapability, LimitExceeded, StorageFailure, OutcomeUnknown, DeadlineExceeded, ProtocolError,
    AuthenticationRequired, AuthenticationExpired
}

public enum TcpStoreAuthenticationMode { MutualTls, JwtBearer, MutualTlsAndJwtBearer }

public class RemoteStoreException : IOException
{
    public RemoteStoreException(RemoteErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
    public RemoteErrorCode Code { get; }
}

/// <summary>A mutation may have committed. Reconcile with storage before deciding to retry.</summary>
public sealed class RemoteCommitOutcomeUnknownException : RemoteStoreException
{
    public RemoteCommitOutcomeUnknownException(Exception? innerException = null)
        : base(RemoteErrorCode.OutcomeUnknown, "The remote mutation may have committed; its acknowledgement was not received. Reconcile before retrying.", innerException) { }
}

/// <summary>Public compatibility and opaque data boundary approved by the server.</summary>
public sealed record TcpStoreDescriptor(CompatibilityManifest Manifest, string ScopeIdentity);

public sealed record TcpStoreClientOptions
{
    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string StoreAlias { get; init; }
    /// <summary>Untrusted group/account selector, authorized by the server policy.</summary>
    public string? RequestedScope { get; init; }
    /// <summary>Optional opaque scope identity from a previous server approval; mismatch rejects the connection.</summary>
    public string? ExpectedScopeIdentity { get; init; }
    public required CompatibilityManifest Manifest { get; init; }
    public X509Certificate2? ClientCertificate { get; init; }
    public TcpStoreAuthenticationMode AuthenticationMode { get; init; } = TcpStoreAuthenticationMode.MutualTls;
    /// <summary>Obtains a caller-supplied or identity-provider-issued token before connecting. Never replays storage operations.</summary>
    public IAccessTokenProvider? TokenProvider { get; init; }
    public EncodedStoreCapabilities RequiredCapabilities { get; init; } = EncodedStoreCapabilities.All;
    public TransportLimits Limits { get; init; } = new();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Defaults to Host. Set when connecting by IP to a certificate with a DNS name.</summary>
    public string? TargetHost { get; init; }
    /// <summary>Optional explicit trust policy, e.g. private CA validation. Null uses platform chain/hostname validation.</summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }
}

/// <summary>The host borrows the store; stop the host before disposing the store.</summary>
public sealed record TcpStoreRegistration(string Alias, CompatibilityManifest Manifest, IEncodedEnvelopeStore Store)
{
    /// <summary>Server-owned policy; null inherits DefaultAccessPolicy. Use UnrestrictedDataAccessPolicy to explicitly opt out.</summary>
    public IDataAccessPolicy? AccessPolicy { get; init; }
}

public sealed record TcpStoreHostOptions
{
    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; init; }
    public IPEndPoint ListenEndPoint { get; init; } = new(IPAddress.Loopback, 7443);
    public required X509Certificate2 ServerCertificate { get; init; }
    public required IReadOnlyList<TcpStoreRegistration> Stores { get; init; }
    /// <summary>Required authorization of the authenticated client certificate for the selected alias.</summary>
    public Func<X509Certificate2, string, bool>? AuthorizeClient { get; init; }
    public TcpStoreAuthenticationMode AuthenticationMode { get; init; } = TcpStoreAuthenticationMode.MutualTls;
    public IAccessTokenValidator? TokenValidator { get; init; }
    /// <summary>Required in JWT modes: authorize the validated identity for this alias and requested capabilities.</summary>
    public Func<AuthenticatedIdentity, string, EncodedStoreCapabilities, bool>? AuthorizeIdentity { get; init; }
    /// <summary>Optional default data policy. Null leaves authenticated data unpartitioned; authentication still applies.</summary>
    public IDataAccessPolicy? DefaultAccessPolicy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Optional explicit client trust policy. Null uses platform chain validation.</summary>
    public RemoteCertificateValidationCallback? ClientCertificateValidation { get; init; }
    public TransportLimits Limits { get; init; } = new();
    public int MaximumConnections { get; init; } = 16;
    /// <summary>Maximum long-lived listeners. Null reserves half the connection budget for subscriptions.</summary>
    public int? MaximumSubscriptions { get; init; }
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Maximum lifetime for admitting operations to a storage session. Activity cannot extend it.</summary>
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumOperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional local diagnostics. Exceptions and paths are never sent to the remote client.</summary>
    public Action<Exception>? OnError { get; init; }
}

internal static partial class TransportValidation
{
    internal static void ConfigureSubscriptionKeepAlive(System.Net.Sockets.Socket socket)
    {
        // Detect a silently broken idle connection without querying repository state.
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 10);
        socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    internal const int MaximumHelloFrameBytes = 64 * 1024;
    internal const int MaximumAccessTokenBytes = 16 * 1024;
    internal const int MaximumControlStringBytes = 1024;
    internal static bool RequiresCertificate(TcpStoreAuthenticationMode mode) => mode != TcpStoreAuthenticationMode.JwtBearer;
    internal static bool RequiresToken(TcpStoreAuthenticationMode mode) => mode != TcpStoreAuthenticationMode.MutualTls;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void ControlString(string? value, bool nullable)
    {
        if (value is null && nullable) return;
        if (string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > MaximumControlStringBytes)
            throw new ArgumentException("A required compatibility value is missing or too long.");
    }

    internal static void Alias(string alias)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 128
            || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Store aliases must contain 1-128 ASCII letters, digits, hyphens or underscores.", nameof(alias));
    }

    internal static void Duration(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(name);
    }

    internal static void Capabilities(EncodedStoreCapabilities capabilities)
    {
        if ((capabilities & ~EncodedStoreCapabilities.All) != 0
            || !capabilities.HasFlag(EncodedStoreCapabilities.DurableCommits))
            throw new ArgumentException("Known capabilities and durable commits are required.");
    }


    internal static void Request(EncodedStoreRequest request, TransportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Command)) throw new ArgumentException("Unknown storage command.");
        void Key(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || StrictUtf8.GetByteCount(key) > limits.MaximumKeyBytes)
                throw new ArgumentException("A storage key is missing or too long.");
        }
        void Payload(byte[]? payload)
        {
            if (payload is null || payload.Length > limits.MaximumPayloadBytes)
                throw new ArgumentException("A payload is missing or exceeds the configured limit.");
        }
        // Reject irrelevant fields too: there is one canonical shape for each operation.
        if ((request.Command is not (EncodedStoreCommand.Get or EncodedStoreCommand.Put or EncodedStoreCommand.Delete) && request.Key is not null)
            || (request.Command != EncodedStoreCommand.Put && request.Payload is not null)
            || (request.Command != EncodedStoreCommand.GetMany && request.Keys is not null)
            || (request.Command != EncodedStoreCommand.ApplyBatch && request.Mutations is not null)
            || (request.Command is not (EncodedStoreCommand.Enumerate or EncodedStoreCommand.Changes) && (request.AfterKey is not null || request.PageSize != 0)))
            throw new ArgumentException("Unexpected storage command fields.");
        switch (request.Command)
        {
            case EncodedStoreCommand.Get:
            case EncodedStoreCommand.Delete: Key(request.Key); break;
            case EncodedStoreCommand.Put: Key(request.Key); Payload(request.Payload); break;
            case EncodedStoreCommand.GetMany:
                if (request.Keys is null || request.Keys.Length < 1 || request.Keys.Length > limits.MaximumBatchItems)
                    throw new ArgumentException("Invalid key batch size.");
                foreach (var key in request.Keys) Key(key);
                break;
            case EncodedStoreCommand.ApplyBatch:
                if (request.Mutations is null || request.Mutations.Length < 1 || request.Mutations.Length > limits.MaximumBatchItems)
                    throw new ArgumentException("Invalid mutation batch size.");
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mutation in request.Mutations)
                {
                    ArgumentNullException.ThrowIfNull(mutation);
                    Key(mutation.Key);
                    if (!unique.Add(mutation.Key)) throw new ArgumentException("Duplicate mutation keys are not allowed.");
                    if (mutation.Payload is not null) Payload(mutation.Payload);
                }
                break;
            case EncodedStoreCommand.Changes:
            case EncodedStoreCommand.Enumerate:
                if (request.AfterKey is not null) Key(request.AfterKey);
                if (request.PageSize < 1 || request.PageSize > limits.MaximumPageItems)
                    throw new ArgumentException("Invalid page size.");
                break;
        }
    }
}

internal enum WireMessageKind { Hello = 1, HelloResult, Request, Response, Close, Subscribe, Changed, Discover }

internal sealed record WireMessage
{
    public required WireMessageKind Kind { get; init; }
    public long CorrelationId { get; init; }
    public string? StoreAlias { get; init; }
    public CompatibilityManifest? Manifest { get; init; }
    public TransportLimits? Limits { get; init; }
    public EncodedStoreCapabilities Capabilities { get; init; }
    public EncodedStoreRequest? Request { get; init; }
    public EncodedStoreResponse? Response { get; init; }
    public RemoteErrorCode Error { get; init; }
    public int TimeoutMilliseconds { get; init; }
    public string? AccessToken { get; init; }
    public TcpStoreAuthenticationMode AuthenticationMode { get; init; }
    public string? RequestedScope { get; init; }
    public string? ScopeIdentity { get; init; }
    public override string ToString() => $"WireMessage {{ Kind = {Kind}, CorrelationId = {CorrelationId} }}";
}
