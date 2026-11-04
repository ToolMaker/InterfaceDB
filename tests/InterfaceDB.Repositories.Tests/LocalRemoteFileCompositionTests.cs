using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Commercial.Repositories.Tests;

public sealed partial class RealProviderCompositionTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task LocalRemoteCompositionWorksWithFileEndpointsWithoutTransport(RepositoryReadPreference mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-generic-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = CreateProvider(Path.Combine(root, "local"), Path.Combine(root, "remote"));
            var local = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-preferred");
            var remote = provider.GetRequiredKeyedService<IRepository<IRealProviderTestRecord>>("physical-peer");
            await using var repository = new ResilientRepository<IRealProviderTestRecord>(local, remote, new RealProviderTestMapper(),
                new ResilientRepositoryOptions<IRealProviderTestRecord>
                {
                    ReadPreference = mode, CacheScope = "dataset", EnableBackgroundSynchronization = false
                });
            var saved = await repository.SaveAsync(new RealProviderTestRecord { Key = "record", Value = "first" });
            if (mode == RepositoryReadPreference.LocalFirst)
                Assert.IsNull(await remote.QueryFirstAsync(_ => true));
            var sync = await repository.SynchronizeAsync();
            Assert.IsTrue(sync.IsSuccessful, string.Join(Environment.NewLine, sync.Failures.Select(failure => failure.Exception)));
            Assert.AreEqual("first", (await remote.QuerySingleAsync(_ => true)).Value);
            var value = await repository.QuerySingleAsync(_ => true);
            value.Value = "second";
            await repository.SaveAsync(value);
            Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
            Assert.AreEqual("second", (await remote.QuerySingleAsync(_ => true)).Value);
            await repository.DeleteAsync(await repository.QuerySingleAsync(_ => true));
            Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
            Assert.IsNull(await repository.QueryFirstAsync(_ => true));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
