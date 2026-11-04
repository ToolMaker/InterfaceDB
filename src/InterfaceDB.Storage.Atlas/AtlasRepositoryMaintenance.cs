using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;

namespace Idb.Storage.AtlasCore;

public interface IAtlasConcurrentRepositoryMaintenance<TContract> : ICompactionMaintenance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    ValueTask<AtlasConcurrentCompactionResult> CompactConcurrentAsync(CancellationToken cancellationToken = default);
}

public sealed partial class AtlasCoreEnvelopeRepository<TContract> : IAtlasConcurrentRepositoryMaintenance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private CompactionScheduler? _compactionScheduler;
    public IMaintenanceSubscription SubscribeMaintenance(
        Func<MaintenanceNotification, CancellationToken, ValueTask> handler, CancellationToken lifetime = default) =>
        GetCompactionScheduler().SubscribeMaintenance(handler, lifetime);

    public async ValueTask<CompactionEstimate> EstimateCompactionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(false, cancellationToken).ConfigureAwait(false);
        return await Store.EstimateConcurrentCompactionAsync(_options.ConcurrentCompaction, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CompactionRunResult> RunCompactionAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(false, cancellationToken).ConfigureAwait(false);
        return await GetCompactionScheduler().RunAsync(manual, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AtlasConcurrentCompactionResult> CompactConcurrentAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(false, cancellationToken).ConfigureAwait(false);
        return await GetCompactionScheduler().ObserveAsync(CompactConcurrentCoreAsync, static r => r.Work, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AtlasConcurrentCompactionResult> CompactConcurrentCoreAsync(CancellationToken token)
    {
        var watermark = Volatile.Read(ref _totalMutations);
        var result = await Store.CompactConcurrentAsync(_options.ConcurrentCompaction, token).ConfigureAwait(false);
        if (result.Published)
        {
            Volatile.Write(ref _compactionMutationWatermark, watermark);
            Volatile.Write(ref _checkpointMutationWatermark, watermark);
            Volatile.Write(ref _lastCompactionUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            Volatile.Write(ref _lastCheckpointUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        Volatile.Write(ref _lastMaintenanceError, null);
        return result;
    }

    private CompactionWorkResult SummarizeLegacy(AtlasCoreCompactionResult result) =>
        new(result.FramesCopied, result.BytesCopied, result.BytesReclaimed, RetentionStatistics.RetainedBytes);

    private CompactionScheduler GetCompactionScheduler()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _compactionScheduler ??= new CompactionScheduler(
                token => Store.EstimateConcurrentCompactionAsync(_options.ConcurrentCompaction, token),
                async token => _options.ConcurrentCompaction is not null
                    ? (await CompactConcurrentCoreAsync(token).ConfigureAwait(false)).Work
                    : SummarizeLegacy(await CompactLegacyCoreAsync(token).ConfigureAwait(false)),
                _options.CompactionSchedule);
        }
    }
}
