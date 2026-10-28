namespace Idb.Transport;

internal static partial class TransportValidation
{
    internal static void Response(EncodedStoreRequest request, EncodedStoreResponse response)
    {
        if (response is null || response.Records?.Any(record => record is null
            || record.Key is null || record.Payload is null) == true)
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid storage response shape.");
        bool invalid = response.AffectedCount < 0;
        switch (request.Command)
        {
            case EncodedStoreCommand.Changes:
                invalid |= response.Records is null || response.Records.Length > request.PageSize
                    || response.Payload is null || response.Payload.Length > 100
                    || response.AffectedCount != 0 || (response.Found && (response.NextAfterKey is not null || response.Records?.Length != 0))
                    || response.Records?.Any(record => record.Payload.Length != 0) == true;
                break;
            case EncodedStoreCommand.Get:
                invalid |= response.Found != (response.Payload is not null) || response.Records is not null
                    || response.NextAfterKey is not null || response.AffectedCount != 0;
                break;
            case EncodedStoreCommand.GetMany:
            case EncodedStoreCommand.Enumerate:
                invalid |= response.Records is null || response.Payload is not null || response.Found || response.AffectedCount != 0;
                if (request.Command == EncodedStoreCommand.GetMany)
                {
                    invalid |= response.NextAfterKey is not null || response.Records?.Length > request.Keys!.Length;
                    if (response.Records is not null)
                    {
                        var remaining = request.Keys!.GroupBy(key => key, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                        foreach (var record in response.Records)
                        {
                            if (!remaining.TryGetValue(record.Key, out var count) || count == 0) invalid = true;
                            else remaining[record.Key] = count - 1;
                        }
                    }
                }
                else
                {
                    invalid |= response.Records?.Length > request.PageSize;
                    var previous = request.AfterKey;
                    foreach (var record in response.Records ?? [])
                    {
                        if (previous is not null && StringComparer.Ordinal.Compare(previous, record.Key) >= 0) invalid = true;
                        previous = record.Key;
                    }
                    if (response.NextAfterKey is not null)
                        invalid |= response.Records is not { Length: > 0 }
                            || response.NextAfterKey != response.Records[^1].Key;
                }
                break;
            case EncodedStoreCommand.Put:
                invalid |= response.Found || response.AffectedCount != 1;
                goto default;
            case EncodedStoreCommand.Delete:
                invalid |= response.AffectedCount != (response.Found ? 1 : 0);
                goto default;
            case EncodedStoreCommand.ApplyBatch:
                invalid |= response.Found || response.AffectedCount > request.Mutations!.Length
                    || response.AffectedCount < request.Mutations.Count(mutation => mutation.Payload is not null);
                goto default;
            case EncodedStoreCommand.Checkpoint:
            case EncodedStoreCommand.Ping:
                invalid |= response.Found || response.AffectedCount != 0;
                goto default;
            default:
                invalid |= response.Payload is not null || response.Records is not null || response.NextAfterKey is not null;
                break;
        }
        if (invalid) throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid storage response shape.");
    }

}
