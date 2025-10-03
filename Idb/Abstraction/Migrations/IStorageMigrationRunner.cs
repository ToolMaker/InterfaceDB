namespace TransactionManager.Abstraction.Migrations
{
    public interface IStorageMigrationRunner
    {
        Task RunAsync(CancellationToken cancellationToken = default);
    }
}
