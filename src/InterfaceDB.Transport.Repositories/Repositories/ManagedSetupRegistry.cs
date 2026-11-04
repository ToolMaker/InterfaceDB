using System.Text.Json;
using Idb.Authentication;

namespace Idb.Transport.Repositories;

/// <summary>Private server registration. Its store and authorization delegate are never serialized.</summary>
public sealed record ManagedSetupRegistration(ManagedClientSetup Client, IEncodedEnvelopeStore Store,
    Func<AuthenticatedIdentity, bool> Authorize)
{
    private static readonly IDataAccessPolicy DefaultPolicy = new SubjectDataAccessPolicy();
    /// <summary>Server-only data policy, never included in the signed client configuration.</summary>
    public IDataAccessPolicy AccessPolicy { get; init; } = DefaultPolicy;
    /// <summary>Server-only grant for preserving externally supplied revisions. Null denies remote imports.</summary>
    public IRepositoryRevisionImportPolicy? RevisionImportPolicy { get; init; }
}

/// <summary>Immutable host configuration. Restart the host to apply changes; clients rediscover after their subscription disconnects.</summary>
public sealed class ManagedSetupRegistry
{
    private const EncodedStoreCapabilities SetupCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits;
    private readonly ManagedSetupRegistration[] _setups;
    private readonly IReadOnlyList<TcpStoreRegistration> _stores;

    public ManagedSetupRegistry(IEnumerable<ManagedSetupRegistration> setups, string audience,
        IManagedSetupSigner signer, TimeSpan offlineLease, TimeProvider? timeProvider = null)
    {
        TransportValidation.ControlString(audience, false);
        ArgumentNullException.ThrowIfNull(signer);
        if (offlineLease <= TimeSpan.Zero || offlineLease > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(offlineLease));
        _setups = setups.ToArray();
        if (_setups.Length == 0 || _setups.Length > 256)
            throw new ArgumentException("Register between one and 256 client setups.");
        var identities = new HashSet<(string Contract, string Setup)>();
        var stores = new List<TcpStoreRegistration>();
        foreach (var setup in _setups)
        {
            ManagedSetupProtocol.Validate(setup.Client);
            if (!identities.Add((setup.Client.Manifest.ContractIdentity, setup.Client.Name)))
                throw new ArgumentException("Each contract interface must have uniquely named client setups.");
            ArgumentNullException.ThrowIfNull(setup.Authorize); ArgumentNullException.ThrowIfNull(setup.Store); ArgumentNullException.ThrowIfNull(setup.AccessPolicy);
            if (!setup.Store.Capabilities.HasFlag(EncodedStoreCapabilities.All & ~EncodedStoreCapabilities.ReserveRevisions)) throw new ArgumentException("Managed datasets require durable read/write and atomic batch support.");
            stores.Add(new(ManagedSetupProtocol.Alias(setup.Client.Manifest.ContractIdentity, setup.Client.Name), ManagedSetupProtocol.Manifest,
                new SetupStore(setup.Client, setup.AccessPolicy, audience, signer, offlineLease, timeProvider ?? TimeProvider.System))
                { AccessPolicy = new DelegateDataAccessPolicy(context => new SubjectDataAccessPolicy().Authorize(context with { RequestedScope = null })) });
        }
        foreach (var group in _setups.GroupBy(s => s.Client.RemoteAlias, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(s => !ReferenceEquals(s.Store, first.Store) || s.Client.Manifest != first.Client.Manifest || !ReferenceEquals(s.AccessPolicy, first.AccessPolicy) || !ReferenceEquals(s.RevisionImportPolicy, first.RevisionImportPolicy)))
                throw new ArgumentException("All setups for a dataset must share its store, data access policy and client compatibility identity.");
            if (_setups.Any(s => ReferenceEquals(s.Store, first.Store) && s.Client.RemoteAlias != first.Client.RemoteAlias))
                throw new ArgumentException("A physical dataset must have one data alias so its exclusive sessions share the same lock.");
            stores.Add(new(first.Client.RemoteAlias, first.Client.Manifest, first.Store) { AccessPolicy = first.AccessPolicy, RevisionImportPolicy = first.RevisionImportPolicy });
        }
        if (stores.Select(s => s.Alias).Distinct(StringComparer.Ordinal).Count() != stores.Count)
            throw new ArgumentException("Setup and dataset aliases must be distinct.");
        _stores = stores.AsReadOnly();
    }

    /// <summary>Installs only the registered managed endpoints. Existing identity authorization, if present, is also required.</summary>
    public TcpStoreHostOptions Configure(TcpStoreHostOptions options)
    {
        if (options.AuthenticationMode != TcpStoreAuthenticationMode.JwtBearer || options.TokenValidator is null)
            throw new ArgumentException("Managed setups require JWT authentication and a validator.", nameof(options));
        if (options.Stores.Count != 0) throw new ArgumentException("Let the setup registry supply host stores; alternate aliases would bypass its policy.", nameof(options));
        var additionalAuthorization = options.AuthorizeIdentity;
        return options with
        {
            Stores = _stores,
            AuthorizeIdentity = (identity, alias, capabilities) => Authorize(identity, alias, capabilities)
                && (additionalAuthorization?.Invoke(identity, alias, capabilities) ?? true)
        };
    }

    private bool Authorize(AuthenticatedIdentity identity, string alias, EncodedStoreCapabilities capabilities)
    {
        if (!DataAccessClaims.TrySubject(identity, out _, out _)) return false;
        foreach (var setup in _setups)
        {
            var setupEndpoint = alias == ManagedSetupProtocol.Alias(setup.Client.Manifest.ContractIdentity, setup.Client.Name);
            if (!setupEndpoint && alias != setup.Client.RemoteAlias) continue;
            var allowed = setupEndpoint ? SetupCapabilities
                : EncodedStoreCapabilities.All;
            if ((capabilities & ~allowed) == 0 && setup.Authorize(identity)) return true;
        }
        return false; // Missing and forbidden names have the same authorization result before lookup.
    }

    private sealed class SetupStore(ManagedClientSetup setup, IDataAccessPolicy dataPolicy, string audience, IManagedSetupSigner signer,
        TimeSpan lease, TimeProvider clock) : IAccessContextEncodedStore
    {
        public EncodedStoreCapabilities Capabilities => SetupCapabilities;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
            => throw Denied();
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, DataAccessContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Command != EncodedStoreCommand.Get || request.Key is null
                || !ScopedEncodedStore.TryGetPrefix(request.Key, out var prefix) || prefix != request.Partition)
                throw Denied();
            try
            {
                var query = JsonSerializer.Deserialize<ManagedSetupProtocol.SetupRequest>(Convert.FromBase64String(request.Key[prefix.Length..]));
                if (query is null || query.ContractIdentity != setup.Manifest.ContractIdentity
                    || (query.ProviderId is not null && query.ProviderId != setup.LocalProviderId)) throw Denied();
            }
            catch (Exception exception) when (exception is JsonException or FormatException) { throw Denied(); }
            DataAccessScope? dataScope;
            try { dataScope = dataPolicy.Authorize(new(context.Identity, setup.RemoteAlias,
                setup.DataScope ?? context.RequestedScope, SetupCapabilities)); }
            catch { throw Denied(); }
            if (dataScope is null) throw Denied();
            var receipt = ManagedSetupProtocol.Sign(new(setup, prefix, audience, clock.GetUtcNow() + lease)
                { DataScopeIdentity = dataScope.Identity, RequestedScope = context.RequestedScope }, signer);
            return ValueTask.FromResult(new EncodedStoreResponse { Found = true, Payload = receipt });
        }
        private static RemoteStoreException Denied() => new(RemoteErrorCode.Unauthorized, "The requested setup is unavailable.");
    }
}
