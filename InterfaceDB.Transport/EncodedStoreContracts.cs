namespace Idb.Transport;

[Flags]
public enum EncodedStoreCapabilities
{
    None = 0, Read = 1, Write = 2, Delete = 4, GetMany = 8, AtomicBatch = 16,
    Enumerate = 32, Checkpoint = 64, DurableCommits = 128,
    All = Read | Write | Delete | GetMany | AtomicBatch | Enumerate | Checkpoint | DurableCommits
}

public enum EncodedStoreCommand { Get = 1, Put, Delete, GetMany, ApplyBatch, Enumerate, Checkpoint, Ping, Changes }

/// <summary>A null payload represents a deletion; an empty payload is a valid stored value.</summary>
public sealed record EncodedMutation(string Key, byte[]? Payload);
public sealed record EncodedRecord(string Key, byte[] Payload);

/// <summary>Operations address opaque storage keys, never server filesystem paths.</summary>
public sealed record EncodedStoreRequest
{
    public required EncodedStoreCommand Command { get; init; }
    internal string Partition { get; init; } = "";
    public string? Key { get; init; }
    public byte[]? Payload { get; init; }
    public string[]? Keys { get; init; }
    public EncodedMutation[]? Mutations { get; init; }
    public string? AfterKey { get; init; }
    public int PageSize { get; init; }

    public bool IsMutation => Command is EncodedStoreCommand.Put or EncodedStoreCommand.Delete
        or EncodedStoreCommand.ApplyBatch or EncodedStoreCommand.Checkpoint;

    public EncodedStoreCapabilities RequiredCapability => Command switch
    {
        EncodedStoreCommand.Get => EncodedStoreCapabilities.Read,
        EncodedStoreCommand.Put => EncodedStoreCapabilities.Write,
        EncodedStoreCommand.Delete => EncodedStoreCapabilities.Delete,
        EncodedStoreCommand.GetMany => EncodedStoreCapabilities.GetMany,
        EncodedStoreCommand.ApplyBatch => EncodedStoreCapabilities.AtomicBatch
            | (Mutations?.Any(mutation => mutation is { Payload: not null }) == true
                ? EncodedStoreCapabilities.Write : EncodedStoreCapabilities.None)
            | (Mutations?.Any(mutation => mutation is { Payload: null }) == true
                ? EncodedStoreCapabilities.Delete : EncodedStoreCapabilities.None),
        EncodedStoreCommand.Enumerate or EncodedStoreCommand.Changes => EncodedStoreCapabilities.Enumerate,
        EncodedStoreCommand.Checkpoint => EncodedStoreCapabilities.Checkpoint,
        EncodedStoreCommand.Ping => EncodedStoreCapabilities.None,
        _ => throw new ArgumentOutOfRangeException(nameof(Command))
    };
}

public sealed record EncodedStoreResponse
{
    public bool Found { get; init; }
    public byte[]? Payload { get; init; }
    public EncodedRecord[]? Records { get; init; }
    public string? NextAfterKey { get; init; }
    public int AffectedCount { get; init; }
}

/// <summary>
/// Transport-independent opaque-storage seam. Implementations own their resources. Successful mutations
/// must satisfy the advertised DurableCommits capability before returning. There is no implicit retry.
/// </summary>
public interface IEncodedEnvelopeStore : IAsyncDisposable
{
    EncodedStoreCapabilities Capabilities { get; }
    ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional application endpoint that needs the authenticated caller when producing a response,
/// for example a signed client setup. The host supplies this context; it is never decoded from payloads.
/// Normal storage providers do not need to implement this interface.
/// </summary>
public interface IAccessContextEncodedStore : IEncodedEnvelopeStore
{
    ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, DataAccessContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class AccessContextEncodedStore(IAccessContextEncodedStore inner, DataAccessContext context) : IEncodedEnvelopeStore
{
    public EncodedStoreCapabilities Capabilities => inner.Capabilities;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        => inner.ExecuteAsync(request, context with { RequiredCapabilities = request.RequiredCapability }, cancellationToken);
}

public static class EncodedEnvelopeStoreExtensions
{
    public static async ValueTask<byte[]?> GetAsync(this IEncodedEnvelopeStore store, string key, CancellationToken cancellationToken = default)
        => (await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = key }, cancellationToken).ConfigureAwait(false)).Payload;

    public static async ValueTask PutAsync(this IEncodedEnvelopeStore store, string key, byte[] payload, CancellationToken cancellationToken = default)
        => _ = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Put, Key = key, Payload = payload }, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<bool> DeleteAsync(this IEncodedEnvelopeStore store, string key, CancellationToken cancellationToken = default)
        => (await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Delete, Key = key }, cancellationToken).ConfigureAwait(false)).Found;

    public static ValueTask<EncodedStoreResponse> ApplyBatchAsync(this IEncodedEnvelopeStore store, EncodedMutation[] mutations, CancellationToken cancellationToken = default)
        => store.ExecuteAsync(new() { Command = EncodedStoreCommand.ApplyBatch, Mutations = mutations }, cancellationToken);

    public static ValueTask<EncodedStoreResponse> EnumerateAsync(this IEncodedEnvelopeStore store, int pageSize, string? afterKey = null, CancellationToken cancellationToken = default)
        => store.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, PageSize = pageSize, AfterKey = afterKey }, cancellationToken);
}
