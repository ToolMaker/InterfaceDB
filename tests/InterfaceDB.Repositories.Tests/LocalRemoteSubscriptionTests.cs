using Idb.Libraries.Repositories;
using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Commercial.Repositories.Tests;

public sealed partial class RealProviderCompositionTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task CompositionUsesExistingFileRepositoryPubSubInBothDirections(RepositoryReadPreference mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-file-pubsub-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = CreateProvider(Path.Combine(root, "local"), Path.Combine(root, "remote"), notifications: true);
            var local = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-preferred");
            var remote = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-peer");
            await using var repository = new ResilientRepository<IRealProviderTestRecord>(local, remote, new RealProviderTestMapper(),
                new ResilientRepositoryOptions<IRealProviderTestRecord>
                { ReadPreference = mode, CacheScope = "file-pubsub", FailureHandler = failure => Console.WriteLine(failure.Exception), BackgroundRetryDelay = TimeSpan.FromMilliseconds(50) });
            await repository.InitializeAsync();
            var created = await remote.SaveAsync(new RealProviderTestRecord { Key = "record", Value = "remote" });
            await Eventually(async () => (await local.QueryFirstAsync(_ => true))?.Value == "remote");
            int changes = 0;
            await using var subscription = repository.SubscribeChanges(() =>
            { Interlocked.Increment(ref changes); return ValueTask.CompletedTask; });
            var localValue = await repository.QuerySingleAsync(_ => true);
            localValue.Value = "local";
            await repository.SaveAsync(localValue);
            await Eventually(async () => (await remote.QuerySingleAsync(_ => true)).Value == "local");
            var update = await remote.QuerySingleAsync(_ => true);
            update.Value = "remote-again";
            await remote.SaveAsync(update);
            await Eventually(async () => (await local.QueryFirstAsync(_ => true))?.Value == "remote-again");
            await Eventually(() => Task.FromResult(changes > 0));
            await subscription.DisposeAsync();
            var count = changes;
            await remote.DeleteAsync(await remote.QuerySingleAsync(_ => true));
            await Eventually(async () => await local.QueryFirstAsync(_ => true) is null);
            Assert.AreEqual(count, changes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
}

[TestClass]
public sealed class RepositoryChangePublisherTests
{
    [TestMethod]
    public async Task CallbackCanDisposeItselfAndPublisherCanTerminateSubscribers()
    {
        using var publisher = new RepositoryChangePublisher();
        IRepositoryChangeSubscription? subscription = null;
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription = publisher.SubscribeChanges(async () =>
        {
            await subscription!.DisposeAsync();
            detached.TrySetResult();
        });
        publisher.PublishChanged();
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(subscription.IsDisposed);
        using var another = publisher.SubscribeChanges(() => ValueTask.CompletedTask);
        publisher.Dispose();
        await another.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(another.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => publisher.SubscribeChanges(() => ValueTask.CompletedTask));
    }
}
