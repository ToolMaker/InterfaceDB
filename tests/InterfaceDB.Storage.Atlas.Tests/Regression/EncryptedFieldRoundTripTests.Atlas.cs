using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.AtlasCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

public sealed partial class EncryptedFieldRoundTripTests
{
    static partial void RegisterAdditionalProvider(IServiceCollection services, Config config, string engine, ref bool handled)
    {
        if (engine != "atlas") return;
        services.AddAtlasCoreRepository(config with { RepositoryType = ContractRepositoryType.AtlasCore });
        handled = true;
    }

    [TestMethod]
    public Task AtlasReaderFirstUpgrade() => ReaderFirstUpgradeRewritesLegacyFieldsAndStrictReopenSucceeds("atlas");

    [TestMethod]
    public Task AtlasConcreteQueriesMaterializeReadableIndependentValues()
        => ConcreteQueriesMaterializeReadableIndependentValues("atlas");

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public Task AtlasUpdatesPreserveEncryptedValuesAndExplicitNullsAfterReopen(bool proxy)
        => UpdatesPreserveEncryptedValuesAndExplicitNullsAfterReopen("atlas", proxy);
}
