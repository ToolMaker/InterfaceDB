using System.Buffers.Binary;

namespace Idb.Transport;

/// <summary>Authorizes preservation of an externally supplied revision. Ordinary writes do not need this grant.</summary>
public interface IRepositoryRevisionImportPolicy
{
    bool AuthorizeImport(DataAccessContext context, DataAccessScope authorizedScope, long sourceRevision);
}

/// <summary>
/// Session-scoped metadata authority over an already authorized data scope. The host holds the
/// exclusive scope lease through completion, including uncertain commits. Payloads remain opaque.
/// </summary>
internal sealed class RepositoryRevisionStore(IEncodedEnvelopeStore inner, TimeProvider time,
    TransportLimits limits) : IEncodedEnvelopeStore
{
    private long? _reservedFloor;
    internal const string ClockKey = ".idb-remote-repository-clock-v1";
    public EncodedStoreCapabilities Capabilities => inner.Capabilities | EncodedStoreCapabilities.ReserveRevisions;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
    {
        // Snapshot before checking: callers cannot replace a batch slot after validation.
        request = request with { Mutations = request.Mutations?.ToArray() };
        if ((request.Command is EncodedStoreCommand.Put or EncodedStoreCommand.Delete && request.Key == ClockKey)
            || request.Mutations?.Any(m => m.Key == ClockKey) == true)
            throw Reject("The repository revision clock is server-owned.");
        if (request.Command != EncodedStoreCommand.ReserveRevisions)
        {
            if (_reservedFloor is not { } reserved || request.Command is not
                (EncodedStoreCommand.Put or EncodedStoreCommand.Delete or EncodedStoreCommand.ApplyBatch))
                return await inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            var mutations = request.Command == EncodedStoreCommand.ApplyBatch ? request.Mutations!
                : [new EncodedMutation(request.Key!, request.Command == EncodedStoreCommand.Delete ? null : request.Payload)];
            // Commit the reserved high-water mark in the SAME atomic batch as its records.
            // Validation/capacity rejections must leave both data and durable clock unchanged.
            var result = await inner.ApplyBatchAsync([.. mutations, new(ClockKey, EncodeClock(reserved))], cancellationToken).ConfigureAwait(false);
            _reservedFloor = null;
            var affected = result.AffectedCount - 1;
            return new() { AffectedCount = affected, Found = request.Command == EncodedStoreCommand.Delete && affected != 0 };
        }

        TransportValidation.Request(request, limits);
        var imported = request.Payload is null ? 0 : BinaryPrimitives.ReadInt64LittleEndian(request.Payload);
        var existing = await inner.GetAsync(ClockKey, cancellationToken).ConfigureAwait(false);
        var floor = Math.Max(existing is null ? 0 : DecodeClock(existing), _reservedFloor ?? 0);
        long first, last;
        try
        {
            first = Math.Max((time.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
                checked(Math.Max(floor, imported) + 1));
            last = checked(first + request.PageSize - 1);
            // Always leave room for the next revision. Existing exhausted stores require explicit recovery.
            if (first <= 0 || last == long.MaxValue) throw new OverflowException();
        }
        catch (OverflowException) { throw Reject("The repository revision clock is exhausted; controlled recovery is required."); }
        // The exclusive session owns this reservation. It becomes durable only with a record commit;
        // abandoned reservations are never exposed as accepted entity revisions.
        _reservedFloor = last;
        var range = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(range, first);
        BinaryPrimitives.WriteInt64LittleEndian(range.AsSpan(8), last);
        return new() { Payload = range };
    }

    internal static long DecodeClock(byte[] bytes)
    {
        if (bytes.Length != 16) throw new InvalidDataException("Invalid repository revision clock.");
        var floor = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (floor < 0 || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)) != ~floor)
            throw new InvalidDataException("Invalid repository revision clock.");
        return floor;
    }
    internal static byte[] EncodeClock(long floor)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, floor);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), ~floor);
        return bytes;
    }
    private static EncodedStoreRequestRejectedException Reject(string message)
        => new(RemoteErrorCode.InvalidRequest, message);
}
