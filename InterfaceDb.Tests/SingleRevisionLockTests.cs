using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SingleRevisionLockTests
{
    [TestMethod]
    public async Task SingleGateSharesBatchProtectionAndCancellationDoesNotLeakOrOverRelease()
    {
        await using var f = new Fixture(true, enabled: false);
        var coordinator = new EnvelopeWriteCoordinator<IRecord>(f.Root,
            f.Services.GetRequiredService<IObjectSerializer<IRecord>>(),
            f.Services.GetRequiredService<IEnvelopeLifecycle<IRecord>>());
        var single = await coordinator.AcquireAsync("same", CancellationToken.None);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => coordinator.AcquireAsync(new[] { "same", "other" }, cancel.Token).AsTask());
        single.Dispose(); single.Dispose();
        using var batch = await coordinator.AcquireAsync(new[] { "same", "other" }, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => coordinator.AcquireAsync("same", timeout.Token).AsTask());
    }
}
