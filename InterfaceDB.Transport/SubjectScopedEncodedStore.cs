using System.Security.Cryptography;
using System.Text;
using Idb.Authentication;

namespace Idb.Transport;

/// <summary>
/// Borrowed view: the server derives a partition solely from validated issuer/subject claims.
/// No client key, cursor, alias, payload or claimed parent identifier can escape that partition.
/// </summary>
internal sealed class SubjectScopedEncodedStore : IEncodedEnvelopeStore
{
    internal const int PrefixBytes = 71;
    private readonly IEncodedEnvelopeStore _inner;
    private readonly string _prefix;

    internal SubjectScopedEncodedStore(IEncodedEnvelopeStore inner, AuthenticatedIdentity identity)
    {
        _inner = inner;
        _prefix = GetPrefix(identity);
    }

    internal static string GetPrefix(AuthenticatedIdentity identity)
    {
        if (!HasSubject(identity)) throw new ArgumentException("A unique validated issuer and subject are required.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(identity.Principal.FindFirst("iss")!.Value);
            writer.Write(identity.Principal.FindFirst("sub")!.Value);
        }
        return "idbp1." + Convert.ToHexString(SHA256.HashData(bytes.ToArray())) + ".";
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

    internal static bool HasSubject(AuthenticatedIdentity identity) =>
        identity.Principal.Identity?.IsAuthenticated == true
        && ValidClaim(identity, "iss") && ValidClaim(identity, "sub");

    private static bool ValidClaim(AuthenticatedIdentity identity, string type)
    {
        var claims = identity.Principal.FindAll(type).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value)
            && Encoding.UTF8.GetByteCount(claims[0].Value) <= 4096;
    }

    public EncodedStoreCapabilities Capabilities => _inner.Capabilities | EncodedStoreCapabilities.SubjectIsolation;
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
