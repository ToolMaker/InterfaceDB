using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore;

public sealed partial class AtlasCoreStore
{
    private RetainedPayloadRegistry<RetainedAddress>? _retention;
    public RetainedVersionStatistics RetentionStatistics => _retention?.Statistics ?? new(0, 0, 0, 0, 0, 0, 0);
    public void EnableRetainedVersions(RetainedVersionOptions options)
    {
        ThrowIfUnavailable();
        if (_retention is not null) return;
        var registry = new RetainedPayloadRegistry<RetainedAddress>(_rootDirectory, options, EnterHistoryReadAsync,
            async (key, address, token) =>
            {
                return (await (await GetGroupAsync(address.Group).ConfigureAwait(false)).ReadAtAsync(key, address.Address, token).ConfigureAwait(false)).Payload
                    ?? throw new RetainedVersionUnavailableException("Retained Atlas record is missing.");
            });
        if (Interlocked.CompareExchange(ref _retention, registry, null) is not null) registry.Dispose();
    }
    public async ValueTask<IRetainedPayloadLease> AcquireRetainedAsync(string key, CancellationToken token = default)
    {
        var retention = _retention ?? throw new NotSupportedException("Atlas retained versions were not enabled.");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterExclusiveMutationAsync(token).ConfigureAwait(false);
        try
        {
            var route = _placement.Route(key);
            var groups = new List<(int Id, AtlasGroupStore Store)>();
            foreach (var groupId in route.ReadGroups) groups.Add((groupId, await GetGroupAsync(groupId).ConfigureAwait(false)));
            _indexVisibility.EnterReadLock();
            try
            {
                foreach (var group in groups)
                {
                    if (!group.Store.TryGetAddress(key, out var address)) continue;
                    if (address.Kind == AtlasGroupRecordKind.Delete) break;
                    return retention.Acquire(key, new(group.Id, _groupSets.CurrentRoot, address), address.FrameLength);
                }
                throw new RetainedVersionUnavailableException("The requested committed entity is missing.");
            }
            finally { _indexVisibility.ExitReadLock(); }
        }
        finally { ExitExclusiveMutation(); }
    }
    private async ValueTask<IDisposable> EnterHistoryReadAsync(CancellationToken token)
    { await EnterReadOperationAsync(token).ConfigureAwait(false); return new HistoryRead(this); }
    private sealed class HistoryRead(AtlasCoreStore owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ExitReadOperation(); }
    }
    private readonly record struct RetainedAddress(int Group, string GenerationRoot, AtlasGroupAddress Address);
}
