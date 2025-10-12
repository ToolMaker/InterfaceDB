namespace MigrationTool.Migrations;

using System.Threading;
using System.Threading.Tasks;
using MigrationTool.Contracts.Person.v1_0_1;
using MigrationTool.Contracts.Person.v1_0_2;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Implementation.Services.Migrations;

[StorageMigration(typeof(MigrationTool.Contracts.Person.v1_0_2.IPerson), "1.0.1", "1.0.2")]
public sealed class IPerson_v1_0_1_To_v1_0_2_Migration : StorageMigrationBase<MigrationTool.Contracts.Person.v1_0_2.IPerson>
{
    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)
    {
        await foreach (var record in context.ReadSourceAsync<MigrationTool.Contracts.Person.v1_0_1.IPerson>(cancellationToken).ConfigureAwait(false))
        {
            // TODO: inspect record.Value and map to the new contract shape.
            throw new NotImplementedException();
        }
    }
}
