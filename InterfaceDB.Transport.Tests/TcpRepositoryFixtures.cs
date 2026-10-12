using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
namespace InterfaceDB.Transport.Tests;
public sealed partial class TcpRepositoryTests
{
    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate()) await Task.Delay(25, timeout.Token);
    }

    private static ValueTask<IRemotePerson> Seed(TcpRepository<IRemotePerson> repository, string key, int score)
        => repository.SaveWithExtensionMetadataAsync(new RemotePerson { Name = key, Score = score }, key, null,
            new EnvelopeWriteCondition(0, ExpectedMissing: true));

    private sealed class CacheConfig(string root, string key = "cache") : IDbContractConfigDto<IRemotePerson>
    {
        public string EnvelopeDICacheKey => key;
        public string ContractName => "RemotePerson";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StoragePolicy? StoragePolicy => new() { RepositoryNotifications = new() };
    }
}
