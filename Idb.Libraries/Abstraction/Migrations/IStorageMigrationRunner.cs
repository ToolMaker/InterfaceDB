namespace Idb.Libraries.Abstractions.Migrations
{
    public interface IStorageMigrationRunner
    {
        Task RunAsync(CancellationToken cancellationToken = default);
    }
}
