using System.Net;
using Idb.Authentication;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task StaleExpiryHintDoesNotTurnAnUnrelatedDisconnectIntoARenewalLoop()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { TokenValidator = new ShortLivedValidator(_validator) });
        var value = Token("parent-a");
        var options = Options(host, value);
        var provider = new StaleHintTokens(value);
        await using var client = new TcpRepository<IRemotePerson>(options with { Connection = options.Connection with { TokenProvider = provider } });
        var subscription = client.SubscribeChanges(() => ValueTask.CompletedTask);
        try
        {
            await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            var before = provider.Calls;
            await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(before, provider.Calls);
        }
        finally { subscription.Dispose(); }
    }
    private sealed class StaleHintTokens(string value) : IAccessTokenProvider
    {
        public int Calls;
        public ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return ValueTask.FromResult(new AccessToken(value, DateTimeOffset.UtcNow.AddMinutes(-1))); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExpiryDoesNotLoopOnExpiredOrRejectedCredentials(bool revoked)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var old = Token("parent-a", additionalClaims: new() { ["exp"] = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds() });
        var provider = new SwitchingTokenProvider(old);
        var options = Options(host, old);
        await using var client = new TcpRepository<IRemotePerson>(options with { Connection = options.Connection with { TokenProvider = provider } });
        var subscription = client.SubscribeChanges(() => ValueTask.CompletedTask);
        try
        {
            await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            if (revoked) provider.Token = Token("parent-a", "scope");
            var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
            await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        }
        finally { subscription.Dispose(); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExpiredSubscriptionReauthenticatesWithoutEndingOrCrossingAccounts(bool switchAccount)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds();
        var oldToken = Token("parent-a", additionalClaims: new() { ["exp"] = expiry });
        var provider = new SwitchingTokenProvider(oldToken);
        var options = Options(host, oldToken);
        await using var client = new TcpRepository<IRemotePerson>(options with { Connection = options.Connection with { TokenProvider = provider } });
        var hints = 0;
        var subscription = client.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(hints == 1));
        provider.Token = Token(switchAccount ? "parent-b" : "parent-a");
        try
        {
            if (switchAccount)
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(8)));
                Assert.AreEqual(1, hints);
            }
            else
            {
                await Until(() => Task.FromResult(hints >= 2));
                Assert.IsFalse(subscription.Completion.IsCompleted);
                await Seed(client, "after-renewal", 7);
                await Until(() => Task.FromResult(hints >= 3));
                await subscription.DisposeAsync();
            }
        }
        finally { subscription.Dispose(); }
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
    }

    [TestMethod]
    public async Task BusyAdmissionUsesInjectedBackoffAndCancellationDoesNotSendAWrite()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        var policy = new WaitingAdmissionPolicy();
        await using var client = new TcpRepository<IRemotePerson>(options with { AdmissionRetryPolicy = policy });
        await client.GetCacheIdentityAsync();
        await using var held = await TcpEncodedEnvelopeStore.ConnectAsync(options.Connection);
        using var stop = new CancellationTokenSource();
        var write = client.SaveAsync(new RemotePerson { Name = "must-not-commit" }, stop.Token).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        Assert.AreEqual(1, policy.Attempts);
        await held.DisposeAsync();
        Assert.IsNull(await client.QueryFirstAsync(_ => true));
    }

    private sealed class WaitingAdmissionPolicy : ITcpAdmissionRetryPolicy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
        public TimeSpan GetDelay(int rejectedAttempts, TimeSpan initialDelay)
        { Attempts = rejectedAttempts; Entered.TrySetResult(); return TimeSpan.FromSeconds(20); }
    }

    [TestMethod]
    [DataRow(HttpRequestError.ConnectionError, true)]
    [DataRow(HttpRequestError.NameResolutionError, true)]
    [DataRow(HttpRequestError.SecureConnectionError, false)]
    [DataRow(HttpRequestError.InvalidResponse, false)]
    public async Task OnlyProvenTokenNetworkFailuresAllowOfflineFallback(HttpRequestError category, bool unavailable)
    {
        using var tokens = new ClientCredentialsAccessTokenProvider(new() {
            Authority = "https://identity.example", ClientId = "client", ClientSecretProvider = _ => ValueTask.FromResult("secret")
        }, new FailingHttp(category));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => tokens.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(unavailable, TcpRepositoryAvailability.IsUnavailable(error));
    }
    private sealed class FailingHttp(HttpRequestError category) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new HttpRequestException(category, "secret");
    }
}
