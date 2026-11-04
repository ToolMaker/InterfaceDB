using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Storage.AtlasCore;

namespace InterfaceDb.Tests;

public sealed partial class EncryptedMigrationPreservationTests
{
    static partial void MigrateExternal(byte[] payload, byte[] context, IEncryptor encryptor, ref bool handled, ref ISecret? result)
    {
        handled = true;
        var engine = new AtlasCoreMigrationEngine<ISecret>("3.0.0", encryptor, [new First(), new Second()], _ => context);
        var migrated = engine.Migrate("record", "1.0.0", payload);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, migrated.ExtensionMetadata);
        result = migrated.Contract;
    }
}
