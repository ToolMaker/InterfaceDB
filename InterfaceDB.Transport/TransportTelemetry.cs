using Idb.Diagnostics;

namespace Idb.Transport;

internal static class TransportTelemetry
{
    internal static async ValueTask<T> ObserveAsync<T>(IDatabaseTelemetry? telemetry, DatabaseComponent component,
        DatabaseOperation operation, Func<ValueTask<T>> action, bool log = false, long bytes = 0, Guid connectionId = default)
    {
        var scope = DatabaseTelemetryScope.Start(telemetry, component, operation, log).WithConnection(connectionId);
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
        EncodedStoreCommand.ReserveRevisions => DatabaseOperation.Execute,
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
            scope.FailWithReason(exception, Error(remote.Code), Reason(remote.Code), remote.Code == RemoteErrorCode.Busy);
        else if (exception is Idb.Authentication.TokenAcquisitionException token)
            scope.FailWithReason(exception, token.Failure switch {
                Idb.Authentication.TokenAcquisitionFailure.Timeout => DatabaseError.Timeout,
                Idb.Authentication.TokenAcquisitionFailure.Network => DatabaseError.Unavailable,
                _ => DatabaseError.Denied }, TokenReason(token.Failure),
                token.Failure is Idb.Authentication.TokenAcquisitionFailure.Timeout or Idb.Authentication.TokenAcquisitionFailure.Network);
        else scope.FailWithReason(exception, DatabaseTelemetryScope.Classify(exception), exception switch {
            System.Security.Authentication.AuthenticationException => DatabaseFailureReason.Tls,
            EndOfStreamException => DatabaseFailureReason.ConnectionClosed,
            System.Net.Sockets.SocketException => DatabaseFailureReason.Socket,
            IOException { InnerException: System.Net.Sockets.SocketException } => DatabaseFailureReason.Socket,
            _ => DatabaseFailureReason.None });
    }
    internal static DatabaseFailureReason Reason(RemoteErrorCode code) => code switch {
        RemoteErrorCode.InvalidRequest => DatabaseFailureReason.InvalidRequest,
        RemoteErrorCode.IncompatibleManifest => DatabaseFailureReason.IncompatibleManifest,
        RemoteErrorCode.Unauthorized => DatabaseFailureReason.Unauthorized,
        RemoteErrorCode.UnknownStore => DatabaseFailureReason.UnknownStore,
        RemoteErrorCode.Busy => DatabaseFailureReason.Busy,
        RemoteErrorCode.UnsupportedCapability => DatabaseFailureReason.UnsupportedCapability,
        RemoteErrorCode.LimitExceeded => DatabaseFailureReason.LimitExceeded,
        RemoteErrorCode.StorageFailure => DatabaseFailureReason.StorageFailure,
        RemoteErrorCode.OutcomeUnknown => DatabaseFailureReason.OutcomeUnknown,
        RemoteErrorCode.DeadlineExceeded => DatabaseFailureReason.DeadlineExceeded,
        RemoteErrorCode.ProtocolError => DatabaseFailureReason.ProtocolError,
        RemoteErrorCode.AuthenticationRequired => DatabaseFailureReason.AuthenticationRequired,
        RemoteErrorCode.AuthenticationExpired => DatabaseFailureReason.AuthenticationExpired,
        _ => DatabaseFailureReason.None };
    private static DatabaseFailureReason TokenReason(Idb.Authentication.TokenAcquisitionFailure failure) => failure switch {
        Idb.Authentication.TokenAcquisitionFailure.Timeout => DatabaseFailureReason.TokenTimeout,
        Idb.Authentication.TokenAcquisitionFailure.Network => DatabaseFailureReason.TokenNetwork,
        Idb.Authentication.TokenAcquisitionFailure.Tls => DatabaseFailureReason.TokenTls,
        Idb.Authentication.TokenAcquisitionFailure.Credentials => DatabaseFailureReason.TokenCredentials,
        Idb.Authentication.TokenAcquisitionFailure.Metadata => DatabaseFailureReason.TokenMetadata,
        Idb.Authentication.TokenAcquisitionFailure.Http => DatabaseFailureReason.TokenHttp,
        Idb.Authentication.TokenAcquisitionFailure.TokenResponse => DatabaseFailureReason.TokenResponse,
        _ => DatabaseFailureReason.TokenUnavailable };
}
