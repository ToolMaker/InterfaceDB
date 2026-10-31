using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace Idb.Authentication;

/// <summary>Native database authentication and account management; storage and policy are injected.</summary>
public sealed class LocalAccountAuthenticationProvider : IAuthenticationProvider, IDatabaseAccountManager, IDisposable
{
    private readonly IDatabaseAccountStore _store;
    private readonly IPasswordHasher _hasher;
    private readonly IAccountCreationPolicy _creation;
    private readonly LocalAccountOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _passwordSlots;
    private readonly string _dummyHash;
    private readonly object _rateGate = new();
    private DateTimeOffset _window;
    private int _attempts;
    private int _queuedPasswordChecks;
    private int _disposed;
    public bool RequiresDataIsolation => _options.AccountMode == DatabaseAccountMode.MultipleAccounts;

    public async ValueTask ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var count = await _store.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count > (_options.AccountMode == DatabaseAccountMode.SingleAccount ? 1 : _options.MaximumAccounts))
            throw new ArgumentException("The existing credential store exceeds the configured account limit.");
    }

    public LocalAccountAuthenticationProvider(IDatabaseAccountStore store, IPasswordHasher hasher,
        IAccountCreationPolicy creation, LocalAccountOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _creation = creation ?? throw new ArgumentNullException(nameof(creation));
        _options = options with { NewAccountRoles = options.NewAccountRoles.ToArray(),
            CreatorAccountIds = options.CreatorAccountIds.ToArray(), CreatorRoles = options.CreatorRoles.ToArray() };
        _clock = timeProvider ?? TimeProvider.System;
        _passwordSlots = new(options.MaximumConcurrentPasswordChecks);
        _dummyHash = _hasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public async ValueTask<AuthenticatedIdentity> AuthenticateAsync(AuthenticationCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (credentials.Scheme != "password" || credentials.Secret is null || credentials.Secret.Length > 8192) throw Denied();
        PasswordLogin login;
        try { login = JsonSerializer.Deserialize<PasswordLogin>(credentials.Secret) ?? throw Denied(); }
        catch (JsonException) { throw Denied(); }
        string username;
        try { username = AccountCredentialValidation.NormalizeUsername(login.Username); }
        catch (ArgumentException) { throw Denied(); }
        if (login.Password is null || System.Text.Encoding.UTF8.GetByteCount(login.Password) > 1024) throw Denied();
        await EnterPasswordWorkAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await _store.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
            // Unknown and disabled accounts still perform a normal password verification.
            var valid = _hasher.Verify(login.Password, account?.PasswordHash ?? _dummyHash);
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid || account is null || account.Disabled) throw Denied();
            var claims = new List<Claim> { new("iss", _options.Authority), new("sub", account.Id),
                new("name", account.Username), new("idb:security-version", account.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            claims.AddRange(account.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            foreach (var entry in account.Claims)
            {
                if (ReservedClaim(entry.Key)) throw Denied();
                claims.AddRange(entry.Value.Select(value => new Claim(entry.Key, value)));
            }
            return new(new ClaimsPrincipal(new ClaimsIdentity(claims, "database-password", "name", ClaimTypes.Role)),
                _clock.GetUtcNow() + _options.SessionLifetime, _options.Authority);
        }
        finally { _passwordSlots.Release(); }
    }

    public async ValueTask<bool> IsSessionValidAsync(AuthenticatedIdentity identity, CancellationToken cancellationToken = default)
    {
        if (identity.ExpiresAt <= _clock.GetUtcNow() || identity.Principal.Identity?.IsAuthenticated != true) return false;
        var actor = identity.GetAccountIdentity();
        if (actor.Authority != _options.Authority) return false;
        var account = await _store.FindByIdAsync(actor.Subject, cancellationToken).ConfigureAwait(false);
        var versions = identity.Principal.FindAll("idb:security-version").Take(2).ToArray();
        return account is { Disabled: false } && versions.Length == 1
            && versions[0].Value == account.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask<AccountIdentity> CreateAsync(string username, string password, AuthenticatedIdentity? actor, CancellationToken cancellationToken = default)
    {
        if (actor is not null && !await IsSessionValidAsync(actor, cancellationToken).ConfigureAwait(false)) throw Denied();
        if (!_creation.CanCreate(actor)) throw Denied();
        return await CreateCoreAsync(username, password, _options.NewAccountRoles, new(), false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Trusted local bootstrap only. Atomically succeeds only while the credential store is empty; never exposed on TCP.</summary>
    public ValueTask<AccountIdentity> ProvisionFirstAccountAsync(string username, string password, CancellationToken cancellationToken = default)
        => CreateCoreAsync(username, password, ["database-user", .. _options.CreatorRoles], new(), true, cancellationToken);

    /// <summary>Trusted local administrative provisioning. Network registration cannot supply roles or claims.</summary>
    public async ValueTask<AccountIdentity> CreateManagedAccountAsync(string username, string password, string[] roles,
        Dictionary<string, string[]> claims, AuthenticatedIdentity administrator, CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(administrator, cancellationToken).ConfigureAwait(false);
        ValidateGrants(roles, claims);
        return await CreateCoreAsync(username, password, roles, claims, false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetDisabledAsync(string accountId, bool disabled, AuthenticatedIdentity administrator, CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(administrator, cancellationToken).ConfigureAwait(false);
        var account = await _store.FindByIdAsync(accountId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        if (!await _store.TryUpdateAsync(account with { Disabled = disabled, SecurityVersion = checked(account.SecurityVersion + 1) }, account.SecurityVersion, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The account changed concurrently.");
    }

    public async ValueTask ChangePasswordAsync(string currentPassword, string newPassword, AuthenticatedIdentity actor, CancellationToken cancellationToken = default)
    {
        if (!await IsSessionValidAsync(actor, cancellationToken).ConfigureAwait(false)) throw Denied();
        AccountCredentialValidation.ValidatePassword(newPassword);
        await EnterPasswordWorkAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await _store.FindByIdAsync(actor.GetAccountIdentity().Subject, cancellationToken).ConfigureAwait(false) ?? throw Denied();
            if (!_hasher.Verify(currentPassword, account.PasswordHash)) throw Denied();
            var updated = account with { PasswordHash = _hasher.Hash(newPassword), SecurityVersion = checked(account.SecurityVersion + 1) };
            if (!await _store.TryUpdateAsync(updated, account.SecurityVersion, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The account changed concurrently.");
        }
        finally { _passwordSlots.Release(); }
    }

    private async ValueTask RequireAdministratorAsync(AuthenticatedIdentity actor, CancellationToken token)
    {
        if (!await IsSessionValidAsync(actor, token).ConfigureAwait(false)
            || !(_options.CreatorAccountIds.Contains(actor.GetAccountIdentity().Subject, StringComparer.Ordinal)
                || _options.CreatorRoles.Any(actor.Principal.IsInRole))) throw Denied();
    }

    private async ValueTask<AccountIdentity> CreateCoreAsync(string username, string password, string[] roles,
        Dictionary<string, string[]> claims, bool onlyIfEmpty, CancellationToken token)
    {
        username = AccountCredentialValidation.NormalizeUsername(username); AccountCredentialValidation.ValidatePassword(password);
        await EnterPasswordWorkAsync(token).ConfigureAwait(false);
        try
        {
            var account = new DatabaseAccount(Guid.NewGuid().ToString("N"), username, _hasher.Hash(password), roles.ToArray(),
                claims.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
            var maximum = _options.AccountMode == DatabaseAccountMode.SingleAccount ? 1 : _options.MaximumAccounts;
            if (!await _store.TryCreateAsync(account, maximum, onlyIfEmpty, token).ConfigureAwait(false))
                throw new AccountCreationRejectedException();
            return new(_options.Authority, account.Id);
        }
        finally { _passwordSlots.Release(); }
    }

    private async ValueTask EnterPasswordWorkAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await _passwordSlots.WaitAsync(0, token).ConfigureAwait(false))
        {
            var queued = Interlocked.Increment(ref _queuedPasswordChecks);
            try
            {
                if (queued > _options.MaximumQueuedPasswordChecks
                    || !await _passwordSlots.WaitAsync(_options.PasswordAdmissionTimeout, token).ConfigureAwait(false))
                    throw new AuthenticationBusyException();
            }
            finally { Interlocked.Decrement(ref _queuedPasswordChecks); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_rateGate)
            {
                var now = _clock.GetUtcNow();
                if (now - _window >= TimeSpan.FromMinutes(1)) { _window = now; _attempts = 0; }
                if (_attempts >= _options.AttemptsPerMinute) throw new AuthenticationBusyException();
                _attempts++;
            }
        }
        catch { _passwordSlots.Release(); throw; }
    }

    private static bool ReservedClaim(string type) => type is "iss" or "sub" or "name" or "exp" or "nbf" or "iat"
        or ClaimTypes.Role or ClaimTypes.NameIdentifier || type.StartsWith("idb:", StringComparison.Ordinal);
    private static void ValidateGrants(string[] roles, Dictionary<string, string[]> claims)
    {
        if (roles is null || claims is null || roles.Length > 32 || claims.Count > 32
            || roles.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 256)
            || claims.Any(c => string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 256 || ReservedClaim(c.Key)
                || c.Value is null || c.Value.Length > 32 || c.Value.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256)))
            throw new ArgumentException("Invalid account grants.");
    }
    private static UnauthorizedAccessException Denied() => new("Authentication or account authorization failed.");
    public void Dispose()
    {
        // Multiple DI service interfaces can reference this same singleton.
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _passwordSlots.Dispose();
        // Store lifetime belongs to the composition root.
    }
}
