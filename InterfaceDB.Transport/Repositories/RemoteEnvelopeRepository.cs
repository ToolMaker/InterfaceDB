using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Transport.Repositories;

/// <summary>One bounded current-state snapshot, protected by one exclusive remote session.</summary>
internal sealed class RemoteEnvelopeRepository<TContract> :
    IEnvelopeRepository<TContract>, IEnvelopeRepositoryInitializationStream<TContract>,
    IReaderAndWriter<TContract>, IObjectReader<TContract>, IObjectWriter<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private const string ClockKey = ".idb-remote-repository-clock-v1";
    private readonly TcpRepositoryOptions<TContract> _options;
    private readonly IEncodedEnvelopeStore _store;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IEnvelopeLifecycle<TContract> _lifecycle;
    private readonly IEnvelopeStorageIdentityFactory<TContract> _identity;
    private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IObjectEnvelope<TContract>> _envelopes = new(StringComparer.Ordinal);
    private readonly SnapshotClock _clock = new();
    private readonly Func<string, CancellationToken, ValueTask<TContract?>> _readFresh;
    private readonly EnvelopeWriteCoordinator<TContract> _revisions;
    private bool _initialized;

    internal RemoteEnvelopeRepository(TcpRepositoryOptions<TContract> options, IEncodedEnvelopeStore store,
        IObjectSerializer<TContract> serializer, IEnvelopeLifecycle<TContract> lifecycle,
        IEnvelopeStorageIdentityFactory<TContract> identity, Func<string, CancellationToken, ValueTask<TContract?>> readFresh)
    {
        _readFresh = readFresh; _options = options; _store = store; _serializer = serializer; _lifecycle = lifecycle; _identity = identity;
        _revisions = new(_clock, serializer, lifecycle, contextFactory: Context);
    }

    public IReaderAndWriter<TContract> ReaderAndWriter => this;
    public IObjectReader<TContract> Reader => this;
    public IObjectWriter<TContract> Writer => this;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        string? after = null;
        long bytes = 0;
        do
        {
            var page = await _store.EnumerateAsync(Math.Min(256, _options.Connection.Limits.MaximumPageItems), after, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records ?? [])
            {
                bytes = checked(bytes + record.Payload.Length);
                if (bytes > _options.MaximumSnapshotBytes) throw new InvalidDataException("The remote snapshot exceeds its byte limit.");
                if (record.Key == ClockKey)
                {
                    if (record.Payload.Length != 16) throw new InvalidDataException("Invalid remote revision clock.");
                    var floor = BinaryPrimitives.ReadInt64LittleEndian(record.Payload);
                    if (floor < 0 || BinaryPrimitives.ReadInt64LittleEndian(record.Payload.AsSpan(8)) != ~floor)
                        throw new InvalidDataException("Corrupt remote revision clock.");
                    _clock.Floor = floor;
                    continue;
                }
                if (_payloads.Count >= _options.MaximumSnapshotRecords || !_payloads.TryAdd(record.Key, record.Payload))
                    throw new InvalidDataException("The remote snapshot exceeds its record limit or contains duplicate keys.");
            }
            if (page.NextAfterKey is { } next && (after is not null && StringComparer.Ordinal.Compare(next, after) <= 0))
                throw new InvalidDataException("The remote enumeration cursor did not advance.");
            after = page.NextAfterKey;
        } while (after is not null);
        foreach (var entry in _payloads)
        {
            var envelope = Materialize(entry.Key);
            if (envelope.RevisionTimestampUtcMicroseconds <= 0 || envelope.RevisionTimestampUtcMicroseconds > _clock.Floor)
                throw new InvalidDataException("The record revision is not covered by the durable repository clock.");
            _revisions.ObserveCommittedRevision(entry.Key, envelope.RevisionTimestampUtcMicroseconds);
        }
        _initialized = true;
    }

    private IObjectEnvelope<TContract> Materialize(string key)
    {
        if (_envelopes.TryGetValue(key, out var cached)) return cached;
        var envelope = new EncodedEnvelopeSerializer<TContract>(_serializer,
            Math.Min(_options.StoragePolicy.MaximumStoredObjectBytes, _options.Connection.Limits.MaximumPayloadBytes)).Deserialize(key, _payloads[key], Context(key));
        if (envelope.VersionTag != _options.Contract.ActiveVersion)
            throw new InvalidDataException("A stored envelope baseline differs from the configured contract.");
        envelope.Location = key;
        envelope.State = StorageObjectState.Active;
        _ = _lifecycle.Unwrap(envelope);
        _envelopes.Add(key, envelope);
        return envelope;
    }

    private byte[] Context(string key) => StorageEncryptionContext.Create(
        _options.Contract, "tcp-repository-v1:" + _options.Connection.StoreAlias, key);
    private static string Logical(string key)
    {
        var dash = key.LastIndexOf('-');
        return dash > 0 && int.TryParse(key.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? key[..dash] : key;
    }
    private bool Matches(string physical, string logical) => physical == logical
        || (_identity.ObjectCardinality == StorageObjectCardinality.Multiple && Logical(physical) == logical);

    public bool Exists(string storageKey) => _payloads.Keys.Any(key => Matches(key, storageKey));
    public bool TryGet(string storageKey, out IReadOnlyList<IObjectEnvelope<TContract>> envelopes)
    {
        envelopes = _payloads.Keys.Where(key => Matches(key, storageKey)).Select(Materialize).ToArray();
        return envelopes.Count > 0;
    }

    public async ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(string physicalStorageKey, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return _payloads.ContainsKey(physicalStorageKey) ? Materialize(physicalStorageKey) : null;
    }
    public async IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in _payloads.Keys.Where(key => Matches(key, storageKey)))
        { cancellationToken.ThrowIfCancellationRequested(); yield return Materialize(key); }
    }
    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in _payloads.Keys)
        { cancellationToken.ThrowIfCancellationRequested(); yield return Materialize(key); }
    }
    public IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(CancellationToken cancellationToken = default)
        => EnumerateAsync(cancellationToken);
    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TImplementation : class, TContract
    {
        await foreach (var envelope in EnumerateAsync(cancellationToken).ConfigureAwait(false))
            if (envelope.ObjectInstance is TImplementation) yield return envelope;
    }

    public async ValueTask<TContract> WriteAsync(TContract contractInstance, CancellationToken cancellationToken = default)
        => (await WriteManyAsync([contractInstance], cancellationToken).ConfigureAwait(false))[0];

    public async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(IEnumerable<TContract> contractInstances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstances);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var inputs = contractInstances.Take(_options.Connection.Limits.MaximumBatchItems).ToArray();
        // One slot is reserved for the durable clock. Never split a requested batch silently.
        if (inputs.Length >= _options.Connection.Limits.MaximumBatchItems)
            throw new ArgumentException("The batch exceeds the negotiated limit including repository metadata.");
        if (inputs.Length == 0) return [];
        var candidates = new List<IObjectEnvelope<TContract>>(inputs.Length);
        var mutations = new List<EncodedMutation>(inputs.Length + 1);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index] ?? throw new ArgumentException("A batch entity is null.");
            if (input.Envelope?.State == StorageObjectState.Deleted)
                throw new InvalidOperationException("A deleted entity cannot be saved again.");
            var inserting = input.Envelope is null || string.IsNullOrWhiteSpace(input.Envelope.EnvelopeCacheKey);
            var candidate = await _lifecycle.WrapAsync(input).ConfigureAwait(false);
            if (inserting && _identity.ObjectCardinality == StorageObjectCardinality.Multiple)
            {
                var logical = candidate.EnvelopeCacheKey;
                var suffix = 0;
                do { candidate.EnvelopeCacheKey = logical + "-" + suffix++.ToString(CultureInfo.InvariantCulture); }
                while (_payloads.ContainsKey(candidate.EnvelopeCacheKey) || keys.Contains(candidate.EnvelopeCacheKey));
            }
            var key = candidate.EnvelopeCacheKey;
            if (key == ClockKey || !keys.Add(key)) throw new ArgumentException("Reserved or duplicate physical key.");
            candidate.Location = key;
            candidate.ImplementationTypeName = input.GetType().AssemblyQualifiedName;
            candidate.State = StorageObjectState.Active;
            var current = _payloads.ContainsKey(key) ? Materialize(key).RevisionTimestampUtcMicroseconds : (long?)null;
            candidate = await PrepareAsync(candidate, inserting, current, cancellationToken).ConfigureAwait(false);
            var payload = new EncodedEnvelopeSerializer<TContract>(_serializer,
            Math.Min(_options.StoragePolicy.MaximumStoredObjectBytes, _options.Connection.Limits.MaximumPayloadBytes)).Serialize(key, candidate, Context(key));
            candidates.Add(candidate);
            mutations.Add(new(key, payload));
        }
        // Do not commit a state that this repository cannot read on its next operation.
        var recordCount = _payloads.Count;
        var snapshotBytes = checked(16L + _payloads.Values.Sum(payload => (long)payload.Length));
        foreach (var mutation in mutations)
        {
            if (_payloads.TryGetValue(mutation.Key, out var prior)) snapshotBytes -= prior.Length;
            else recordCount++;
            snapshotBytes = checked(snapshotBytes + mutation.Payload!.Length);
        }
        if (recordCount > _options.MaximumSnapshotRecords || snapshotBytes > _options.MaximumSnapshotBytes)
            throw new InvalidDataException("The write would exceed the repository snapshot limit.");
        mutations.Add(new(ClockKey, _clock.Encode()));
        try { await _store.ApplyBatchAsync(mutations.ToArray(), cancellationToken).ConfigureAwait(false); }
        catch (RemoteCommitOutcomeUnknownException exception)
        {
            throw new RemoteRepositoryCommitOutcomeUnknownException(candidates.ToDictionary(
                candidate => candidate.EnvelopeCacheKey, candidate => (long?)candidate.RevisionTimestampUtcMicroseconds), exception);
        }
        var saved = new TContract[inputs.Length];
        for (var index = 0; index < inputs.Length; index++)
        {
            var candidate = candidates[index];
            _payloads[candidate.EnvelopeCacheKey] = mutations[index].Payload!;
            saved[index] = _revisions.Publish(inputs[index], candidate);
            _envelopes[candidate.EnvelopeCacheKey] = candidate;
        }
        return saved;
    }

    public async ValueTask<bool> DeleteAsync(TContract contractInstance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstance);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var envelope = contractInstance.Envelope ?? throw new InvalidOperationException("The entity has not been persisted.");
        if (envelope.State == StorageObjectState.Deleted) return false;
        var key = envelope.EnvelopeCacheKey;
        if (key == ClockKey) throw new ArgumentException("Reserved physical key.");
        var current = _payloads.ContainsKey(key) ? Materialize(key).RevisionTimestampUtcMicroseconds : (long?)null;
        await PrepareAsync(envelope, false, current, cancellationToken, deleting: true).ConfigureAwait(false);
        try { await _store.ApplyBatchAsync([new(key, null), new(ClockKey, _clock.Encode())], cancellationToken).ConfigureAwait(false); }
        catch (RemoteCommitOutcomeUnknownException exception)
        { throw new RemoteRepositoryCommitOutcomeUnknownException(new Dictionary<string, long?> { [key] = null }, exception); }
        _payloads.Remove(key);
        _envelopes.Remove(key);
        envelope.State = StorageObjectState.Deleted;
        return true;
    }

    private async ValueTask<IObjectEnvelope<TContract>> PrepareAsync(IObjectEnvelope<TContract> candidate,
        bool inserting, long? current, CancellationToken token, bool deleting = false)
    {
        ValueTask<TContract?> ReadCurrent(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // This operation holds the server lease and uses the strict revision strategy.
            // Materialize reuses the validated snapshot; acknowledgements need no second decode.
            return ValueTask.FromResult(_payloads.ContainsKey(candidate.EnvelopeCacheKey)
                ? Materialize(candidate.EnvelopeCacheKey).ObjectInstance : null);
        }
        try { return await _revisions.PrepareWithCurrentAsync(candidate, inserting, current, ReadCurrent, token, deleting).ConfigureAwait(false); }
        catch (WriteConflictException<TContract> conflict)
        {
            // Invoked by the caller after this operation releases its lease, never against its stale snapshot.
            throw new WriteConflictException<TContract>(new(conflict.Key, conflict.ExpectedRevision,
                conflict.CurrentRevision, inserting, candidate, ct => _readFresh(conflict.Key, ct)),
                conflict.Reason is null ? null : new EnvelopeWriteRejected<TContract>(conflict.Reason, conflict.Properties));
        }
    }

    public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        => (await GetPhysicalAsync(file.Name, cancellationToken).ConfigureAwait(false))?.ObjectInstance
            ?? throw new FileNotFoundException("Remote envelope not found.");

    // This operation-scoped provider does not expose subscriptions beyond its snapshot lifetime.
    public IContractSubscription SubscribeToAdded(Func<IObjectEnvelope<TContract>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
    public IContractSubscription SubscribeToRemoved(Func<IObjectEnvelope<TContract>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
    public IContractSubscription SubscribeToStored(Func<IObjectEnvelope<TContract>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
    public IContractSubscription SubscribeToDeleted(Func<IObjectEnvelope<TContract>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
    public ValueTask ContractAdded(IObjectEnvelope<TContract> envelope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ContractRemoved(IObjectEnvelope<TContract> envelope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ContractStored(IObjectEnvelope<TContract> envelope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ContractDeleted(IObjectEnvelope<TContract> envelope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    private sealed class Subscription : IContractSubscription
    { public bool IsDisposed { get; private set; } public void Dispose() => IsDisposed = true; }

    private sealed class SnapshotClock : IRevisionTimestampGenerator
    {
        private readonly MonotonicRevisionTimestampGenerator _generator = new();
        internal long Floor { get; set; }
        public long Next(long observedRevisionTimestampUtcMicroseconds)
            => Floor = _generator.Next(Math.Max(Floor, observedRevisionTimestampUtcMicroseconds));
        internal byte[] Encode()
        {
            var bytes = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, Floor);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), ~Floor);
            return bytes;
        }
    }
}
