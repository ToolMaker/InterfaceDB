using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Materializes opted-in edit buffers. The original object remains only in retained storage.</summary>
public sealed class RetainedEditingSupport<T> where T : class, IStoredContract<T>
{
    private readonly IObjectSerializer<T> _serializer;
    private readonly IEnvelopeLifecycle<T> _lifecycle;
    public RetainedEditingSupport(IObjectSerializer<T> serializer, IEnvelopeLifecycle<T> lifecycle)
    { _serializer = serializer; _lifecycle = lifecycle; }

    public async ValueTask<IRevisionEditSession<T>> BeginAsync(IRetainedPayloadLease lease, string location,
        Func<T, CancellationToken, ValueTask<T>> save, CancellationToken token)
    {
        try
        {
            var working = Materialize(await lease.ReadAsync(token).ConfigureAwait(false), lease.Key, location);
            var reference = new RetainedVersionReference(this, lease.Key, working.Envelope!.RevisionTimestampUtcMicroseconds, lease.RecordId);
            var binding = new EditBinding<T>(this, reference, lease, location,
                working.Envelope.VersionTag, working.Envelope.ImplementationTypeName);
            var envelope = new RuntimeEditEnvelope<T>(working.Envelope, binding);
            working = _lifecycle.Unwrap(envelope);
            return new Session(this, binding, working, save);
        }
        catch { lease.Dispose(); throw; }
    }

    internal T Materialize(byte[] bytes, string key, string location)
    {
        var envelope = _serializer.Deserialize(bytes);
        if (envelope.EnvelopeCacheKey != key) throw new RetainedVersionUnavailableException("Retained bytes have a different entity identity.");
        envelope.Location = location;
        return _lifecycle.Unwrap(envelope);
    }

    internal async ValueTask<T> FreezeAsync(T value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var envelope = await _lifecycle.WrapAsync(value).ConfigureAwait(false);
        return Materialize(_serializer.Serialize(envelope), envelope.EnvelopeCacheKey, envelope.Location!);
    }

    private sealed class Session(RetainedEditingSupport<T> owner, EditBinding<T> binding, T working,
        Func<T, CancellationToken, ValueTask<T>> save) : IRevisionEditSession<T>
    {
        private int _busy, _disposed, _completed;
        public T Working => working;
        public RetainedVersionReference OriginalVersion => binding.Reference;
        public long ExpectedRevision => binding.Reference.Revision;
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public async ValueTask<T> SaveAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("An edit session cannot save concurrently.");
            try
            {
                Check();
                using var use = binding.Lease.AcquireUse();
                var frozen = await owner.FreezeAsync(working, cancellationToken).ConfigureAwait(false);
                if (frozen.Envelope!.EnvelopeCacheKey != binding.Reference.Key
                    || frozen.Envelope.RevisionTimestampUtcMicroseconds != ExpectedRevision
                    || frozen.Envelope.Location != binding.Location
                    || frozen.Envelope.VersionTag != binding.VersionTag
                    || frozen.Envelope.ImplementationTypeName != binding.ImplementationTypeName)
                    throw new RetainedVersionUnavailableException("An editor cannot change the original identity, schema or expected revision.");
                var submission = new EditBinding<T>(owner, binding.Reference, binding.Lease, binding.Location,
                    binding.VersionTag, binding.ImplementationTypeName) { Use = use };
                frozen = owner._lifecycle.Unwrap(new RuntimeEditEnvelope<T>(frozen.Envelope, submission));
                var result = await save(frozen, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _completed, 1); binding.Lease.Dispose();
                return result;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }
        public async ValueTask<T> ReadOriginalAsync(CancellationToken cancellationToken = default)
        { Check(); return owner.Materialize(await binding.Lease.ReadAsync(cancellationToken).ConfigureAwait(false), binding.Reference.Key, binding.Location); }
        public void Renew(TimeSpan duration) { Check(); binding.Lease.Renew(duration); }
        private void Check()
        { if (Volatile.Read(ref _disposed) != 0 || IsCompleted) throw new RetainedVersionUnavailableException("This edit session has completed or been disposed."); }
        public ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref _disposed, 1) == 0) binding.Lease.Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class EditBinding<T>(RetainedEditingSupport<T> owner, RetainedVersionReference reference,
    IRetainedPayloadLease lease, string location, string versionTag, string? implementationTypeName) where T : class, IStoredContract<T>
{
    internal readonly RetainedEditingSupport<T> Owner = owner;
    internal readonly RetainedVersionReference Reference = reference;
    internal readonly IRetainedPayloadLease Lease = lease;
    internal readonly string Location = location;
    internal readonly string VersionTag = versionTag;
    internal readonly string? ImplementationTypeName = implementationTypeName;
    internal IRetainedPayloadUse? Use;
}

/// <summary>Only opted-in envelopes allocate this runtime association. It is never serialized.</summary>
internal sealed class RuntimeEditEnvelope<T>(IObjectEnvelope<T> inner, EditBinding<T>? binding = null, bool noChange = false)
    : IObjectEnvelope<T> where T : class, IStoredContract<T>
{
    internal EditBinding<T>? Binding { get; } = binding;
    internal bool NoChange { get; } = noChange;
    public string EnvelopeCacheKey { get => inner.EnvelopeCacheKey; set => inner.EnvelopeCacheKey = value; }
    public string VersionTag { get => inner.VersionTag; set => inner.VersionTag = value; }
    public long RevisionTimestampUtcMicroseconds { get => inner.RevisionTimestampUtcMicroseconds; set => inner.RevisionTimestampUtcMicroseconds = value; }
    public T? ObjectInstance { get => inner.ObjectInstance; set => inner.ObjectInstance = value; }
    public WeakReference<T>? ObjectInstanceWeakReference { get => inner.ObjectInstanceWeakReference; set => inner.ObjectInstanceWeakReference = value; }
    public string? ImplementationTypeName { get => inner.ImplementationTypeName; set => inner.ImplementationTypeName = value; }
    public bool CacheObjectInstance { get => inner.CacheObjectInstance; set => inner.CacheObjectInstance = value; }
    public IDictionary<string, EncryptedFieldValue> EncryptedFields { get => inner.EncryptedFields; set => inner.EncryptedFields = value; }
    public byte[]? ExtensionMetadata { get => inner.ExtensionMetadata; set => inner.ExtensionMetadata = value; }
    public string? Location { get => inner.Location; set => inner.Location = value; }
    public StorageObjectState State { get => inner.State; set => inner.State = value; }
}

public static class EnvelopeWriteOutcome
{
    public static bool IsNoChange<T>(IObjectEnvelope<T>? envelope) where T : class, IStoredContract<T>
        => envelope is RuntimeEditEnvelope<T> { NoChange: true };
}
