namespace TestScopeService.Migrations;

using System.Threading;
using System.Threading.Tasks;
using TestScopeService.Contracts.Person.v1_0_1;
using TestScopeService.Contracts.Person.v2_0_0;
using TransactionManager.Abstraction.Migrations;
using TransactionManager.Implementation.Migrations;

[StorageMigration(typeof(TestScopeService.Contracts.Person.v2_0_0.IPerson), "1.0.1", "2.0.0")]
public sealed class IPerson_v1_0_1_To_v2_0_0_Migration : StorageMigrationBase<TestScopeService.Contracts.Person.v2_0_0.IPerson>
{
    protected override async Task ExecuteCoreAsync(IStorageMigrationContext context, CancellationToken cancellationToken)
    {
        await foreach (var record in context.ReadSourceAsync<TestScopeService.Contracts.Person.v1_0_1.IPerson>(cancellationToken).ConfigureAwait(false))
        {
            // TODO: inspect record.Value and map to the new contract shape.
            throw new NotImplementedException();
        }
    }
}
