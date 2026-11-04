using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.AtlasCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task ContractPolicy_DeclaredCriterionAutomaticallyIndexesAtlasWithoutAuthentication()
    {
        var config = new TenantConfig(_root, ContractRepositoryType.AtlasCore);
        var collection = new ServiceCollection().AddAtlasCoreRepository(config, runtimeTypeAllowlist: new(typeof(TenantRecord)));
        await using var services = collection.BuildServiceProvider();
        var repository = services.GetRequiredKeyedService<IRepository<ITenantRecord>>("tenants");
        await repository.InitializeAsync();
        await repository.SaveAsync(new TenantRecord { TenantId = "a", Text = "first" });
        await repository.SaveAsync(new TenantRecord { TenantId = "b", Text = "second" });
        Assert.AreEqual("first", (await repository.SearchFirstAsync(r => r.TenantId == "a"))!.Text);
        Assert.IsTrue(services.GetRequiredKeyedService<IIndexManager<ITenantRecord>>("tenants").ExplainSearch(r => r.TenantId == "a").UsesIndexes);
        Assert.AreEqual("second", (await repository.SearchFirstAsync(r => r.TenantId == "b"))!.Text);
    }
}
