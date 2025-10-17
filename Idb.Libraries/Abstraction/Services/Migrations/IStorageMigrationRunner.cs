namespace Idb.Libraries.Abstractions.Services.Migrations
{
    public interface IStorageMigrationRunner
    {
        Task RunAsync(CancellationToken cancellationToken = default);
    }
}

