using System.Net;
using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow("network")]
    [DataRow("timeout")]
    [DataRow("tls")]
    public async Task CompositionTokenFailureRecoversOnlyForProvenTemporaryOutages(string kind)
    {
        using var failureSource = new ClientCredentialsAccessTokenProvider(new() {
            Authority = "https://identity.example", ClientId = "client", ClientSecretProvider = _ => ValueTask.FromResult("secret"),
            RequestTimeout = TimeSpan.FromMilliseconds(50)
        }, new FailingIdentityHandler(kind));
        var failure = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => failureSource.GetAccessTokenAsync().AsTask());
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var port = host.LocalEndPoint.Port;
        var tokens = new InterruptibleTokens(Token("parent-a"));
        var options = Options(host, tokens.Value);
        await using var remote = new TcpRepository<IRemotePerson>(options with { Connection = options.Connection with { TokenProvider = tokens } });
        await Seed(remote, "before", 1);
        await using var cache = Cache("token-recovery");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.RemoteFirst, background: true);
        await repository.InitializeAsync();
        await Until(() => Task.FromResult(repository.Connection.State == RepositoryConnectionState.Online));
        tokens.Failure = failure;
        await host.DisposeAsync();
        await Until(() => Task.FromResult(tokens.Failures > 0));
        await Until(() => Task.FromResult(repository.Connection.State == (kind == "tls" ? RepositoryConnectionState.Faulted : RepositoryConnectionState.Offline)));
        if (kind == "tls")
        {
            await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(async () => await repository.QueryFirstAsync(_ => true));
            return;
        }
        // Reconnection can retry, but an unavailable identity provider does not bypass the
        // existing account-bound cache access check. Reads resume after identity is available.
        await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(async () => await repository.QueryFirstAsync(_ => true));
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(IPAddress.Loopback, port) });
        tokens.Failure = null;
        await Until(() => Task.FromResult(repository.Connection.State == RepositoryConnectionState.Online));
        await using var writer = Client(restarted, "parent-a");
        await Seed(writer, "after", 2);
        await Until(async () => (await Local(cache).QueryFirstAsync(person => person.Name == "after"))?.Score == 2);
    }

    [TestMethod]
    public async Task VersionAuthorityExpiryRequestsImmediateRediscovery()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var token = Token("parent-a", additionalClaims: new() { ["exp"] = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds() });
        var authority = new TcpRepositoryVersionAuthority(Options(host, token).Connection);
        await authority.ObserveAsync(Manifest()).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        // The old fixed token is still rejected; normal Observe completion grants no access.
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => authority.DiscoverAsync().AsTask());
    }

    private sealed class InterruptibleTokens(string value) : IAccessTokenProvider
    {
        public string Value => value;
        public volatile TokenAcquisitionException? Failure;
        public int Failures;
        public ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } error) { Interlocked.Increment(ref Failures); throw error; }
            return ValueTask.FromResult(new AccessToken(value));
        }
    }
    private sealed class FailingIdentityHandler(string kind) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (kind == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new HttpRequestException(kind == "tls" ? HttpRequestError.SecureConnectionError : HttpRequestError.NameResolutionError, "redacted");
        }
    }
}
