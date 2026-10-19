using System.Runtime.CompilerServices;

namespace Idb.Diagnostics;

/// <summary>Boundary helpers preserve the original exception, lazy enumeration and cancellation behavior.</summary>
public static class DatabaseTelemetryOperation
{
    public static bool IsEnabled(IDatabaseTelemetry? telemetry)
    { try { return telemetry?.IsEnabled == true; } catch { return false; } }
    public static async ValueTask<T> ObserveAsync<T>(IDatabaseTelemetry? telemetry, DatabaseComponent component, DatabaseOperation operation,
        Func<ValueTask<T>> action, bool log = false, long bytes = 0)
    {
        var scope = DatabaseTelemetryScope.Start(telemetry, component, operation, log);
        try { var result = await action().ConfigureAwait(false); scope.Complete(bytes); return result; }
        catch (Exception exception) { scope.Fail(exception); throw; }
    }
    public static async ValueTask ObserveAsync(IDatabaseTelemetry? telemetry, DatabaseComponent component, DatabaseOperation operation,
        Func<ValueTask> action, bool log = false, long bytes = 0)
    {
        var scope = DatabaseTelemetryScope.Start(telemetry, component, operation, log);
        try { await action().ConfigureAwait(false); scope.Complete(bytes); }
        catch (Exception exception) { scope.Fail(exception); throw; }
    }
    public static async IAsyncEnumerable<T> ObserveStream<T>(IDatabaseTelemetry? telemetry, DatabaseComponent component, DatabaseOperation operation,
        IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = DatabaseTelemetryScope.Start(telemetry, component, operation, log: true);
        var complete = false; var failed = false;
        IAsyncEnumerator<T>? iterator = null;
        try
        {
            try { iterator = source.GetAsyncEnumerator(cancellationToken); }
            catch (Exception exception) { failed = true; scope.Fail(exception); throw; }
            while (true)
            {
                bool more;
                try { more = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception exception) { failed = true; scope.Fail(exception); throw; }
                if (!more) { complete = true; break; }
                T current;
                try { current = iterator.Current; }
                catch (Exception exception) { failed = true; scope.Fail(exception); throw; }
                yield return current;
            }
        }
        finally
        {
            try { if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { if (!failed) { failed = true; scope.Fail(exception); } throw; }
            finally { if (!failed) scope.Complete(outcome: complete ? DatabaseOutcome.Success : DatabaseOutcome.Incomplete); }
        }
    }
}
