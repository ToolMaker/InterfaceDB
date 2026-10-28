
namespace Idb.Transport;

/// <summary>
/// Borrowed view: the server derives a partition solely from the server-approved access policy.
/// No client key, cursor, alias, payload or claimed parent identifier can escape that partition.
/// </summary>
internal sealed class ScopedEncodedStore : IEncodedEnvelopeStore
{
    internal const int PrefixBytes = 71;
    internal const string ChangeJournalPrefix = "\u0001idb-changes:";
    private readonly IEncodedEnvelopeStore _inner;
    private readonly string _prefix;

    internal ScopedEncodedStore(IEncodedEnvelopeStore inner, DataAccessScope scope)
    {
        _inner = inner;
        _prefix = scope.Partition;
    }

    internal static bool TryGetPrefix(string physicalKey, out string prefix)
    {
        if (HasPartitionPrefix(physicalKey))
        {
            prefix = physicalKey[..PrefixBytes];
            return true;
        }
        prefix = "";
        return false;
    }

    private static bool HasPartitionPrefix(string key) => key.StartsWith("idbp1.", StringComparison.Ordinal)
        && key.Length > PrefixBytes && key[PrefixBytes - 1] == '.'
        && key.AsSpan(6, 64).IndexOfAnyExcept("0123456789ABCDEF") < 0;

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
        if (_prefix.Length == 0)
            return await ExecuteUnpartitionedAsync(request, cancellationToken).ConfigureAwait(false);
        request = request with { UnpartitionedOnly = false };
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

    // This view is the transport boundary for every provider. The underlying store remains
    // available to trusted offline migrations that must enumerate all physical partitions.
    internal static bool IsUnpartitioned(string key) => !HasPartitionPrefix(key)
        && !key.StartsWith(ChangeJournalPrefix, StringComparison.Ordinal);

    private async ValueTask<EncodedStoreResponse> ExecuteUnpartitionedAsync(
        EncodedStoreRequest request, CancellationToken token)
    {
        request = request with { Partition = "", UnpartitionedOnly = request.Command == EncodedStoreCommand.Enumerate,
            Keys = request.Keys?.ToArray(), Mutations = request.Mutations?.ToArray() };
        if ((request.Key is { } key && !IsUnpartitioned(key))
            || request.Keys?.Any(key => !IsUnpartitioned(key)) == true
            || request.Mutations?.Any(mutation => !IsUnpartitioned(mutation.Key)) == true)
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.InvalidRequest,
                "The partition and change-journal namespaces are reserved.");

        if (request.Command == EncodedStoreCommand.Enumerate)
        {
            string? after = request.AfterKey;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var response = await _inner.ExecuteAsync(request with { AfterKey = after }, token).ConfigureAwait(false);
                // Validate before filtering: filtering must not conceal a malformed provider page
                // or allow a non-advancing continuation to keep the session in this loop.
                TransportValidation.Response(request with { AfterKey = after }, response);
                var records = response.Records!.Where(record => IsUnpartitioned(record.Key)).ToArray();
                if (records.Length > 0 || response.NextAfterKey is null)
                    return response with { Records = records,
                        NextAfterKey = records.Length > 0 && response.NextAfterKey is not null ? records[^1].Key : null };
                after = response.NextAfterKey;
            }
        }

        var result = await _inner.ExecuteAsync(request, token).ConfigureAwait(false);
        if (result?.Records?.Any(record => !IsUnpartitioned(record.Key)) == true)
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError,
                "The provider returned a record outside the approved data scope.");
        return result!;
    }
}
