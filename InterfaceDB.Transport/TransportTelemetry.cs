using Idb.Diagnostics;

namespace Idb.Transport;

internal static class TransportTelemetry
{
    internal static async ValueTask<T> ObserveAsync<T>(IDatabaseTelemetry? telemetry, DatabaseComponent component,
        DatabaseOperation operation, Func<ValueTask<T>> action, bool log = false, long bytes = 0)
    {
        var scope = DatabaseTelemetryScope.Start(telemetry, component, operation, log);
        try { var result = await action().ConfigureAwait(false); scope.Complete(bytes); return result; }
        catch (Exception exception) { Fail(scope, exception); throw; }
    }

    internal static DatabaseOperation Operation(EncodedStoreCommand command) => command switch
    {
        EncodedStoreCommand.Get or EncodedStoreCommand.GetMany => DatabaseOperation.Read,
        EncodedStoreCommand.Put => DatabaseOperation.Save,
        EncodedStoreCommand.Delete => DatabaseOperation.Delete,
        EncodedStoreCommand.ApplyBatch => DatabaseOperation.SaveBatch,
        EncodedStoreCommand.Enumerate or EncodedStoreCommand.Changes => DatabaseOperation.Query,
        EncodedStoreCommand.Checkpoint => DatabaseOperation.Checkpoint,
        _ => DatabaseOperation.Unknown
    };
    internal static DatabaseError Error(RemoteErrorCode code) => code switch
    {
        RemoteErrorCode.None => DatabaseError.None,
        RemoteErrorCode.Unauthorized or RemoteErrorCode.AuthenticationRequired or RemoteErrorCode.AuthenticationExpired => DatabaseError.Denied,
        RemoteErrorCode.DeadlineExceeded => DatabaseError.Timeout,
        RemoteErrorCode.OutcomeUnknown => DatabaseError.OutcomeUnknown,
        RemoteErrorCode.InvalidRequest or RemoteErrorCode.ProtocolError => DatabaseError.InvalidInput,
        _ => DatabaseError.RemoteRejected
    };
    internal static void Fail(DatabaseTelemetryScope scope, Exception exception)
    {
        if (exception is RemoteStoreException remote)
            scope.Fail(exception, Error(remote.Code));
        else scope.Fail(exception);
    }
}
