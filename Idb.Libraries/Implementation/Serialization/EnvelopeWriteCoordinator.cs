using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Shared envelope policy. Providers hold their write guard from validation through commit and cache publication.</summary>
public sealed class EnvelopeWriteCoordinator<TContract> where TContract : class, IStoredContract<TContract>
{
    private readonly IWriteConflictStrategy<TContract> _strategy;
    private readonly IRevisionTimestampGenerator _clock;
    private readonly ConcurrentDictionary<string, long> _committed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly IRevisionObjectSerializer<TContract> _serializer;
    private readonly IEnvelopeLifecycle<TContract> _lifecycle;
    private readonly RetainedEditingSupport<TContract>? _editing;
    private readonly Func<string, byte[]>? _contextFactory;
    private static readonly ConditionalWeakTable<IObjectEnvelope<TContract>, EnvelopeWriteCondition> Conditions = new();

    public EnvelopeWriteCoordinator(string storeRoot, IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> lifecycle, IWriteConflictStrategy<TContract>? strategy = null)
        : this(storeRoot, serializer, lifecycle, strategy, null) { }

    public EnvelopeWriteCoordinator(string storeRoot, IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> lifecycle, IWriteConflictStrategy<TContract>? strategy,
        RetainedEditingSupport<TContract>? editing,
        Func<string, byte[]>? contextFactory = null)
        : this(new StoreRevisionClock(storeRoot), serializer, lifecycle, strategy, editing, contextFactory) { }

    /// <summary>
    /// Providers supplying a clock must reserve revisions durably under their authoritative
    /// write guard before publishing an accepted commit. The clock must survive deletion/reopen.
    /// </summary>
    public EnvelopeWriteCoordinator(IRevisionTimestampGenerator clock, IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> lifecycle, IWriteConflictStrategy<TContract>? strategy = null,
        RetainedEditingSupport<TContract>? editing = null, Func<string, byte[]>? contextFactory = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _serializer = serializer as IRevisionObjectSerializer<TContract>
            ?? throw new NotSupportedException("Envelope writes require an IRevisionObjectSerializer that preserves revision timestamps.");
        _lifecycle = lifecycle;
        _strategy = strategy ?? new StrictTimestampMatchStrategy<TContract>();
        _editing = editing;
        _contextFactory = contextFactory;
    }

    internal static void SetCondition(IObjectEnvelope<TContract> envelope, EnvelopeWriteCondition condition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(condition.ExpectedRevision);
        if (condition.Action == EnvelopeRevisionAction.Import && condition.SourceRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(condition), "Imports require a positive source revision.");
        Conditions.Remove(envelope);
        Conditions.Add(envelope, condition with { ExpectedExtensionMetadata = condition.ExpectedExtensionMetadata?.ToArray() });
        envelope.RevisionTimestampUtcMicroseconds = condition.ExpectedRevision;
    }

    internal static void CopyCondition(IObjectEnvelope<TContract> source, IObjectEnvelope<TContract> target)
    {
        if (Conditions.TryGetValue(source, out var condition)) SetCondition(target, condition);
    }

    internal static bool ExpectsMissing(IObjectEnvelope<TContract> envelope)
        => Conditions.TryGetValue(envelope, out var condition) && condition.ExpectedMissing;

    /// <summary>Embedded providers exclusively own the raw store, including its one-writer-process lease.</summary>
    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token)
    {
        EnvelopeResolverScope.ThrowIfActive();
        var gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new SingleLease(gate);
    }

    /// <summary>Multiple keys acquire distinct gates in a stable order to avoid deadlocks.</summary>
    public async ValueTask<IDisposable> AcquireAsync(IEnumerable<string> keys, CancellationToken token)
    {
        EnvelopeResolverScope.ThrowIfActive();
        var gates = keys.Select(k => (int)((uint)StringComparer.Ordinal.GetHashCode(k) % (uint)_gates.Length)).Distinct().Order().ToArray();
        var acquired = 0;
        try
        {
            for (; acquired < gates.Length; acquired++) await _gates[gates[acquired]].WaitAsync(token).ConfigureAwait(false);
            return new Lease(_gates, gates);
        }
        catch { for (var i = acquired - 1; i >= 0; i--) _gates[gates[i]].Release(); throw; }
    }

    public async ValueTask<IDisposable> AcquireMaintenanceAsync(CancellationToken token)
    {
        EnvelopeResolverScope.ThrowIfActive();
        var indexes = Enumerable.Range(0, _gates.Length).ToArray();
        var acquired = 0;
        try
        {
            for (; acquired < indexes.Length; acquired++) await _gates[acquired].WaitAsync(token).ConfigureAwait(false);
            return new Lease(_gates, indexes);
        }
        catch { for (var i = acquired - 1; i >= 0; i--) _gates[i].Release(); throw; }
    }

    public async ValueTask<long?> ReadCommittedRevisionAsync(string key, Func<CancellationToken, ValueTask<byte[]?>> read, CancellationToken token)
    {
        if (_committed.TryGetValue(key, out var revision)) return revision < 0 ? null : revision;
        var payload = await read(token).ConfigureAwait(false);
        var current = payload is null ? (long?)null : ReadStoredRevision(key, payload);
        _committed[key] = current ?? -1;
        return current;
    }

    public void Committed(string key, long? revision) => _committed[key] = revision ?? -1;

    public void ObserveCommittedPayload(string key, ReadOnlyMemory<byte> payload)
        => _committed.TryAdd(key, ReadStoredRevision(key, payload));

    /// <summary>
    /// Seeds an unknown revision from an already materialized, key-validated committed envelope.
    /// A read may finish after a concurrent commit or delete; never replace an existing entry.
    /// </summary>
    public void ObserveCommittedRevision(string key, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        _committed.TryAdd(key, revision);
    }

    public ValueTask<IObjectEnvelope<TContract>> PrepareAsync(IObjectEnvelope<TContract> candidate, bool isInsert,
        long? current, Func<CancellationToken, ValueTask<byte[]?>> read, CancellationToken token, bool deleting = false)
    {
        var key = candidate.EnvelopeCacheKey;
        var location = candidate.Location;
        async ValueTask<TContract?> ReadCurrent(CancellationToken ct)
        {
            var bytes = await read(ct).ConfigureAwait(false);
            if (bytes is null) return null;
            var envelope = DeserializeStored(key, bytes);
            envelope.EnvelopeCacheKey = key; envelope.Location = location;
            return _lifecycle.Unwrap(envelope);
        }
        return PrepareWithCurrentAsync(candidate, isInsert, current, ReadCurrent, token, deleting);
    }

    /// <summary>
    /// Uses a provider's already materialized current record instead of decoding its payload again.
    /// The provider must hold its authoritative write guard and supply key-validated committed state
    /// matching <paramref name="current"/>. Custom conflict strategies may edit returned objects;
    /// providers using those strategies must supply isolated copies.
    /// </summary>
    public async ValueTask<IObjectEnvelope<TContract>> PrepareWithCurrentAsync(
        IObjectEnvelope<TContract> candidate, bool isInsert, long? current,
        Func<CancellationToken, ValueTask<TContract?>> readCurrent, CancellationToken token, bool deleting = false)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        var key = candidate.EnvelopeCacheKey;
        var location = candidate.Location;
        var expected = candidate.RevisionTimestampUtcMicroseconds;
        Conditions.TryGetValue(candidate, out var condition);
        var context = new EnvelopeWriteConflict<TContract>(key, expected, current, isInsert || condition?.ExpectedMissing == true, candidate, readCurrent);
        var decision = condition is null && (_strategy is not StrictTimestampMatchStrategy<TContract> || candidate is RuntimeEditEnvelope<TContract>)
            ? await DecideExtendedAsync(context, deleting, token).ConfigureAwait(false)
            : await (condition is null ? _strategy : new StrictTimestampMatchStrategy<TContract>()).DecideAsync(context, token).ConfigureAwait(false);
        if (!decision.Accepted || decision.Disposition == EnvelopeWriteDisposition.Reject)
            throw new WriteConflictException<TContract>(context, decision);
        if (decision.Disposition == EnvelopeWriteDisposition.NoChange)
        {
            if (deleting || isInsert || current is null) throw new WriteConflictException<TContract>(context);
            var unchanged = await readCurrent(token).ConfigureAwait(false)
                ?? throw new WriteConflictException<TContract>(context);
            return new RuntimeEditEnvelope<TContract>(unchanged.Envelope!, noChange: true);
        }
        var resolved = decision.ResolvedCandidate ?? candidate;
        // Metadata-only acknowledgement needs both the revision and the delivery-state precondition.
        if (condition?.Action == EnvelopeRevisionAction.MetadataOnly)
        {
            if (current is null || deleting || isInsert) throw new InvalidOperationException("Metadata-only writes require an existing entity.");
            var existing = await readCurrent(token).ConfigureAwait(false);
            if (!(existing?.Envelope?.ExtensionMetadata ?? []).AsSpan().SequenceEqual(condition.ExpectedExtensionMetadata ?? []))
                throw new WriteConflictException<TContract>(context);
            // A materialized provider may lend its committed envelope. Stage metadata separately
            // so a failed write cannot alter the committed snapshot.
            resolved = CopyEnvelope(existing!.Envelope!);
            resolved.ObjectInstance = existing.Envelope!.ObjectInstance;
            resolved.ExtensionMetadata = candidate.ExtensionMetadata?.ToArray();
        }
        if (resolved.EnvelopeCacheKey != key || resolved.Location != location)
            throw new InvalidOperationException("A write strategy cannot change physical storage identity.");
        if (!deleting && !ReferenceEquals(resolved, candidate))
        {
            // Resolver and acknowledgement objects may belong to a caller; restage before stamping or serializing.
            var staged = CopyEnvelope(resolved);
            staged.ObjectInstance = Idb.Libraries.Implementation.Encryption.EncryptedStorageFieldProcessor<TContract>.CloneForStorage(
                resolved.ObjectInstance ?? throw new InvalidOperationException("A resolved envelope requires an object."));
            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(staged.ObjectInstance, staged);
            resolved = await _lifecycle.WrapAsync(staged.ObjectInstance).ConfigureAwait(false);
            resolved.EnvelopeCacheKey = key;
            resolved.Location = location;
        }
        if (resolved is RuntimeEditEnvelope<TContract> { Binding: not null })
        {
            var plain = CopyEnvelope(resolved, preserveEdit: false);
            plain.ObjectInstance = resolved.ObjectInstance;
            resolved = plain;
        }
        if (!deleting)
            resolved.RevisionTimestampUtcMicroseconds = condition?.Action switch
            {
                EnvelopeRevisionAction.Import => ObserveImportedRevision(condition.SourceRevision),
                EnvelopeRevisionAction.MetadataOnly => current!.Value,
                _ => _clock.Next(Math.Max(current ?? 0, expected))
            };
        if (decision is ContextDecision captured)
        {
            if (captured.PreserveMetadata) resolved.ExtensionMetadata = captured.CurrentMetadata;
            if (captured.Before is not null) CommittedWriteCapture<TContract>.RememberBefore(resolved, captured.Before);
        }
        return resolved;
    }

    private long ReadStoredRevision(string key, ReadOnlyMemory<byte> payload)
        => _contextFactory is null
            ? _serializer.ReadRevision(payload)
            : ContextualObjectSerialization.ReadRevision(
                (IObjectSerializer<TContract>)_serializer,
                payload,
                _contextFactory(key));

    private IObjectEnvelope<TContract> DeserializeStored(string key, byte[] payload)
        => _contextFactory is null
            ? _serializer.Deserialize(payload)
            : ContextualObjectSerialization.Deserialize(
                (IObjectSerializer<TContract>)_serializer,
                payload,
                _contextFactory(key));

    private long ObserveImportedRevision(long revision)
    {
        _ = _clock.Next(revision); // Reserve beyond imported revisions too, before they can commit.
        return revision;
    }

    public TContract Publish(TContract input, IObjectEnvelope<TContract> committed)
    {
        if (EnvelopeWriteOutcome.IsNoChange(committed)) return _lifecycle.Unwrap(committed);
        EnvelopeCommitOrder.Stamp(committed);
        var saved = _lifecycle.Unwrap(committed);
        // The caller learns its accepted token only after commit; cached committed results use their own envelope.
        if (!ReferenceEquals(saved, input))
        {
            var callerEnvelope = CopyEnvelope(committed, preserveEdit: false);
            callerEnvelope.ObjectInstance = input;
            EnvelopeCommitOrder.Copy(committed, callerEnvelope);
            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(input, callerEnvelope);
        }
        Conditions.Remove(committed);
        Committed(committed.EnvelopeCacheKey, committed.RevisionTimestampUtcMicroseconds);
        return saved;
    }

    internal static IObjectEnvelope<TContract> CopyEnvelope(IObjectEnvelope<TContract> source, bool preserveEdit = true)
    {
        IObjectEnvelope<TContract> copy = new FileObjectEnvelope<TContract>
        {
        EnvelopeCacheKey = source.EnvelopeCacheKey, VersionTag = source.VersionTag,
        RevisionTimestampUtcMicroseconds = source.RevisionTimestampUtcMicroseconds,
        Location = source.Location, State = source.State, ImplementationTypeName = source.ImplementationTypeName,
        CacheObjectInstance = source.CacheObjectInstance, ExtensionMetadata = source.ExtensionMetadata?.ToArray(),
        EncryptedFields = new Dictionary<string, EncryptedFieldValue>(source.EncryptedFields, StringComparer.Ordinal)
        };
        return preserveEdit && source is RuntimeEditEnvelope<TContract> { Binding: { } binding }
            ? new RuntimeEditEnvelope<TContract>(copy, binding) : copy;
    }

    private async ValueTask<EnvelopeWriteDecision<TContract>> DecideExtendedAsync(
        EnvelopeWriteConflict<TContract> context, bool deleting, CancellationToken token)
    {
        var binding = (context.Candidate as RuntimeEditEnvelope<TContract>)?.Binding;
        if (binding is not null && (!ReferenceEquals(binding.Owner, _editing)
            || !ReferenceEquals(binding.Reference.Owner, _editing) || binding.Reference.Key != context.Key
            || binding.Reference.Revision != context.ExpectedRevision || binding.Use is null))
            return new EnvelopeWriteRejected<TContract>("Invalid retained editing reference or submission. Use the issuing session's SaveAsync.");
        if (binding is not null && (!binding.Use!.IsSameIncarnation || context.CurrentRevision is null))
            return new EnvelopeWriteRejected<TContract>("The original entity incarnation was deleted.");
        var extended = new ExtendedConflict(this, context, binding, deleting);
        using var resolverScope = new EnvelopeResolverScope();
        var decision = await _strategy.DecideAsync(extended, token).ConfigureAwait(false);
        var captureBefore = decision.ResolvedCandidate is not null && CommittedWriteCapture<TContract>.Enabled;
        if (decision.Accepted && decision.Disposition == EnvelopeWriteDisposition.Commit
            && context.CurrentRevision is not null && (binding is not null || captureBefore))
        {
            var current = await context.ReadCurrentAsync(token).ConfigureAwait(false)
                ?? throw new RetainedVersionUnavailableException("The current entity is missing.");
            if (binding is not null && (current.Envelope!.VersionTag != binding.VersionTag
                || current.Envelope.ImplementationTypeName != binding.ImplementationTypeName))
                return new EnvelopeWriteRejected<TContract>("The retained contract version or concrete type is incompatible with current state.");
            ICommittedEnvelopeSnapshot<TContract>? before = null;
            if (captureBefore)
            {
                var staged = await _lifecycle.WrapAsync(current).ConfigureAwait(false);
                before = new CommittedEnvelopeSnapshot<TContract>(_serializer.Serialize(staged),
                    _serializer, _lifecycle, context.Key, context.Candidate.Location);
            }
            return new ContextDecision(decision, before, binding is not null, current.Envelope!.ExtensionMetadata?.ToArray());
        }
        return decision;
    }

    private sealed record ContextDecision(EnvelopeWriteDecision<TContract> Inner, ICommittedEnvelopeSnapshot<TContract>? Before,
        bool PreserveMetadata, byte[]? CurrentMetadata)
        : EnvelopeWriteDecision<TContract>(Inner.Accepted, Inner.ResolvedCandidate);

    private sealed record ExtendedConflict : EnvelopeWriteConflict<TContract>
    {
        private readonly EnvelopeWriteCoordinator<TContract> _owner;
        private readonly EditBinding<TContract>? _binding;
        public override bool IsDelete { get; }
        public override RetainedVersionReference? OriginalVersion => _binding?.Reference;
        internal ExtendedConflict(EnvelopeWriteCoordinator<TContract> owner, EnvelopeWriteConflict<TContract> context,
            EditBinding<TContract>? binding, bool deleting)
            : base(context.Key, context.ExpectedRevision, context.CurrentRevision, context.IsInsert, context.Candidate, context.ReadCurrentAsync)
        { _owner = owner; _binding = binding; IsDelete = deleting; }
        public override async ValueTask<TContract> ReadOriginalAsync(CancellationToken token = default)
        {
            if (_binding?.Use is null) throw new RetainedVersionUnavailableException("Original committed version was not retained before editing.");
            var value = _binding.Owner.Materialize(await _binding.Use.ReadAsync(token).ConfigureAwait(false), Key, _binding.Location);
            if (value.Envelope!.RevisionTimestampUtcMicroseconds != ExpectedRevision)
                throw new RetainedVersionUnavailableException("The retained record has a different original revision.");
            return value;
        }
        public override ValueTask<TContract> ReadIncomingAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var envelope = _owner._serializer.Deserialize(_owner._serializer.Serialize(Candidate));
            envelope.Location = Candidate.Location; envelope.EnvelopeCacheKey = Key;
            return ValueTask.FromResult(_owner._lifecycle.Unwrap(envelope));
        }
        public override async ValueTask<IObjectEnvelope<TContract>> PrepareResolvedAsync(TContract resolved, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var current = await ReadCurrentAsync(token).ConfigureAwait(false)
                ?? throw new RetainedVersionUnavailableException("The current entity was deleted.");
            var envelope = await _owner._lifecycle.WrapAsync(resolved).ConfigureAwait(false);
            envelope.EnvelopeCacheKey = Key; envelope.Location = Candidate.Location;
            envelope.ExtensionMetadata = current.Envelope!.ExtensionMetadata?.ToArray();
            envelope.VersionTag = current.Envelope.VersionTag;
            return envelope;
        }
    }

    private sealed class SingleLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class Lease(SemaphoreSlim[] gates, int[] indexes) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) for (var i = indexes.Length - 1; i >= 0; i--) gates[indexes[i]].Release(); }
    }
}
