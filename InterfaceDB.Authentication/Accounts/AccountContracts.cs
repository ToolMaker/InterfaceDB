namespace Idb.Authentication;

public enum DatabaseAccountMode { SingleAccount = 1, MultipleAccounts = 2 }
public enum AccountCreationMode { Administrators = 0, Everyone = 1, Disabled = 2 }

/// <summary>Server configuration. Account privileges never come from a registration request.</summary>
public sealed record LocalAccountOptions
{
    public required string Authority { get; init; }
    public required DatabaseAccountMode AccountMode { get; init; }
    public AccountCreationMode CreationMode { get; init; } = AccountCreationMode.Administrators;
    public string[] CreatorAccountIds { get; init; } = [];
    public string[] CreatorRoles { get; init; } = ["account-admin"];
    public string[] NewAccountRoles { get; init; } = ["database-user"];
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumAccounts { get; init; } = 10_000;
    public int MaximumConcurrentPasswordChecks { get; init; } = 2;
    public int AttemptsPerMinute { get; init; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Authority) || Authority.Length > 256 || !Authority.StartsWith("idb-local:", StringComparison.Ordinal))
            throw new ArgumentException("A stable idb-local: authority is required.");
        if (!Enum.IsDefined(AccountMode) || !Enum.IsDefined(CreationMode) || MaximumAccounts is < 1 or > 100_000
            || MaximumConcurrentPasswordChecks is < 1 or > 32 || AttemptsPerMinute is < 1 or > 10_000
            || SessionLifetime < TimeSpan.FromSeconds(1) || SessionLifetime > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Invalid local account limits or mode.");
        foreach (var values in new[] { CreatorAccountIds, CreatorRoles, NewAccountRoles })
            if (values is null || values.Length > 32 || values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256))
                throw new ArgumentException("Invalid configured account permissions.");
        if (NewAccountRoles.Intersect(CreatorRoles, StringComparer.Ordinal).Any())
            throw new ArgumentException("New accounts cannot automatically receive account-creation roles.");
    }
}

/// <summary>Internal credential-store record. Never expose through application repositories or remote responses.</summary>
public sealed record DatabaseAccount(string Id, string Username, string PasswordHash, string[] Roles,
    Dictionary<string, string[]> Claims, long SecurityVersion = 1, bool Disabled = false)
{
    public override string ToString() => "[PROTECTED DATABASE ACCOUNT]";
}

/// <summary>Exclusive server-owned store. Implementations must durably and atomically enforce uniqueness and account limits.</summary>
public interface IDatabaseAccountStore : IAsyncDisposable
{
    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);
    ValueTask<DatabaseAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);
    ValueTask<DatabaseAccount?> FindByIdAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<bool> TryCreateAsync(DatabaseAccount account, int maximumAccounts, bool onlyIfEmpty, CancellationToken cancellationToken = default);
    ValueTask<bool> TryUpdateAsync(DatabaseAccount account, long expectedSecurityVersion, CancellationToken cancellationToken = default);
}

/// <summary>A proven rejection before a credential write was attempted.</summary>
public sealed class AccountCreationRejectedException() : InvalidOperationException("The account could not be created.");

/// <summary>A credential-store capacity rejection proven to occur before attempting a write.</summary>
public sealed class AccountStoreCapacityException() : InvalidOperationException("The credential store has reached its configured capacity.");

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string encodedHash);
}

public interface IAccountCreationPolicy
{
    bool CanCreate(AuthenticatedIdentity? actor);
}

/// <summary>Management boundary; raw records and privilege fields are never accepted from remote registration.</summary>
public interface IDatabaseAccountManager
{
    ValueTask<AccountIdentity> CreateAsync(string username, string password, AuthenticatedIdentity? actor, CancellationToken cancellationToken = default);
}

public sealed class ConfiguredAccountCreationPolicy : IAccountCreationPolicy
{
    private readonly LocalAccountOptions _options;
    public ConfiguredAccountCreationPolicy(LocalAccountOptions options)
    {
        options.Validate();
        _options = options with { CreatorAccountIds = options.CreatorAccountIds.ToArray(), CreatorRoles = options.CreatorRoles.ToArray() };
    }
    public bool CanCreate(AuthenticatedIdentity? actor)
    {
        if (_options.CreationMode == AccountCreationMode.Disabled) return false;
        if (_options.CreationMode == AccountCreationMode.Everyone) return true;
        if (actor is null || actor.Principal.Identity?.IsAuthenticated != true) return false;
        var id = actor.GetAccountIdentity();
        return id.Authority == _options.Authority && (_options.CreatorAccountIds.Contains(id.Subject, StringComparer.Ordinal)
            || _options.CreatorRoles.Any(actor.Principal.IsInRole));
    }
}
