using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Implementation.Encryption;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class NativeAccountTransportTests
{
    private const string Password = "native-password-test-123";
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "InterfaceDB.NativeAccounts", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task AdministratorCreatesAccountsAndEveryStorageOperationUsesTheAccountPartition()
    {
        await using var fixture = await Fixture.Open(_root);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        var admin = fixture.Credentials("admin");
        var alice = await TcpAccountClient.CreateAsync(fixture.Client(host, "admin"), "alice", Password, admin);
        var bob = await TcpAccountClient.CreateAsync(fixture.Client(host, "admin"), "bob", Password, admin);
        Assert.AreNotEqual(alice.Subject, bob.Subject);
        await using var a = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "alice"));
        await using var b = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "bob"));
        Assert.AreEqual(alice, a.AccountIdentity);
        await a.PutAsync("same", [1, 2, 3]); await b.PutAsync("same", [9]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await a.GetAsync("same"));
        CollectionAssert.AreEqual(new byte[] { 9 }, await b.GetAsync("same"));
        await a.ApplyBatchAsync([new("a-only", [7]), new("same", [4])]);
        Assert.IsNull(await b.GetAsync("a-only"));
        var page = await b.EnumerateAsync(10); Assert.AreEqual(1, page.Records!.Length);
        CollectionAssert.AreEqual(new byte[] { 9 }, page.Records[0].Payload);
        var many = await b.ExecuteAsync(new() { Command = EncodedStoreCommand.GetMany, Keys = ["same", "a-only"] });
        Assert.AreEqual(1, many.Records!.Length);
        Assert.IsFalse(await b.DeleteAsync("a-only"));
        CollectionAssert.AreEqual(new byte[] { 7 }, await a.GetAsync("a-only"));
        var denied = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpAccountClient.CreateAsync(fixture.Client(host, "alice"), "rogue", Password, fixture.Credentials("alice")));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, denied.Code);
        Assert.AreEqual(3, await fixture.Accounts.CountAsync());
    }

    [TestMethod]
    public async Task EveryoneCanRegisterButCannotSmuggleRolesIntoTheRegistrationPayload()
    {
        await using var fixture = await Fixture.Open(_root, creation: AccountCreationMode.Everyone);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        var alice = await TcpAccountClient.CreateAsync(fixture.Client(host, "unused"), "alice", Password);
        var user = await fixture.Provider.AuthenticateAsync(await fixture.Credentials("alice").GetCredentialsAsync());
        Assert.AreEqual(alice, user.GetAccountIdentity()); Assert.IsFalse(user.Principal.IsInRole("account-admin"));
        var bad = JsonSerializer.SerializeToUtf8Bytes(new { Username = "elevated", Password, Roles = new[] { "account-admin" } });
        var reply = await RegistrationFrame(fixture, host, bad);
        Assert.AreEqual(RemoteErrorCode.InvalidRequest, reply.Error);
        Assert.IsNull(await fixture.Accounts.FindByUsernameAsync("elevated"));
    }

    [TestMethod]
    public async Task SingleAccountAllowsLoginButCannotRegisterASecondAccount()
    {
        await using var fixture = await Fixture.Open(_root, DatabaseAccountMode.SingleAccount, AccountCreationMode.Everyone);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with { DefaultAccessPolicy = null });
        await using var session = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "admin"));
        await session.PutAsync("value", [4]);
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpAccountClient.CreateAsync(fixture.Client(host, "admin"), "second", Password));
        Assert.AreEqual(RemoteErrorCode.InvalidRequest, error.Code);
        Assert.AreEqual(1, await fixture.Accounts.CountAsync());
    }

    [TestMethod]
    public async Task MultiAccountModeRequiresPolicyAndRejectsAnUnrestrictedCustomPolicyResult()
    {
        await using var fixture = await Fixture.Open(_root);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await TcpStoreHost.StartAsync(fixture.Host() with { DefaultAccessPolicy = null }));
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with
            { DefaultAccessPolicy = new DelegateDataAccessPolicy(_ => DataAccessScope.Unrestricted) });
        var denied = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "admin")));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, denied.Code);
    }

    [TestMethod]
    public async Task InvalidPasswordOrScopeCannotReachAnotherAccountOrDowngradeAuthentication()
    {
        await using var fixture = await Fixture.Open(_root);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        foreach (var connection in new[] {
            fixture.Client(host, "admin") with { CredentialsProvider = new UsernamePasswordCredentialsProvider("admin", _ => ValueTask.FromResult("wrong")) },
            fixture.Client(host, "admin") with { RequestedScope = "another-account" },
            fixture.Client(host, "admin") with { AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, CredentialsProvider = null,
                TokenProvider = new FixedAccessTokenProvider(new AccessToken("fake")) } })
        {
            var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.ConnectAsync(connection));
            Assert.IsTrue(error.Code is RemoteErrorCode.Unauthorized or RemoteErrorCode.AuthenticationRequired);
        }
        Assert.AreEqual(0, (await fixture.Data.EnumerateAsync(10)).Records!.Length);
    }

    [TestMethod]
    public async Task DisabledAccountIsDeniedOnAnAlreadyConnectedSession()
    {
        await using var fixture = await Fixture.Open(_root);
        var admin = await fixture.Provider.AuthenticateAsync(await fixture.Credentials("admin").GetCredentialsAsync());
        var alice = await fixture.Provider.CreateAsync("alice", Password, admin);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        await using var session = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "alice"));
        await session.PutAsync("before", [1]);
        await fixture.Provider.SetDisabledAsync(alice.Subject, true, admin);
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await session.GetAsync("before"));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
    }

    [TestMethod]
    public async Task TypedRepositoryUsesCommittedCopiesAndRejectsAccountSwitchEvenForASharedGroup()
    {
        await using var fixture = await Fixture.Open(_root);
        var admin = await fixture.Provider.AuthenticateAsync(await fixture.Credentials("admin").GetCredentialsAsync());
        foreach (var name in new[] { "alice", "bob" })
            await fixture.Provider.CreateManagedAccountAsync(name, Password, ["database-user"], new() { ["groups"] = ["family"] }, admin);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with { DefaultAccessPolicy = new ClaimDataAccessPolicy("groups") });
        var switching = new SwitchingCredentials(fixture.Credentials("alice"));
        await using var repository = new TcpRepository<IRemotePerson>(new()
        {
            Connection = fixture.Client(host, "alice") with { CredentialsProvider = switching },
            Contract = new TcpRepositoryTests.PersonConfig(), RuntimeTypes = new(typeof(RemotePerson))
        });
        var entity = await repository.SaveAsync(new RemotePerson { Name = "original", Score = 1 });
        var cache = await repository.GetCacheIdentityAsync();
        entity.Name = "unsaved";
        Assert.AreEqual("original", (await repository.QuerySingleAsync(_ => true)).Name);
        switching.Inner = fixture.Credentials("bob");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await repository.SaveAsync(new RemotePerson { Name = "must-not-write" }));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await repository.GetCacheIdentityAsync());
        await using var bob = new TcpRepository<IRemotePerson>(new() { Connection = fixture.Client(host, "bob"),
            Contract = new TcpRepositoryTests.PersonConfig(), RuntimeTypes = new(typeof(RemotePerson)) });
        Assert.AreEqual("original", (await bob.QuerySingleAsync(_ => true)).Name);
        Assert.AreNotEqual(cache, await bob.GetCacheIdentityAsync());
    }

    [TestMethod]
    public async Task CredentialCatalogueIsEncryptedAndReopensWithTheSameIdentity()
    {
        var key = RandomNumberGenerator.GetBytes(32); AccountIdentity id;
        await using (var fixture = await Fixture.Open(_root, key: key))
        {
            id = (await fixture.Provider.AuthenticateAsync(await fixture.Credentials("admin").GetCredentialsAsync())).GetAccountIdentity();
            var raw = await fixture.Backing.GetAsync("idb-accounts-v1"); Assert.IsNotNull(raw);
            Assert.IsFalse(Encoding.UTF8.GetString(raw).Contains(Password, StringComparison.Ordinal));
            Assert.IsFalse(Encoding.UTF8.GetString(raw).Contains("admin", StringComparison.Ordinal));
            Assert.IsFalse(Encoding.UTF8.GetString(raw).Contains("idb-pbkdf2", StringComparison.Ordinal));
        }
        await using (var reopened = await Fixture.Open(_root, key: key))
            Assert.AreEqual(id, (await reopened.Provider.AuthenticateAsync(await reopened.Credentials("ADMIN").GetCredentialsAsync())).GetAccountIdentity());
        await using var backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "credentials"));
        await Assert.ThrowsAsync<CryptographicException>(async () => await EncryptedDatabaseAccountStore.OpenAsync(backing,
            new AesGcmEncryptor(new Key(RandomNumberGenerator.GetBytes(32))), "idb-local:transport-test"));
        await Assert.ThrowsAsync<CryptographicException>(async () => await EncryptedDatabaseAccountStore.OpenAsync(backing,
            new AesGcmEncryptor(new Key(key)), "idb-local:wrong-authority"));
        var cipher = (await backing.GetAsync("idb-accounts-v1"))!; cipher[^1] ^= 1;
        await backing.PutAsync("idb-accounts-v1", cipher);
        await Assert.ThrowsAsync<CryptographicException>(async () => await EncryptedDatabaseAccountStore.OpenAsync(backing,
            new AesGcmEncryptor(new Key(key)), "idb-local:transport-test"));
    }

    [TestMethod]
    public async Task ConcurrentRegistrationCannotBypassTheSingleAccountLimit()
    {
        await using var backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "credentials"));
        await using var accounts = await EncryptedDatabaseAccountStore.OpenAsync(backing, new AesGcmEncryptor(new Key(RandomNumberGenerator.GetBytes(32))), "idb-local:race");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => accounts.TryCreateAsync(new(Guid.NewGuid().ToString("N"), "user" + i,
            "test-hash", [], new()), 1, false).AsTask()));
        Assert.AreEqual(1, results.Count(x => x)); Assert.AreEqual(1, await accounts.CountAsync());
    }

    [TestMethod]
    public async Task UnknownCredentialWriteOutcomeFaultsTheStoreUntilControlledReopen()
    {
        await using var backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "credentials"));
        var encryptor = new AesGcmEncryptor(new Key(RandomNumberGenerator.GetBytes(32)));
        await using (var accounts = await EncryptedDatabaseAccountStore.OpenAsync(new FailAfterCommit(backing), encryptor, "idb-local:fault"))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () => await accounts.TryCreateAsync(new(Guid.NewGuid().ToString("N"), "alice", "hash", [], new()), 10, false));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await accounts.FindByUsernameAsync("alice"));
        }
        await using var reopened = await EncryptedDatabaseAccountStore.OpenAsync(backing, encryptor, "idb-local:fault");
        Assert.AreEqual(1, await reopened.CountAsync());
    }

    [TestMethod]
    public async Task RejectedServerCertificateStopsBeforeRequestingThePassword()
    {
        await using var fixture = await Fixture.Open(_root);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        var calls = 0;
        var provider = new UsernamePasswordCredentialsProvider("admin", _ => { calls++; return ValueTask.FromResult(Password); });
        await Assert.ThrowsExactlyAsync<AuthenticationException>(async () => await TcpEncodedEnvelopeStore.ConnectAsync(
            fixture.Client(host, "admin") with { CredentialsProvider = provider, ServerCertificateValidation = (_, _, _, _) => false }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task DisabledRegistrationRejectsBothAnonymousAndAdministratorRequests()
    {
        await using var fixture = await Fixture.Open(_root, creation: AccountCreationMode.Disabled);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host());
        foreach (var creator in new IClientCredentialsProvider?[] { null, fixture.Credentials("admin") })
        {
            var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
                await TcpAccountClient.CreateAsync(fixture.Client(host, "admin"), "denied", Password, creator));
            Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
        }
        Assert.AreEqual(1, await fixture.Accounts.CountAsync());
    }

    [TestMethod]
    public async Task LostRegistrationAcknowledgementDoesNotClaimTheAccountWasRejected()
    {
        await using var fixture = await Fixture.Open(_root, creation: AccountCreationMode.Everyone);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with { AccountManager = new LostRegistrationReply(fixture.Provider) });
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await TcpAccountClient.CreateAsync(fixture.Client(host, "unused"), "alice", Password));
        Assert.AreEqual(RemoteErrorCode.OutcomeUnknown, error.Code);
        await using var alice = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "alice"));
        Assert.IsNotNull(alice.AccountIdentity);
        Assert.AreEqual(2, await fixture.Accounts.CountAsync());
    }

    [TestMethod]
    public async Task CredentialRevalidationFailureDeniesAnExistingSession()
    {
        await using var fixture = await Fixture.Open(_root);
        var provider = new FaultingSessionProvider(fixture.Provider);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with { AuthenticationProvider = provider });
        await using var session = await TcpEncodedEnvelopeStore.ConnectAsync(fixture.Client(host, "admin"));
        await session.PutAsync("before", [1]);
        provider.Fault = true;
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await session.GetAsync("before"));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
    }

    [TestMethod]
    public async Task NativeSubscriptionsAreScopedAndStopDeliveringAfterAccountRevocation()
    {
        await using var fixture = await Fixture.Open(_root);
        var admin = await fixture.Provider.AuthenticateAsync(await fixture.Credentials("admin").GetCredentialsAsync());
        var bob = await fixture.Provider.CreateManagedAccountAsync("bob", Password, ["database-user"], new() { ["groups"] = ["family"] }, admin);
        await fixture.Provider.CreateManagedAccountAsync("alice", Password, ["database-user"], new() { ["groups"] = ["family"] }, admin);
        await fixture.Provider.CreateManagedAccountAsync("other", Password, ["database-user"], new() { ["groups"] = ["another"] }, admin);
        await using var host = await TcpStoreHost.StartAsync(fixture.Host() with { DefaultAccessPolicy = new ClaimDataAccessPolicy("groups") });
        TcpRepository<IRemotePerson> Client(string name) => new(new() { Connection = fixture.Client(host, name),
            Contract = new TcpRepositoryTests.PersonConfig(), RuntimeTypes = new(typeof(RemotePerson)) });
        await using var aliceClient = Client("alice"); await using var bobClient = Client("bob"); await using var otherClient = Client("other");
        var bobInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int bobCount = 0, otherCount = 0;
        using var bobSub = bobClient.SubscribeChanges(() =>
        { if (Interlocked.Increment(ref bobCount) == 1) bobInitial.TrySetResult(); else changed.TrySetResult(); return ValueTask.CompletedTask; });
        await using var otherSub = otherClient.SubscribeChanges(() =>
        { Interlocked.Increment(ref otherCount); otherInitial.TrySetResult(); return ValueTask.CompletedTask; });
        await Task.WhenAll(bobInitial.Task, otherInitial.Task).WaitAsync(TimeSpan.FromSeconds(15));
        await aliceClient.SaveAsync(new RemotePerson { Name = "first", Score = 1 });
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, Volatile.Read(ref otherCount));
        await fixture.Provider.SetDisabledAsync(bob.Subject, true, admin);
        var before = Volatile.Read(ref bobCount);
        await aliceClient.SaveAsync(new RemotePerson { Name = "after-revocation", Score = 2 });
        await Assert.ThrowsAsync<IOException>(() => bobSub.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.AreEqual(before, Volatile.Read(ref bobCount));
        Assert.AreEqual(1, Volatile.Read(ref otherCount));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CredentialStorageRejectsNoOpAndContextInsensitiveEncryption(bool plaintext)
    {
        await using var backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "credentials"));
        await Assert.ThrowsAsync<CryptographicException>(async () => await EncryptedDatabaseAccountStore.OpenAsync(
            backing, new InvalidCredentialEncryptor(plaintext), "idb-local:encryption-test"));
        Assert.IsNull(await backing.GetAsync("idb-accounts-v1"));
    }

    [TestMethod]
    public async Task CatalogueCapacityRejectionDoesNotWriteOrFaultTheAccountStore()
    {
        await using var backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "credentials"));
        await using var accounts = await EncryptedDatabaseAccountStore.OpenAsync(backing,
            new AesGcmEncryptor(new Key(RandomNumberGenerator.GetBytes(32))), "idb-local:bounded", 4096);
        var oversized = new DatabaseAccount(Guid.NewGuid().ToString("N"), "alice", "test-hash", [],
            new() { ["groups"] = Enumerable.Repeat(new string('a', 256), 20).ToArray() });
        await Assert.ThrowsExactlyAsync<AccountStoreCapacityException>(async () => await accounts.TryCreateAsync(oversized, 10, false));
        Assert.AreEqual(0, await accounts.CountAsync());
        Assert.IsNull(await backing.GetAsync("idb-accounts-v1"));
        Assert.IsTrue(await accounts.TryCreateAsync(oversized with { Claims = new() }, 10, false));
        Assert.AreEqual(1, await accounts.CountAsync());
    }

    private sealed class InvalidCredentialEncryptor(bool plaintext) : IContextualEncryptor
    {
        private readonly AesGcmEncryptor _inner = new(new Key(RandomNumberGenerator.GetBytes(32)));
        public byte[] Encrypt(ReadOnlySpan<byte> plain) => plaintext ? plain.ToArray() : _inner.Encrypt(plain);
        public byte[] Decrypt(ReadOnlySpan<byte> cipher) => plaintext ? cipher.ToArray() : _inner.Decrypt(cipher);
        public byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associatedData) => Encrypt(plain);
        public byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData) => Decrypt(cipher);
        public EncryptionDecryptionResult DecryptWithMetadata(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData)
            => new(Decrypt(cipher), null, false, true);
    }

    private sealed class LostRegistrationReply(IDatabaseAccountManager inner) : IDatabaseAccountManager
    {
        public async ValueTask<AccountIdentity> CreateAsync(string username, string password, AuthenticatedIdentity? actor, CancellationToken cancellationToken = default)
        { await inner.CreateAsync(username, password, actor, cancellationToken); throw new IOException("Injected lost acknowledgement."); }
    }
    private sealed class FaultingSessionProvider(IAuthenticationProvider inner) : IAuthenticationProvider
    {
        internal bool Fault;
        public bool RequiresDataIsolation => inner.RequiresDataIsolation;
        public ValueTask<AuthenticatedIdentity> AuthenticateAsync(AuthenticationCredentials credentials, CancellationToken cancellationToken = default)
            => inner.AuthenticateAsync(credentials, cancellationToken);
        public ValueTask<bool> IsSessionValidAsync(AuthenticatedIdentity identity, CancellationToken cancellationToken = default)
            => Fault ? throw new IOException("Injected credential store failure.") : inner.IsSessionValidAsync(identity, cancellationToken);
    }

    private static async Task<WireMessage> RegistrationFrame(Fixture fixture, TcpStoreHost host, byte[] payload)
    {
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, host.LocalEndPoint.Port);
        using var tls = new SslStream(tcp.GetStream(), false, fixture.Certificates.ValidateServer);
        await tls.AuthenticateAsClientAsync("localhost");
        await MessagePackFrameCodec.WriteAsync(tls, new() { Kind = WireMessageKind.CreateAccount, CorrelationId = 1,
            AuthenticationMode = TcpStoreAuthenticationMode.Provider, Request = new() { Command = EncodedStoreCommand.Put, Key = "create", Payload = payload } }, new());
        return (await MessagePackFrameCodec.ReadAsync(tls, new()))!;
    }
    private sealed class SwitchingCredentials(IClientCredentialsProvider inner) : IClientCredentialsProvider
    {
        internal IClientCredentialsProvider Inner = inner;
        public ValueTask<AuthenticationCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => Inner.GetCredentialsAsync(cancellationToken);
    }
    private sealed class Key(byte[] key) : IKeyProvider { public ReadOnlyMemory<byte> GetCurrentKey() => key; }
    private sealed class FailAfterCommit(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        { var result = await inner.ExecuteAsync(request, cancellationToken); if (request.IsMutation) throw new IOException("Injected lost acknowledgement."); return result; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TestCertificates Certificates = new();
        internal IEncodedEnvelopeStore Backing = null!, Data = null!;
        internal EncryptedDatabaseAccountStore Accounts = null!;
        internal LocalAccountAuthenticationProvider Provider = null!;
        internal static async Task<Fixture> Open(string root, DatabaseAccountMode mode = DatabaseAccountMode.MultipleAccounts,
            AccountCreationMode creation = AccountCreationMode.Administrators, byte[]? key = null)
        {
            var f = new Fixture();
            f.Backing = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(root, "credentials"));
            f.Data = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(root, "data"));
            f.Accounts = await EncryptedDatabaseAccountStore.OpenAsync(f.Backing, new AesGcmEncryptor(new Key(key ?? RandomNumberGenerator.GetBytes(32))), "idb-local:transport-test");
            var options = new LocalAccountOptions { Authority = "idb-local:transport-test", AccountMode = mode, CreationMode = creation, AttemptsPerMinute = 1000 };
            f.Provider = new(f.Accounts, new Pbkdf2PasswordHasher(), new ConfiguredAccountCreationPolicy(options), options);
            if (await f.Accounts.CountAsync() == 0) await f.Provider.ProvisionFirstAccountAsync("admin", Password);
            return f;
        }
        internal IClientCredentialsProvider Credentials(string username) => new UsernamePasswordCredentialsProvider(username, _ => ValueTask.FromResult(Password));
        internal TcpStoreHostOptions Host() => new()
        {
            ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = Certificates.Server,
            AuthenticationMode = TcpStoreAuthenticationMode.Provider, AuthenticationProvider = Provider, AccountManager = Provider,
            DefaultAccessPolicy = new SubjectDataAccessPolicy(), AuthorizeIdentity = (i, alias, _) => alias == "people" && i.Principal.IsInRole("database-user"),
            Stores = [new("people", TcpRepositoryTests.Manifest(), Data)]
        };
        internal TcpStoreClientOptions Client(TcpStoreHost host, string username) => new()
        {
            Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "people",
            Manifest = TcpRepositoryTests.Manifest(), AuthenticationMode = TcpStoreAuthenticationMode.Provider,
            CredentialsProvider = Credentials(username), ServerCertificateValidation = Certificates.ValidateServer
        };
        public async ValueTask DisposeAsync()
        { Provider.Dispose(); await Accounts.DisposeAsync(); await Data.DisposeAsync(); await Backing.DisposeAsync(); Certificates.Dispose(); }
    }
}
