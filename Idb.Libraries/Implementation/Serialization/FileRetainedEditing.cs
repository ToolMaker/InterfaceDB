using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

public partial class FileObjectWriter<TContract> where TContract : class, IStoredContract<TContract>
{
    private readonly RetainedEditingSupport<TContract>? _editing;
    private readonly RetainedPayloadRegistry<Guid>? _retention;
    private readonly bool _mayResolve;
    public RetainedVersionCapability RetainedVersions => new(_editing is not null && _storagePolicy.UseSidecarWriteLocks
        && !string.Equals(_contractConfig.FileExtension.TrimStart('.'), "edit-incarnation", StringComparison.OrdinalIgnoreCase)
        && _fileWriteCoordinator is CoalescingFileWriteCoordinator<TContract>,
        "Retained File editing requires retention options, sidecar locks, the supplied coordinator, and a payload extension other than the reserved .edit-incarnation suffix.");
    public RetainedVersionStatistics RetentionStatistics => _retention?.Statistics ?? new(0, 0, 0, 0, 0, 0, 0);

    public async ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey,
        Func<TContract, CancellationToken, ValueTask<TContract>> saveAsync, CancellationToken cancellationToken = default)
    {
        EnvelopeResolverScope.ThrowIfActive();
        if (!RetainedVersions.Supported) throw new NotSupportedException(RetainedVersions.Limitation);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        var identity = new FileObjectEnvelope<TContract> { EnvelopeCacheKey = physicalKey, VersionTag = _contractConfig.ActiveVersion ?? "1.0.0" };
        try { _storageIdentityFactory.InitEnvelope(identity); }
        catch (Exception exception) when (exception is NullReferenceException or InvalidOperationException)
        { throw new NotSupportedException("Retained File editing requires an identity factory that resolves an existing physical key without a caller's mutable object.", exception); }
        if (identity.EnvelopeCacheKey != physicalKey || identity.Location is null)
            throw new NotSupportedException("The configured identity factory cannot resolve an existing physical key for retained editing.");
        var path = Path.GetFullPath(identity.Location);
        var relative = Path.GetRelativePath(_identityLockScope, path);
        if (RetainedHistoryNamespace.ContainsRelative(relative) || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Physical key resolves outside the configured store.", nameof(physicalKey));
        IRetainedPayloadLease? captured = null;
        try
        {
            await ((IConditionalFileWriteCoordinator)_fileWriteCoordinator).InspectCommittedAsync(path, async (currentPath, token) =>
            {
                if (currentPath is null) throw new RetainedVersionUnavailableException("The requested committed entity is missing.");
                var size = new FileInfo(currentPath).Length;
                var lease = _retention!.Acquire(physicalKey, Guid.NewGuid(), size);
                captured = lease;
                var payload = await File.ReadAllBytesAsync(currentPath, token).ConfigureAwait(false);
                await _retention.ArchiveAsync(lease, payload, token).ConfigureAwait(false);
                captured = new IncarnationLease(lease, path, FileRetentionIncarnation.Capture(path));
            }, cancellationToken).ConfigureAwait(false);
            return await _editing!.BeginAsync(captured!, path, saveAsync, cancellationToken).ConfigureAwait(false);
        }
        catch { captured?.Dispose(); throw; }
    }

    public void Dispose() => _retention?.Dispose();
    private sealed class HistoryRead : IDisposable
    { internal static readonly HistoryRead Instance = new(); public void Dispose() { } }

    private sealed class IncarnationLease(IRetainedPayloadLease inner, string path, string incarnation) : IRetainedPayloadLease
    {
        public Guid RecordId => inner.RecordId;
        public string Key => inner.Key;
        public IRetainedPayloadUse AcquireUse() => new IncarnationUse(inner.AcquireUse(), path, incarnation);
        public void Renew(TimeSpan duration) => inner.Renew(duration);
        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public void Dispose() => inner.Dispose();
    }
    private sealed class IncarnationUse(IRetainedPayloadUse inner, string path, string incarnation) : IRetainedPayloadUse
    {
        public bool IsSameIncarnation => inner.IsSameIncarnation && FileRetentionIncarnation.Matches(path, incarnation);
        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
