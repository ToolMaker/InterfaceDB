using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Eager migration strategy that upgrades legacy payloads during initialisation.
/// </summary>
public sealed class FileSystemEagerMigrationStrategy<TContract> : FileSystemMigrationStrategyBase<TContract>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    public FileSystemEagerMigrationStrategy(
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        IObjectSerializer<TContract> serializer,
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IEnumerable<IMigration> migrations,
        MigrationStrategyType strategyType)
        : base(contractConfig, serializer, fileNameFactory, migrations, strategyType)
    {
        if (strategyType is not (MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant))
        {
            throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Eager strategy requires EagerStrict or EagerTolerant.");
        }
    }

    public override async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!DataRootDirectory.Exists || MigrationDescriptors.Count == 0)
        {
            yield break;
        }

        foreach (var legacyDirectory in EnumerateLegacyDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var migrated in ProcessDirectoryAsync(legacyDirectory, legacyDirectory.Name, cancellationToken).ConfigureAwait(false))
            {
                if (migrated is not null)
                {
                    yield return migrated;
                }
            }
        }
    }
}
