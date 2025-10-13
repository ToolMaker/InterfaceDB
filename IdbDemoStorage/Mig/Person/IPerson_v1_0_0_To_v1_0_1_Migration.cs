namespace MigrationTool.Migrations;

using System.Threading;
using System.Threading.Tasks;
using MigrationTool.Contracts.Person.v1_0_0;
using MigrationTool.Contracts.Person.v1_0_1;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Implementation.Services.Migrations;

[StorageMigration(typeof(MigrationTool.Contracts.Person.v1_0_1.IPerson), "1.0.0", "1.0.1")]
public sealed class IPerson_v1_0_0_To_v1_0_1_Migration : StorageMigrationBase<MigrationTool.Contracts.Person.v1_0_1.IPerson>
{
    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)
    {
        await foreach (var record in context.ReadSourceAsync<MigrationTool.Contracts.Person.v1_0_0.IPerson>(cancellationToken).ConfigureAwait(false))
        {
            var upgraded = new MigrationTool.Contracts.Person.v1_0_1.Person(
                record.Value.FirstName,
                record.Value.LastName,
                string.Empty);

            await context.WriteAsync(record.Id, upgraded, cancellationToken).ConfigureAwait(false);
        }
    }
}
