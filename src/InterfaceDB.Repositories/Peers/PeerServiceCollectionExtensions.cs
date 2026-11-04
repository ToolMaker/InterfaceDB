using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Commercial.Repositories.Peers;

public static class PeerServiceCollectionExtensions
{
    /// <summary>Compose an existing private local store. The serializer and lifecycle are resolved using its exact DI key.</summary>
    public static IServiceCollection AddPeerReplicatedRepository<T>(this IServiceCollection services, string databaseKey,
        string localDatabaseKey, string journalDirectory, string datasetIdentity, string contractVersion,
        IResilientEntityMapper<T> mapper, IPeerConnectionFactory transport, PeerLimits? limits = null,
        ISequenceAccessor<T>? sequences = null, TimeSpan? retryDelay = null,
        Func<IServiceProvider, IPeerJournal>? journalFactory = null,
        IPeerHistoryDateAccessor<T>? historyDate = null) where T : class, IStoredContract<T>
    {
        if (string.IsNullOrWhiteSpace(databaseKey) || string.IsNullOrWhiteSpace(localDatabaseKey) || databaseKey == localDatabaseKey)
            throw new ArgumentException("Use distinct local and composed database registration keys.");
        var selected = limits ?? new(); selected.Validate();
        var accessor = sequences ?? new AttributedSequenceAccessor<T>();
        if (historyDate?.PropertyName != transport.HistoryDateProperty)
            throw new ArgumentException("The payload date accessor must match the connection registration's DateProperty.");
        services.AddKeyedSingleton<PeerReplicatedRepository<T>>(databaseKey, (provider, _) =>
        {
            var local = provider.GetRequiredKeyedService<IRepository<T>>(localDatabaseKey);
            var metadata = local as IEnvelopeMetadataRepository<T> ?? throw new NotSupportedException("Local provider needs conditional metadata commits.");
            var notifications = local as IRepositoryNotifications<T> ?? throw new NotSupportedException("Local provider needs repository notifications.");
            var codec = new PeerPayloadCodec<T>(provider.GetRequiredKeyedService<IObjectSerializer<T>>(localDatabaseKey),
                provider.GetRequiredKeyedService<IEnvelopeLifecycle<T>>(localDatabaseKey), mapper, accessor, datasetIdentity, contractVersion, selected, historyDate);
            return new(metadata, notifications, mapper, accessor, codec,
                journalFactory?.Invoke(provider) ?? new FilePeerJournal(journalDirectory, selected), transport, selected, retryDelay,
                provider.GetService<Idb.Diagnostics.IDatabaseTelemetry>());
        });
        services.AddKeyedSingleton<IRepository<T>>(databaseKey, (provider, key) => provider.GetRequiredKeyedService<PeerReplicatedRepository<T>>(key));
        services.AddKeyedSingleton<IRepositoryNotifications<T>>(databaseKey, (provider, key) => provider.GetRequiredKeyedService<PeerReplicatedRepository<T>>(key));
        return services;
    }
}
