using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Services.Serialization;
namespace Idb.Libraries.Implementation.Serialization;

// Runtime publication order only, never a public version token or persisted field.
internal static class EnvelopeCommitOrder
{
    private sealed record Order(long Value);
    private static readonly ConditionalWeakTable<object, Order> Orders = new();
    private static long _next;
    internal static long Get(object? envelope) => envelope is IEnvelopeRuntimeState state
        ? state.CommitOrder
        : envelope is not null && Orders.TryGetValue(envelope, out var order) ? order.Value : 0;
    internal static void Stamp(object envelope)
    {
        if (envelope is IEnvelopeRuntimeState state) state.CommitOrder = Interlocked.Increment(ref _next);
        else lock (Orders) { Orders.Remove(envelope); Orders.Add(envelope, new(Interlocked.Increment(ref _next))); }
    }
    internal static void Copy(object source, object target)
    {
        var order = Get(source);
        if (order == 0) return;
        if (target is IEnvelopeRuntimeState state) state.CommitOrder = order;
        else lock (Orders) { Orders.Remove(target); Orders.Add(target, new(order)); }
    }
}
