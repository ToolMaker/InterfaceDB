using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Repository;

public interface IRevisionEditingRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    RetainedVersionCapability RetainedVersions { get; }
    RetainedVersionStatistics RetentionStatistics { get; }
    ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey, CancellationToken cancellationToken = default);
}

public static class RevisionEditingExtensions
{
    public static ValueTask<IRevisionEditSession<TContract>> BeginEditAsync<TContract>(this IRepository<TContract> repository,
        string physicalKey, CancellationToken cancellationToken = default) where TContract : class, IStoredContract<TContract>
        => repository is IRevisionEditingRepository<TContract> editing
            ? editing.BeginEditAsync(physicalKey, cancellationToken)
            : throw new NotSupportedException("This repository does not support local retained editing.");
}
