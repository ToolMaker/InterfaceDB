
namespace Idb.Transport;

/// <summary>
/// Borrowed view: the server derives a partition solely from the server-approved access policy.
/// No client key, cursor, alias, payload or claimed parent identifier can escape that partition.
/// </summary>
internal sealed class ScopedEncodedStore : IEncodedEnvelopeStore
{
    internal const int PrefixBytes = 71;
    private readonly IEncodedEnvelopeStore _inner;
    private readonly string _prefix;

    internal ScopedEncodedStore(IEncodedEnvelopeStore inner, DataAccessScope scope)
    {
        _inner = inner;
        _prefix = scope.Partition;
        if (_prefix.Length == 0) throw new ArgumentException("A partitioned scope is required.");
    }

    internal static bool TryGetPrefix(string physicalKey, out string prefix)
    {
        if (physicalKey.StartsWith("idbp1.", StringComparison.Ordinal)
            && physicalKey.Length > PrefixBytes
            && physicalKey[PrefixBytes - 1] == '.'
            && physicalKey.AsSpan(6, 64).IndexOfAnyExcept("0123456789ABCDEF") < 0)
        {
            prefix = physicalKey[..PrefixBytes];
            return true;
        }
        prefix = "";
        return false;
    }

    public EncodedStoreCapabilities Capabilities => _inner.Capabilities;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Scope(string key) => _prefix + key;
    private string Unscope(string key)
    {
        if (!key.StartsWith(_prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The physical provider returned a record outside the authenticated partition.");
        return key[_prefix.Length..];
    }

    public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
    {
        // The host has already applied frame, operation-shape and capability validation.
        if (request.Command == EncodedStoreCommand.Enumerate)
        {
            var response = await _inner.ExecuteAsync(request with
            {
                Partition = _prefix,
                AfterKey = request.AfterKey is null ? _prefix : Scope(request.AfterKey)
            }, cancellationToken).ConfigureAwait(false);
            var records = (response.Records ?? []).TakeWhile(record =>
                record.Key.StartsWith(_prefix, StringComparison.Ordinal))
                .Select(record => record with { Key = Unscope(record.Key) }).ToArray();
            return response with
            {
                Records = records,
                NextAfterKey = records.Length > 0 && response.NextAfterKey is { } next
                    && next.StartsWith(_prefix, StringComparison.Ordinal) ? Unscope(next) : null
            };
        }
        var mapped = request with
        {
            Partition = _prefix,
            Key = request.Key is null ? null : Scope(request.Key),
            Keys = request.Keys?.Select(Scope).ToArray(),
            Mutations = request.Mutations?.Select(mutation => mutation with { Key = Scope(mutation.Key) }).ToArray()
        };
        var result = await _inner.ExecuteAsync(mapped, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Records = result.Records?.Select(record => record with { Key = Unscope(record.Key) }).ToArray()
        };
    }
}
