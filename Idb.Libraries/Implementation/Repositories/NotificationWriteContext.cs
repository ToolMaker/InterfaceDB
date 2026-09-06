namespace Idb.Libraries.Implementation.Repositories;

/// <summary>In-process causal provenance only. Never stored in an entity or envelope and never
/// treated as authentication, a remote operation id or a durable delivery guarantee.</summary>
internal static class NotificationWriteContext
{
    private static readonly AsyncLocal<Origin?> Ambient = new();
    internal static readonly AsyncLocal<object?> ExecutingSubscription = new();
    internal static readonly AsyncLocal<bool> Comparing = new();
    internal static Origin? Current => Ambient.Value;

    internal sealed record Origin(Guid Subscriber, Origin? Parent)
    {
        internal int Depth { get; } = (Parent?.Depth ?? 0) + 1;
        internal bool Contains(Guid subscriber)
        {
            for (Origin? item = this; item is not null; item = item.Parent)
                if (item.Subscriber == subscriber) return true;
            return false;
        }
    }

    internal static IDisposable Enter(Guid subscriber, Origin? parent) => new Scope(new Origin(subscriber, parent));

    private sealed class Scope : IDisposable
    {
        private readonly Origin? _previous = Ambient.Value;
        private readonly Origin _current;
        private bool _disposed;
        internal Scope(Origin current) { _current = current; Ambient.Value = current; }
        public void Dispose()
        {
            if (_disposed) return;
            if (!ReferenceEquals(Ambient.Value, _current))
                throw new InvalidOperationException("Notification write scopes must be disposed in nesting order in their originating async flow.");
            Ambient.Value = _previous; _disposed = true;
        }
    }
}
