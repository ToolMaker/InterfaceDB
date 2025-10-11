namespace MigrationTool.Migrations;

using System.Threading;
using System.Threading.Tasks;
using MigrationTool.Contracts.Person.v2_1_0;
using MigrationTool.Contracts.Person.v2_1_1;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Implementation.Services.Migrations;

[StorageMigration(typeof(MigrationTool.Contracts.Person.v2_1_1.IPerson), "2.1.0", "2.1.1")]
public sealed class IPerson_v2_1_0_To_v2_1_1_Migration : StorageMigrationBase<MigrationTool.Contracts.Person.v2_1_1.IPerson>
{
    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)
    {
        await foreach (var record in context.ReadSourceAsync<MigrationTool.Contracts.Person.v2_1_0.IPerson>(cancellationToken).ConfigureAwait(false))
        {
            // TODO: inspect record.Value and map to the new contract shape.
            throw new NotImplementedException();
        }
    }
}
