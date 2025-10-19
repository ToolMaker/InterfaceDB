using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Lazy migration strategy that upgrades payloads on-demand.
/// </summary>
internal sealed class FileSystemLazyMigrationStrategy<TContract> : FileSystemMigrationStrategyBase<TContract>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    public FileSystemLazyMigrationStrategy(
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        IObjectSerializer<TContract> serializer,
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IEnumerable<IMigration> migrations)
        : base(contractConfig, serializer, fileNameFactory, migrations, MigrationStrategyType.Lazy)
    {
    }

    public override async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
