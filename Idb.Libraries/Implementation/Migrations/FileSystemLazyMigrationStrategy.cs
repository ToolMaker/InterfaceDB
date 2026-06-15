using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Lazy migration strategy that upgrades payloads on-demand.
/// </summary>
public sealed class FileSystemLazyMigrationStrategy<TContract> : FileSystemMigrationStrategyBase<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public FileSystemLazyMigrationStrategy(
        IDbContractConfigDto<TContract> contractConfig,
        IObjectSerializer<TContract> serializer,
        IEnvelopeKeyAndLocationFactory<TContract> fileNameFactory,
        IEnumerable<IMigration> migrations,
        IEncryptor encryptor,
        IFileWriteCoordinator fileWriteCoordinator,
        IObjectReader<TContract> reader,
        IObjectWriter<TContract> writer)
        : base(contractConfig, serializer, fileNameFactory, migrations, MigrationStrategyType.Lazy, encryptor, fileWriteCoordinator, reader, writer)
    {
    }

    public override async IAsyncEnumerable<TContract> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
