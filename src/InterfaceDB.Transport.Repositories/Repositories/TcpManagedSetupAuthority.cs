using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Libraries.Repositories;

namespace Idb.Transport.Repositories;

/// <summary>Fetches one authorized setup before opening storage. Owns reconnect discovery; never downloads code or server configuration.</summary>
public sealed class TcpManagedSetupAuthority : IRepositoryVersionAuthority, IManagedRepositorySettings
{
    private readonly TcpStoreClientOptions _connection;
    private readonly string _name, _contract;
    private readonly string? _requestedProvider;
    private readonly IManagedSetupVerifier _verifier;
    private readonly IManagedSetupReceiptStore _receipts;
    private readonly TimeProvider _clock;
    private readonly SetupTokenProvider _tokens;
    private ManagedClientSetupGrant? _grant;
    public string Scope { get; }
    public CompatibilityManifest? LastApprovedManifest => Volatile.Read(ref _grant)?.Setup.Manifest;
    public ManagedClientSetup Setup => Volatile.Read(ref _grant)?.Setup ?? throw new InvalidOperationException("The server has not approved a client setup.");
    public string LocalProviderId => Setup.LocalProviderId;
    public RepositoryReadPreference ReadPreference => Setup.ReadPreference;
    public string SelectionIdentity => ManagedSetupProtocol.Hash(JsonSerializer.Serialize(Setup));
    public static string Audience(TcpStoreClientOptions connection) => JsonSerializer.Serialize(new[]
        { connection.Host.ToLowerInvariant(), connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), (connection.TargetHost ?? connection.Host).ToLowerInvariant() });

    public TcpManagedSetupAuthority(TcpStoreClientOptions connection, string setupName, string contractIdentity,
        IManagedSetupVerifier verifier, IManagedSetupReceiptStore receipts, string? requestedProvider = null, TimeProvider? clock = null)
    {
        if (connection.AuthenticationMode != TcpStoreAuthenticationMode.JwtBearer || connection.TokenProvider is null)
            throw new ArgumentException("Managed setup negotiation requires a JWT provider and TLS.", nameof(connection));
        TransportValidation.ControlString(contractIdentity, false);
        if (requestedProvider is not null) TransportValidation.Alias(requestedProvider);
        _name = setupName; _contract = contractIdentity; _requestedProvider = requestedProvider;
        _verifier = verifier; _receipts = receipts; _clock = clock ?? TimeProvider.System;
        _tokens = new(connection.TokenProvider);
        _connection = connection with
        {
            TokenProvider = _tokens, Manifest = ManagedSetupProtocol.Manifest, StoreAlias = ManagedSetupProtocol.Alias(contractIdentity, setupName),
            ExpectedScopeIdentity = null,
            RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits
        };
        Scope = Audience(connection) + "|" + setupName + "|" + contractIdentity + "|" + JsonSerializer.Serialize(connection.RequestedScope);
    }

    public TcpStoreClientOptions DataConnection(CompatibilityManifest manifest) => _connection with
    { StoreAlias = Setup.RemoteAlias, Manifest = manifest, RequestedScope = Setup.DataScope ?? _connection.RequestedScope, ExpectedScopeIdentity = _grant!.DataScopeIdentity, RequiredCapabilities = EncodedStoreCapabilities.All };

    public async ValueTask<CompatibilityManifest> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var receiptKey = ManagedSetupProtocol.Hash(Scope + "|" + _tokens.Partition);
        try
        {
            await using var connection = await TcpEncodedEnvelopeStore.ConnectAsync(_connection, cancellationToken).ConfigureAwait(false);
            var bytes = await connection.GetAsync(ManagedSetupProtocol.RequestKey(_contract, _requestedProvider), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The server did not return an approved client setup.");
            var grant = Validate(bytes);
            // Provider/dataset changes need an explicit cache migration, never an empty replacement that loses pending writes.
            var previous = Volatile.Read(ref _grant);
            if (previous is null && await _receipts.ReadAsync(receiptKey, cancellationToken).ConfigureAwait(false) is { } cached)
                previous = Decode(cached, requireUnexpired: false);
            if (previous is not null && (previous.Setup.LocalProviderId != grant.Setup.LocalProviderId || previous.Setup.RemoteAlias != grant.Setup.RemoteAlias
                || previous.Setup.DataScope != grant.Setup.DataScope || previous.DataScopeIdentity != grant.DataScopeIdentity))
                throw new InvalidOperationException("Changing a setup's provider, dataset or data scope requires an explicit cache migration or a new setup name.");
            await _receipts.WriteAsync(receiptKey, Convert.ToBase64String(bytes), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _grant, grant);
            return grant.Setup.Manifest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (TcpRepositoryAvailability.IsUnavailable(exception))
        {
            if (Volatile.Read(ref _grant) is null && await _receipts.ReadAsync(receiptKey, cancellationToken).ConfigureAwait(false) is { } cached)
                Volatile.Write(ref _grant, Decode(cached));
            // The coordinator handles downtime and its last committed version receipt. First-use offline startup still fails.
            if (Volatile.Read(ref _grant) is not null) await ValidateAccessAsync(false, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private ManagedClientSetupGrant Decode(string encoded, bool requireUnexpired = true)
    {
        try
        {
            if (encoded.Length > ManagedSetupProtocol.MaximumReceiptBytes * 2) throw new FormatException();
            return Validate(Convert.FromBase64String(encoded), requireUnexpired);
        }
        catch (FormatException) { throw new InvalidDataException("The cached setup approval is invalid."); }
    }
    private ManagedClientSetupGrant Validate(byte[] receipt, bool requireUnexpired = true)
    {
        var grant = ManagedSetupProtocol.Verify(receipt, _verifier);
        if (grant.Setup is null || grant.Setup.Manifest is null) throw new InvalidDataException("Invalid setup approval.");
        ManagedSetupProtocol.Validate(grant.Setup);
        TransportValidation.ControlString(grant.DataScopeIdentity, false);
        if (grant.RequestedScope != _connection.RequestedScope || grant.Setup.Name != _name || grant.Setup.Manifest.ContractIdentity != _contract
            || ( _requestedProvider is not null && grant.Setup.LocalProviderId != _requestedProvider)
            || grant.SubjectPartition != _tokens.Partition || grant.Audience != Audience(_connection))
            throw new UnauthorizedAccessException("The server setup approval does not cover this account, endpoint or request.");
        if (requireUnexpired && grant.ExpiresAt <= _clock.GetUtcNow()) throw new RepositoryAccessRenewalRequiredException();
        return grant;
    }
    public async ValueTask ValidateAccessAsync(bool writing, CancellationToken cancellationToken = default)
    {
        await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var grant = Volatile.Read(ref _grant) ?? throw new UnauthorizedAccessException("A server-approved client setup is required.");
        if (grant.SubjectPartition != _tokens.Partition)
            throw new UnauthorizedAccessException("The server setup approval belongs to another account.");
        if (grant.ExpiresAt <= _clock.GetUtcNow()) throw new RepositoryAccessRenewalRequiredException();
    }
    public async ValueTask ObserveAsync(CompatibilityManifest manifest, CancellationToken cancellationToken = default)
    {
        var remaining = (_grant ?? throw new InvalidOperationException("No approved setup.")).ExpiresAt - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return;
        // A lease deadline renews authorization; healthy storage is never polled for changes.
        using var renewal = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, remaining.Ticks * 4 / 5)), _clock);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, renewal.Token);
        await using var listener = await TcpEncodedEnvelopeStore.ConnectCoreAsync(DataConnection(manifest) with
        { RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate | EncodedStoreCapabilities.DurableCommits }, true, cancellationToken).ConfigureAwait(false);
        try { await listener.ReadChangesAsync(() => { }, observation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (renewal.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
    }

    private sealed class SetupTokenProvider(IAccessTokenProvider inner) : IAccessTokenProvider
    {
        private string? _partition;
        public string Partition => Volatile.Read(ref _partition) ?? throw new InvalidOperationException("No account selected.");
        public async ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var token = await inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Routing/account-switch guard only. JWT validation and permissions are enforced by the host.
                if (token.Value.Length > TransportValidation.MaximumAccessTokenBytes) throw new FormatException();
                var parts = token.Value.Split('.');
                if (parts.Length != 3) throw new FormatException();
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
                string Claim(string name)
                {
                    var claims = payload.RootElement.EnumerateObject().Where(p => p.Name == name).ToArray();
                    return claims.Length == 1 && claims[0].Value.ValueKind == JsonValueKind.String
                        && claims[0].Value.GetString() is { Length: > 0 and <= 4096 } value ? value : throw new FormatException();
                }
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true)) { writer.Write(Claim("iss")); writer.Write(Claim("sub")); }
                var partition = "idbp1." + Convert.ToHexString(SHA256.HashData(bytes.ToArray())) + ".";
                if (Interlocked.CompareExchange(ref _partition, partition, null) is { } prior && prior != partition)
                    throw new UnauthorizedAccessException("Create a separate managed repository after switching accounts.");
                return token;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
            { throw new UnauthorizedAccessException("A bounded JWT with one issuer and subject is required."); }
        }
    }
}
