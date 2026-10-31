using System.Text.Json;
using Idb.Authentication;

namespace InterfaceDB.Authentication.Tests;

[TestClass]
public sealed class LocalAccountAuthenticationTests
{
    [TestMethod]
    public async Task PasswordAdmissionWaitsBoundedlyAndCancelledWaitDoesNotConsumeWorkBudget()
    {
        await using var store = new MemoryAccounts();
        var options = Options(AccountCreationMode.Everyone) with { MaximumConcurrentPasswordChecks = 1, AttemptsPerMinute = 3 };
        using var hasher = new GatedHasher();
        using var provider = new LocalAccountAuthenticationProvider(store, hasher, new ConfiguredAccountCreationPolicy(options), options);
        await provider.CreateAsync("alice", Password, null);
        var first = Task.Run(async () => await provider.AuthenticateAsync(Login("alice")));
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.IsTrue(hasher.Entered.Wait(TimeSpan.FromSeconds(10)));
            var queued = provider.AuthenticateAsync(Login("alice"), cancellation.Token).AsTask();
            Assert.IsFalse(queued.IsCompleted, "A busy password worker must not become an invalid-credentials rejection.");
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        }
        finally { hasher.Release.Set(); await first; }
        Assert.IsNotNull(await provider.AuthenticateAsync(Login("alice")));
    }

    private sealed class GatedHasher : IPasswordHasher, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public string Hash(string password) => password;
        public bool Verify(string password, string encodedHash)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return password == encodedHash;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)]
    public async Task PasswordAdmissionOverloadAndDeadlineReturnBusyWithoutConsumingAttempts(int queueCapacity)
    {
        await using var store = new MemoryAccounts();
        var options = Options(AccountCreationMode.Everyone) with
        {
            MaximumConcurrentPasswordChecks = 1, MaximumQueuedPasswordChecks = queueCapacity,
            PasswordAdmissionTimeout = TimeSpan.FromMilliseconds(100), AttemptsPerMinute = 3
        };
        using var hasher = new GatedHasher();
        using var provider = new LocalAccountAuthenticationProvider(store, hasher, new ConfiguredAccountCreationPolicy(options), options);
        await provider.CreateAsync("alice", Password, null);
        var first = Task.Run(async () => await provider.AuthenticateAsync(Login("alice")));
        try
        {
            Assert.IsTrue(hasher.Entered.Wait(TimeSpan.FromSeconds(10)));
            var waiting = provider.AuthenticateAsync(Login("alice")).AsTask();
            await Assert.ThrowsExactlyAsync<AuthenticationBusyException>(() => provider.AuthenticateAsync(Login("alice")).AsTask());
            await Assert.ThrowsExactlyAsync<AuthenticationBusyException>(() => waiting);
        }
        finally { hasher.Release.Set(); await first; }
        Assert.IsNotNull(await provider.AuthenticateAsync(Login("alice")));
    }

    private const string Password = "a-long-test-password-123";
    private static LocalAccountOptions Options(AccountCreationMode creation = AccountCreationMode.Administrators,
        DatabaseAccountMode mode = DatabaseAccountMode.MultipleAccounts) => new()
        { Authority = "idb-local:test", AccountMode = mode, CreationMode = creation, AttemptsPerMinute = 1000 };
    private static AuthenticationCredentials Login(string name, string password = Password)
        => new("password", JsonSerializer.Serialize(new PasswordLogin(name, password)));
    private static LocalAccountAuthenticationProvider Provider(MemoryAccounts store, LocalAccountOptions? options = null, TimeProvider? clock = null)
    {
        options ??= Options();
        return new(store, new Pbkdf2PasswordHasher(), new ConfiguredAccountCreationPolicy(options), options, clock);
    }

    [TestMethod]
    public void PasswordHashesAreSaltedBoundedAndDoNotAcceptWrongPasswords()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var first = hasher.Hash(Password); var second = hasher.Hash(Password);
        Assert.AreNotEqual(first, second);
        Assert.IsFalse(first.Contains(Password, StringComparison.Ordinal));
        Assert.IsTrue(hasher.Verify(Password, first));
        Assert.IsFalse(hasher.Verify("wrong", first));
        Assert.IsFalse(hasher.Verify(Password, first.Replace("600000", "2147483647", StringComparison.Ordinal)));
        Assert.IsFalse(hasher.Verify(Password, "malformed"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Pbkdf2PasswordHasher(1));
    }

    [TestMethod]
    public async Task BootstrapIsOneTimeAndRegistrationCannotGrantAdministrativePrivileges()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store);
        var admin = await provider.ProvisionFirstAccountAsync("Admin", Password);
        var actor = await provider.AuthenticateAsync(Login("ADMIN"));
        Assert.AreEqual(admin, actor.GetAccountIdentity());
        Assert.IsTrue(actor.Principal.IsInRole("account-admin"));
        await Assert.ThrowsExactlyAsync<AccountCreationRejectedException>(async () => await provider.ProvisionFirstAccountAsync("second-admin", Password));
        var alice = await provider.CreateAsync("alice", Password, actor);
        var user = await provider.AuthenticateAsync(Login("alice"));
        Assert.AreEqual(alice, user.GetAccountIdentity());
        Assert.IsTrue(user.Principal.IsInRole("database-user"));
        Assert.IsFalse(user.Principal.IsInRole("account-admin"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.CreateAsync("bob", Password, user));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.CreateAsync("bob", Password, null));
        Assert.AreEqual(2, await store.CountAsync());
    }

    [TestMethod]
    public async Task EveryoneRegistrationStillCannotAssignClaimsOrRoles()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store, Options(AccountCreationMode.Everyone));
        var id = await provider.CreateAsync("alice", Password, null);
        var user = await provider.AuthenticateAsync(Login("alice"));
        Assert.AreEqual(id.Subject, user.GetAccountIdentity().Subject);
        Assert.IsFalse(user.Principal.IsInRole("account-admin"));
        Assert.IsFalse(user.Principal.HasClaim(c => c.Type == "tenant_id"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
            await provider.CreateManagedAccountAsync("hacker", Password, ["account-admin"], new(), user));
    }

    [TestMethod]
    public async Task SingleAccountRejectsSecondLoginAndCaseVariants()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store, Options(AccountCreationMode.Everyone, DatabaseAccountMode.SingleAccount));
        await provider.ProvisionFirstAccountAsync("alice", Password);
        await Assert.ThrowsExactlyAsync<AccountCreationRejectedException>(async () => await provider.CreateAsync("ALICE", Password, null));
        await Assert.ThrowsExactlyAsync<AccountCreationRejectedException>(async () => await provider.CreateAsync("bob", Password, null));
        Assert.AreEqual(1, await store.CountAsync());
    }

    [TestMethod]
    public async Task SwitchingAnExistingMultiAccountStoreToSingleFailsConfiguration()
    {
        await using var store = new MemoryAccounts();
        using (var multiple = Provider(store, Options(AccountCreationMode.Everyone)))
        { await multiple.CreateAsync("a", Password, null); await multiple.CreateAsync("b", Password, null); }
        using var single = Provider(store, Options(mode: DatabaseAccountMode.SingleAccount));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await single.ValidateConfigurationAsync());
    }

    [TestMethod]
    [DataRow("missing", "wrong")]
    [DataRow("admin", "wrong")]
    public async Task InvalidCredentialsHaveTheSameFailure(string name, string password)
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store);
        await provider.ProvisionFirstAccountAsync("admin", Password);
        var error = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.AuthenticateAsync(Login(name, password)));
        Assert.AreEqual("Authentication or account authorization failed.", error.Message);
        Assert.IsFalse(error.ToString().Contains(Password, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PasswordChangePreservesAccountIdentityAndRevokesOldSessions()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store);
        var id = await provider.ProvisionFirstAccountAsync("alice", Password);
        var before = await provider.AuthenticateAsync(Login("alice"));
        await provider.ChangePasswordAsync(Password, "a-new-long-test-password", before);
        Assert.IsFalse(await provider.IsSessionValidAsync(before));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.AuthenticateAsync(Login("alice")));
        var after = await provider.AuthenticateAsync(Login("alice", "a-new-long-test-password"));
        Assert.AreEqual(id, after.GetAccountIdentity()); Assert.IsTrue(await provider.IsSessionValidAsync(after));
    }

    [TestMethod]
    public async Task DisabledAccountCannotLoginOrUseItsExistingIdentity()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store);
        await provider.ProvisionFirstAccountAsync("admin", Password);
        var admin = await provider.AuthenticateAsync(Login("admin"));
        var alice = await provider.CreateAsync("alice", Password, admin);
        var identity = await provider.AuthenticateAsync(Login("alice"));
        await provider.SetDisabledAsync(alice.Subject, true, admin);
        Assert.IsFalse(await provider.IsSessionValidAsync(identity));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.AuthenticateAsync(Login("alice")));
    }

    [TestMethod]
    public async Task ExpiredSessionsCannotCreateAccounts()
    {
        var clock = new Clock(); await using var store = new MemoryAccounts(); using var provider = Provider(store, clock: clock);
        await provider.ProvisionFirstAccountAsync("admin", Password);
        var admin = await provider.AuthenticateAsync(Login("admin"));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.IsFalse(await provider.IsSessionValidAsync(admin));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await provider.CreateAsync("alice", Password, admin));
    }

    [TestMethod]
    public async Task AttemptsAreBoundedAndCancellationDoesNotCreateAnAccount()
    {
        await using var store = new MemoryAccounts(); using var provider = Provider(store, Options(AccountCreationMode.Everyone) with { AttemptsPerMinute = 1 });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await provider.CreateAsync("alice", Password, null, cancellation.Token));
        Assert.AreEqual(0, await store.CountAsync());
        await provider.CreateAsync("alice", Password, null);
        await Assert.ThrowsExactlyAsync<AuthenticationBusyException>(async () => await provider.AuthenticateAsync(Login("alice")));
    }

    [TestMethod]
    public void SelfRegistrationCannotDefaultToCreatorRoles()
        => Assert.ThrowsExactly<ArgumentException>(() => (Options() with { NewAccountRoles = ["account-admin"] }).Validate());

    [TestMethod]
    public async Task AccountIdAllowlistCanAuthorizeCreationWithoutAnAdminRole()
    {
        await using var store = new MemoryAccounts(); string creator;
        using (var setup = Provider(store, Options(AccountCreationMode.Everyone)))
            creator = (await setup.CreateAsync("creator", Password, null)).Subject;
        using var provider = Provider(store, Options() with { CreatorRoles = [], CreatorAccountIds = [creator] });
        var actor = await provider.AuthenticateAsync(Login("creator"));
        await provider.CreateAsync("child", Password, actor);
        Assert.AreEqual(2, await store.CountAsync());
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryAccounts : IDatabaseAccountStore
    {
        private readonly Dictionary<string, DatabaseAccount> _accounts = new();
        private readonly object _gate = new();
        public ValueTask<int> CountAsync(CancellationToken cancellationToken = default) { lock (_gate) return ValueTask.FromResult(_accounts.Count); }
        public ValueTask<DatabaseAccount?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
        { lock (_gate) return ValueTask.FromResult(_accounts.GetValueOrDefault(id)); }
        public ValueTask<DatabaseAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        { lock (_gate) return ValueTask.FromResult(_accounts.Values.SingleOrDefault(a => a.Username == username)); }
        public ValueTask<bool> TryCreateAsync(DatabaseAccount account, int maximumAccounts, bool onlyIfEmpty, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_accounts.Count >= maximumAccounts || (onlyIfEmpty && _accounts.Count != 0) || _accounts.Values.Any(a => a.Username == account.Username)) return ValueTask.FromResult(false);
                _accounts.Add(account.Id, account); return ValueTask.FromResult(true);
            }
        }
        public ValueTask<bool> TryUpdateAsync(DatabaseAccount account, long expectedSecurityVersion, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_accounts.TryGetValue(account.Id, out var old) || old.SecurityVersion != expectedSecurityVersion) return ValueTask.FromResult(false);
                _accounts[account.Id] = account; return ValueTask.FromResult(true);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
