using System.Net;
using Idb.Commercial.Repositories;
using Idb.Commercial.Transport.Atlas;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.AtlasCore;
using Idb.Storage.SegmentCore;
using Idb.Transport;
using Idb.Transport.Repositories;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private static string InterfaceName<T>() => typeof(T).FullName!;
    private static CompatibilityManifest InterfaceManifest<T>() => ManagedSetupProtocol.ClientManifest(Manifest() with
    {
        ContractIdentity = InterfaceName<T>(), ContractSchemaHash = InterfaceName<T>() + "-v1",
        ImplementationManifestHash = InterfaceName<T>() + "-types-v1"
    });

    [TestMethod]
    [DataRow("file")]
    [DataRow("segment-core")]
    [DataRow("atlas")]
    public async Task InterfaceScopes_SameSetupNameKeepsManagedSettingsDataAndCachesIndependent(string providerId)
    {
        var port = FreeSetupPort();
        await using var peopleStore = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server-people"));
        await using var storyStore = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server-stories"));
        var peopleSetup = new ManagedClientSetup("mobile", providerId, RepositoryReadPreference.LocalFirst,
            InterfaceManifest<IRemotePerson>(), "people-data", "people-policy-1");
        var storySetup = new ManagedClientSetup("mobile", providerId, RepositoryReadPreference.RemoteFirst,
            InterfaceManifest<IRemoteStory>(), "story-data", "story-policy-2");
        var registry = new ManagedSetupRegistry([new(peopleSetup, peopleStore, _ => true), new(storySetup, storyStore, _ => true)],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1));
        await using var host = await TcpStoreHost.StartAsync(registry.Configure(HostOptions(peopleStore) with
        { Stores = [], AuthorizeIdentity = null, ListenEndPoint = new(IPAddress.Loopback, port), MaximumConnections = 32 }));
        var peopleOptions = new TcpRepositoryOptions<IRemotePerson>
        {
            Connection = SetupConnection(port) with { Manifest = peopleSetup.Manifest, StoreAlias = peopleSetup.RemoteAlias },
            Contract = new InterfaceScopeConfig<IRemotePerson>(null, ContractRepositoryType.Remote), RuntimeTypes = new(typeof(RemotePerson))
        };
        var storyOptions = new TcpRepositoryOptions<IRemoteStory>
        {
            Connection = SetupConnection(port) with { Manifest = storySetup.Manifest, StoreAlias = storySetup.RemoteAlias },
            Contract = new InterfaceScopeConfig<IRemoteStory>(null, ContractRepositoryType.Remote), RuntimeTypes = new(typeof(RemoteStory))
        };
        var cache = Path.Combine(_root, "shared-account-cache-parent");
        TcpManagedSetupAuthority ScopedAuthority<T>() => Authority(port, contract: InterfaceName<T>(),
            receipts: new FileManagedSetupReceiptStore(Path.Combine(_root, InterfaceName<T>() + "-approval.json")));
        LocalRemoteRepository<IRemotePerson> People() => LocalRemoteRepositoryFactory.CreateFromServer(ScopedAuthority<IRemotePerson>(),
            [ManagedRepositoryModel<IRemotePerson>.FromTcp(peopleOptions)], Providers(), cache, "parent-a", new PersonMapper(),
            retryDelay: TimeSpan.FromMilliseconds(50));
        LocalRemoteRepository<IRemoteStory> Stories() => LocalRemoteRepositoryFactory.CreateFromServer(ScopedAuthority<IRemoteStory>(),
            [ManagedRepositoryModel<IRemoteStory>.FromTcp(storyOptions)],
            [new FileManagedProvider<IRemoteStory>(), new SegmentCoreManagedProvider<IRemoteStory>(), new AtlasManagedProvider<IRemoteStory>()],
            cache, "parent-a", new StoryMapper(), retryDelay: TimeSpan.FromMilliseconds(50));
        await using var remotePeople = new TcpRepository<IRemotePerson>(peopleOptions);
        await using var remoteStories = new TcpRepository<IRemoteStory>(storyOptions);
        await using (var people = People())
        await using (var stories = Stories())
        {
            await people.InitializeAsync(); await stories.InitializeAsync();
            await Until(() => Task.FromResult(people.Connection.State == RepositoryConnectionState.Online
                && stories.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(RepositoryReadPreference.LocalFirst, people.EffectiveReadPreference);
            Assert.AreEqual(RepositoryReadPreference.RemoteFirst, stories.EffectiveReadPreference);
            // The same record key must be valid in two independent interface datasets.
            await people.SaveAsync(new RemotePerson { Name = "same-key", Score = 11 });
            await stories.SaveAsync(new RemoteStory { Title = "same-key", Pages = 22 });
            await Until(async () => (await remotePeople.QueryFirstAsync(_ => true))?.Score == 11);
            await Until(async () => (await stories.QueryFirstAsync(_ => true))?.Pages == 22);
            Assert.AreEqual(22, (await remoteStories.QuerySingleAsync(_ => true)).Pages);
            Assert.AreEqual(2, Directory.GetDirectories(cache).Length, "Each interface needs an independent cache under the common parent.");

            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = stories.SubscribeChanges(async () =>
            {
                if ((await stories.QueryFirstAsync(_ => true))?.Pages == 23) refreshed.TrySetResult();
            });
            await subscription.Ready;
            await people.DisposeAsync(); // Another interface's connections/subscriptions must survive this disposal.
            var story = await remoteStories.QuerySingleAsync(_ => true); story.Pages = 23; await remoteStories.SaveAsync(story);
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(11, (await remotePeople.QuerySingleAsync(_ => true)).Score);
        }
        // Restart offline: both receipts and local caches must still select the correct interface.
        await host.DisposeAsync();
        await using var offlinePeople = People(); await using var offlineStories = Stories();
        await offlinePeople.InitializeAsync(); await offlineStories.InitializeAsync();
        Assert.AreEqual(11, (await offlinePeople.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(23, (await offlineStories.QuerySingleAsync(_ => true)).Pages);
        Assert.IsTrue(offlinePeople.Connection.IsDowntime); Assert.IsTrue(offlineStories.Connection.IsDowntime);
    }

    [TestMethod]
    public async Task InterfaceScopes_AuthorizationDoesNotLeakAcrossIdenticallyNamedProfiles()
    {
        var port = FreeSetupPort();
        await using var peopleStore = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "people"));
        await using var storyStore = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "stories"));
        var people = Setup(manifest: InterfaceManifest<IRemotePerson>());
        var stories = Setup(manifest: InterfaceManifest<IRemoteStory>()) with { RemoteAlias = "stories" };
        var registry = new ManagedSetupRegistry([
            new(people, peopleStore, identity => identity.Principal.FindFirst("sub")?.Value == "parent-a"),
            new(stories, storyStore, identity => identity.Principal.FindFirst("sub")?.Value == "parent-b")],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1));
        await using var host = await TcpStoreHost.StartAsync(registry.Configure(HostOptions(peopleStore) with
            { Stores = [], AuthorizeIdentity = null, ListenEndPoint = new(IPAddress.Loopback, port) }));
        foreach (var (subject, allowed, denied) in new[]
        {
            ("parent-a", people, stories), ("parent-b", stories, people)
        })
        {
            Assert.AreEqual(allowed.Manifest, await Authority(port, contract: allowed.Manifest.ContractIdentity, subject: subject,
                receipts: new SetupReceipts()).DiscoverAsync());
            var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
                await Authority(port, contract: denied.Manifest.ContractIdentity, subject: subject, receipts: new SetupReceipts()).DiscoverAsync());
            Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
            var direct = SetupConnection(port, subject) with { Manifest = denied.Manifest, StoreAlias = denied.RemoteAlias };
            rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            { await using var connection = await TcpEncodedEnvelopeStore.ConnectAsync(direct); });
            Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        }
        Assert.ThrowsExactly<ArgumentException>(() => new ManagedSetupRegistry([
            new(people, peopleStore, _ => true), new(people with { LocalProviderId = "atlas" }, peopleStore, _ => true)],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1)));
    }

    [TestMethod]
    [DataRow(ContractRepositoryType.FileSystem)]
    [DataRow(ContractRepositoryType.SegmentCore)]
    [DataRow(ContractRepositoryType.AtlasCore)]
    public async Task InterfaceScopes_NativeProvidersKeepMultipleInterfacesAndInstancesIndependent(ContractRepositoryType type)
    {
        var people = new InterfaceScopeConfig<IRemotePerson>(Path.Combine(_root, "people"), type, concurrency: 1);
        var stories = new InterfaceScopeConfig<IRemoteStory>(Path.Combine(_root, "stories"), type, concurrency: 3);
        var archive = new InterfaceScopeConfig<IRemotePerson>(Path.Combine(_root, "archive"), type, "archive", concurrency: 5);
        ServiceProvider Open()
        {
            var services = new ServiceCollection();
            RegisterScopedProvider(services, people, new(typeof(RemotePerson)));
            RegisterScopedProvider(services, stories, new(typeof(RemoteStory)));
            RegisterScopedProvider(services, archive, new(typeof(RemotePerson)));
            return services.BuildServiceProvider();
        }
        await using (var services = Open())
        {
            foreach (var (key, concurrency) in new[] { (people.EnvelopeDICacheKey, 1), (stories.EnvelopeDICacheKey, 3), (archive.EnvelopeDICacheKey, 5) })
            {
                Assert.AreEqual(concurrency, services.GetRequiredKeyedService<StoragePolicy>(key).MaxConcurrency);
                if (type == ContractRepositoryType.SegmentCore)
                    Assert.AreEqual(concurrency * 100, services.GetRequiredKeyedService<SegmentCoreRepositoryOptions>(key).MinimumMutationsPerCheckpoint);
                if (type == ContractRepositoryType.AtlasCore)
                    Assert.AreEqual(concurrency * 100, services.GetRequiredKeyedService<AtlasCoreRepositoryOptions>(key).MinimumMutationsPerCheckpoint);
            }
            var first = services.GetRequiredKeyedService<IRepository<IRemotePerson>>(people.EnvelopeDICacheKey);
            var second = services.GetRequiredKeyedService<IRepository<IRemoteStory>>(stories.EnvelopeDICacheKey);
            var third = services.GetRequiredKeyedService<IRepository<IRemotePerson>>(archive.EnvelopeDICacheKey);
            await first.InitializeAsync(); await second.InitializeAsync(); await third.InitializeAsync();
            await ((IEnvelopeMetadataRepository<IRemotePerson>)first).SaveWithExtensionMetadataAsync(
                new RemotePerson { Name = "same-key", Score = 1 }, "same-key", null, new EnvelopeWriteCondition(0, ExpectedMissing: true));
            await ((IEnvelopeMetadataRepository<IRemoteStory>)second).SaveWithExtensionMetadataAsync(
                new RemoteStory { Title = "same-key", Pages = 2 }, "same-key", null, new EnvelopeWriteCondition(0, ExpectedMissing: true));
            await ((IEnvelopeMetadataRepository<IRemotePerson>)third).SaveWithExtensionMetadataAsync(
                new RemotePerson { Name = "same-key", Score = 3 }, "same-key", null, new EnvelopeWriteCondition(0, ExpectedMissing: true));
            Assert.AreEqual(1, (await first.QuerySingleAsync(_ => true)).Score);
            Assert.AreEqual(2, (await second.QuerySingleAsync(_ => true)).Pages);
            Assert.AreEqual(3, (await third.QuerySingleAsync(_ => true)).Score);
        }
        await using var reopened = Open();
        var reopenedPeople = reopened.GetRequiredKeyedService<IRepository<IRemotePerson>>(people.EnvelopeDICacheKey);
        var reopenedStories = reopened.GetRequiredKeyedService<IRepository<IRemoteStory>>(stories.EnvelopeDICacheKey);
        var reopenedArchive = reopened.GetRequiredKeyedService<IRepository<IRemotePerson>>(archive.EnvelopeDICacheKey);
        await reopenedPeople.InitializeAsync(); await reopenedStories.InitializeAsync(); await reopenedArchive.InitializeAsync();
        Assert.AreEqual(1, (await reopenedPeople.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(2, (await reopenedStories.QuerySingleAsync(_ => true)).Pages);
        Assert.AreEqual(3, (await reopenedArchive.QuerySingleAsync(_ => true)).Score);
    }

    private static void RegisterScopedProvider<T>(IServiceCollection services, InterfaceScopeConfig<T> config,
        StoredRuntimeTypeAllowlist<T> runtimeTypes) where T : class, IStoredContract<T>
    {
        switch (config.RepositoryType)
        {
            case ContractRepositoryType.FileSystem:
                services.AddFileRepository(config, runtimeTypeAllowlist: runtimeTypes); break;
            case ContractRepositoryType.SegmentCore:
                services.AddSegmentCoreRepository(config, new() { EnableBackgroundMaintenance = false,
                    MinimumMutationsPerCheckpoint = config.StoragePolicy.MaxConcurrency * 100 }, runtimeTypeAllowlist: runtimeTypes); break;
            case ContractRepositoryType.AtlasCore:
                services.AddAtlasCoreRepository(config, new() { EnableBackgroundMaintenance = false,
                    MinimumMutationsPerCheckpoint = config.StoragePolicy.MaxConcurrency * 100 }, runtimeTypeAllowlist: runtimeTypes); break;
            default: throw new AssertFailedException("Unexpected provider.");
        }
    }

    private sealed class InterfaceScopeConfig<T>(string? root, ContractRepositoryType type, string profile = "default", int concurrency = 2)
        : IDbContractConfigDto<T> where T : class, IStoredContract<T>
    {
        public string EnvelopeDICacheKey => InterfaceName<T>() + ":" + profile;
        public string ContractName => InterfaceName<T>();
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => type;
        public StoragePolicy StoragePolicy => new() { MaxConcurrency = concurrency, WatchStorageChanges = false, DurabilityMode = StorageDurabilityMode.Durable };
    }

    private sealed class StoryMapper : IResilientEntityMapper<IRemoteStory>
    {
        public string GetStorageKey(IRemoteStory entity) => entity.Title;
        public IRemoteStory CreateDetachedCopy(IRemoteStory source) => new RemoteStory { Title = source.Title, Pages = source.Pages };
        public void CopyValues(IRemoteStory source, IRemoteStory target) { target.Title = source.Title; target.Pages = source.Pages; }
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IRemoteStory : IStoredContract<IRemoteStory>
{
    [SearchIndex(SearchIndexMode.Exact)] string Title { get; set; }
    [SearchIndex] int Pages { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class RemoteStory : IRemoteStory
{
    public string Title { get; set; } = "";
    public int Pages { get; set; }
    [IgnoreMember] public IObjectEnvelope<IRemoteStory>? Envelope { get; private set; }
}
