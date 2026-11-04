using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories;

public enum RepositoryConflictResolution { AcceptRemote, KeepLocal }

public sealed partial class ResilientRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Explicitly resolves one queued local conflict against the currently observed remote revision.
    /// KeepLocal requeues it with that base; another remote edit can still conflict before delivery.
    /// AcceptRemote discards the pending edit. The expected local revision prevents discarding a newer edit.
    /// </summary>
    public async ValueTask ResolveConflictAsync(string storageKey, long expectedLocalRevision,
        RepositoryConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        ThrowIfCompositionFeedbackWrite(ResilientRepositoryOperation.Synchronize);
        if (CompositionOptions is null) throw new NotSupportedException("Conflict resolution requires local/remote composition.");
        ValidateStorageKey(storageKey);
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        await InitializeCompositionAsync(cancellationToken).ConfigureAwait(false);
        await _compositionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed = false;
        try
        {
            ThrowIfDisposed();
            var local = (await CompositionSnapshotAsync(_preferred, true, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(storageKey)
                ?? throw new InvalidOperationException("No pending local record exists.");
            var metadata = ReadMetadata(local);
            if (!metadata.HasPendingMutation) throw new InvalidOperationException("No pending local mutation exists.");
            if (local.Envelope!.RevisionTimestampUtcMicroseconds != expectedLocalRevision)
                throw new WriteConflictException<TContract>(new(storageKey, expectedLocalRevision,
                    local.Envelope.RevisionTimestampUtcMicroseconds, false, local.Envelope,
                    token => QueryFirstByStorageKeyAsync(storageKey, token)));
            var remote = (await CompositionSnapshotAsync(_peer, false, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(storageKey);
            if (resolution == RepositoryConflictResolution.AcceptRemote)
            {
                if (remote is null) await _preferred.DeleteAsync(local, cancellationToken).ConfigureAwait(false);
                else await ImportCompositionAsync(remote, local, storageKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CompositionSaveAsync(_preferredMetadataRepository, CreateDetachedCopy(local, storageKey), local,
                    storageKey, metadata with { BasePeerRevision = remote?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                        PendingMutationId = Guid.NewGuid() },
                    new EnvelopeWriteCondition(expectedLocalRevision, EnvelopeRevisionAction.MetadataOnly,
                        ExpectedExtensionMetadata: local.Envelope.ExtensionMetadata), cancellationToken).ConfigureAwait(false);
                _compositionPending = true;
            }
            changed = true;
            Interlocked.Exchange(ref _compositionDirty, 1);
            _synchronizationWakeSignal.Writer.TryWrite(0);
        }
        finally
        {
            _compositionGate.Release();
            if (changed) PublishCompositionChange();
        }
    }
}
