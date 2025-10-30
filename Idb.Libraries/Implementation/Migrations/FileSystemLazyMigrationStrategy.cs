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
public sealed class FileSystemLazyMigrationStrategy<TContract> : FileSystemMigrationStrategyBase<TContract>
    where TContract : class, IRepoDto<TContract, string>
{
    public FileSystemLazyMigrationStrategy(
        IDbContractConfigDto<TContract, string> contractConfig,
        IObjectSerializer<TContract, string> serializer,
        IEnvelopeKeyAndLocationFactory<TContract, string> fileNameFactory,
        IEnumerable<IMigration> migrations,
        IObjectReader<TContract, string> reader,
        IObjectWriter<TContract, string> writer)
        : base(contractConfig, serializer, fileNameFactory, migrations, MigrationStrategyType.Lazy, reader, writer)
    {
    }

    public override async IAsyncEnumerable<TContract> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
