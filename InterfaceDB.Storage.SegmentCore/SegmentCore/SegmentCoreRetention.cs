using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

public sealed partial class SegmentCoreStore
{
    private readonly RetainedPayloadRegistry<StripedObjectLogRecordAddress>? _retention;
    public RetainedVersionStatistics RetentionStatistics => _retention?.Statistics ?? new(0, 0, 0, 0, 0, 0, 0);

    public async ValueTask<IRetainedPayloadLease> AcquireRetainedAsync(string key, CancellationToken token = default)
    {
        var retention = _retention ?? throw new NotSupportedException("Segment Core retained versions were not enabled at open.");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        // Admission excludes compaction retirement, while the index lock makes selection + hold atomic with deletion.
        await EnterMutationAsync(token).ConfigureAwait(false);
        try
        {
            _indexVisibility.EnterReadLock();
            try
            {
                if (!_primaryIndex.TryGetValue(key, out var address))
                    throw new RetainedVersionUnavailableException("The requested committed entity is missing.");
                return retention.Acquire(key, address, address.Address.FrameLength);
            }
            finally { _indexVisibility.ExitReadLock(); }
        }
        finally { Interlocked.Decrement(ref _activeMutations); }
    }

    private async ValueTask<IDisposable> EnterHistoryReadAsync(CancellationToken token)
    { await EnterReadOperationAsync(token).ConfigureAwait(false); return new HistoryRead(this); }

    private sealed class HistoryRead(SegmentCoreStore owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._activeReads); }
    }
}
