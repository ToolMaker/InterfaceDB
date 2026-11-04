using System.Text.Json.Serialization;

namespace Idb.Commercial.Repositories.Peers;

/// <summary>Marks the nullable Int64 replication sequence on a stored contract. Null means locally pending.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SequenceNumberAttribute : Attribute;

public sealed record PeerLimits
{
    public int MaximumPayloadBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumRecords { get; init; } = 10_000;
    public int PageSize { get; init; } = 128;
    public int MaximumPeers { get; init; } = 32;
    public long MaximumReplicaBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Overall budget for a round involving multiple bounded peer requests, including queueing.</summary>
    public TimeSpan ReconciliationTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public void Validate()
    {
        if (MaximumPayloadBytes is < 1 or > 16 * 1024 * 1024 || MaximumRecords is < 1 or > 100_000
            || PageSize is < 1 or > 128 || MaximumPeers is < 1 or > 1024 || MaximumReplicaBytes < MaximumPayloadBytes
            || MaximumReplicaBytes > 1024L * 1024 * 1024
            || RequestTimeout < TimeSpan.FromMilliseconds(10) || RequestTimeout > TimeSpan.FromMinutes(5)
            || ReconciliationTimeout < RequestTimeout || ReconciliationTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(PeerLimits));
    }
}

/// <summary>Sequence identifies the latest mutation; RecordId remains stable across edits and deletions.</summary>
public sealed record PeerRecordDescriptor(string RecordId, Guid MutationId, long BaseSequence, long Sequence,
    bool Deleted, string PayloadHash)
{
    public long CreatedSequence { get; init; }
    public DateTimeOffset? HistoryDateUtc { get; init; }
    public void Validate(bool assigned)
    {
        if (string.IsNullOrWhiteSpace(RecordId) || System.Text.Encoding.UTF8.GetByteCount(RecordId) > 512
            || MutationId == Guid.Empty || BaseSequence < 0 || Sequence < 0 || assigned && Sequence <= 0
            || assigned && Sequence <= BaseSequence || CreatedSequence < 0 || assigned && CreatedSequence > Sequence
            || !assigned && CreatedSequence != 0 || HistoryDateUtc is { } date && (date == DateTimeOffset.MinValue || date == DateTimeOffset.MaxValue)
            || PayloadHash.Length != 64 || !PayloadHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid peer record descriptor.");
    }
}

public sealed record PeerRecord(PeerRecordDescriptor Descriptor, byte[] Payload);
public sealed record PeerJournalEntry(string RecordId, PeerRecord? Committed, PeerRecord? Pending, PeerRecord? Publishing = null);
public sealed record PeerSyncStatus(long ThroughSequence, int MissingRecords, int PendingRecords = 0, bool Reconciled = false)
{
    public bool IsComplete => Reconciled && MissingRecords == 0 && PendingRecords == 0;
}

public enum PeerCommand { Hello, Inventory, Read, Apply, Allocate, Reconcile, Status }
public enum PeerError { None, InvalidRequest, Unauthorized, Incompatible, Conflict, Unavailable, LimitExceeded, Failure }
public enum PeerConflictResolution { UseRemote, KeepLocal }

/// <summary>Transport-independent controls. Payload contains the registered object serializer's opaque bytes.</summary>
public sealed record PeerMessage
{
    public PeerCommand Command { get; init; }
    public long CorrelationId { get; init; }
    public bool IsResponse { get; init; }
    public PeerError Error { get; init; }
    public string? Dataset { get; init; }
    public string? Manifest { get; init; }
    public string? AccessToken { get; init; }
    public string? Account { get; init; }
    public long Cursor { get; init; }
    public int PageSize { get; init; }
    public PeerRecordDescriptor? Record { get; init; }
    public PeerRecordDescriptor[]? Records { get; init; }
    public PeerSyncStatus? Status { get; init; }
    public PeerLimits? Limits { get; init; }
    public PeerRegistrationOptions? Registration { get; init; }
    [JsonIgnore] public byte[]? Payload { get; init; }
    public override string ToString() => $"PeerMessage {{ Command = {Command}, Error = {Error} }}";
}

public sealed class PeerProtocolException(PeerError error) : IOException($"Peer operation failed: {error}.")
{
    public PeerError Error { get; } = error;
}

public interface IPeerConnection : IAsyncDisposable
{
    Task Completion { get; }
    ValueTask<PeerMessage> RequestAsync(PeerMessage request, CancellationToken cancellationToken = default);
}

public interface IPeerConnectionFactory
{
    /// <summary>Includes endpoint, dataset, contract/version/serializer identity and signed-in account.</summary>
    string Scope { get; }
    string? HistoryDateProperty => null;
    ValueTask<IPeerConnection> ConnectAsync(Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> handler,
        CancellationToken cancellationToken = default);
}

/// <summary>Exclusive local journal. Commit must atomically persist an entry before returning.</summary>
public interface IPeerJournal : IAsyncDisposable
{
    ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PeerJournalEntry>> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask PutAsync(PeerJournalEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Durable metadata authority. Never stores application payloads; retries return their original assignment.</summary>
public interface IPeerSequenceLedger : IAsyncDisposable
{
    ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default);
    ValueTask<PeerRecordDescriptor> AllocateAsync(PeerRecordDescriptor mutation, string principal,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PeerRecordDescriptor>> SnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed record PeerRange(long Start, long End);
public sealed record PeerTransfer(string Source, PeerRecordDescriptor Record, IReadOnlyList<string> Targets);
public interface IPeerRangePlanner
{
    IReadOnlyList<PeerTransfer> Plan(IReadOnlyList<PeerRecordDescriptor> authoritative,
        IReadOnlyDictionary<string, IReadOnlyList<PeerRecordDescriptor>> inventories,
        Func<string, PeerRecordDescriptor, bool>? receive = null, Func<string, PeerRecordDescriptor, bool>? provide = null);
}
