namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Application resolvers run inside provider guards; nested writes would deadlock.
/// Disabled strict writes perform only the zero-valued counter branch.</summary>
public sealed class EnvelopeResolverScope : IDisposable
{
    private static readonly AsyncLocal<EnvelopeResolverScope?> Current = new();
    private static int _active;
    private int _disposed;
    private readonly EnvelopeResolverScope? _previous;
    public EnvelopeResolverScope()
    {
        _previous = Current.Value;
        Current.Value = this;
        Interlocked.Increment(ref _active);
    }
    public static void ThrowIfActive()
    {
        if (Volatile.Read(ref _active) != 0 && Current.Value is { _disposed: 0 })
            throw new InvalidOperationException("A conflict resolver or validator cannot enter another storage write, edit acquisition or maintenance operation.");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Current.Value = _previous;
        Interlocked.Decrement(ref _active);
    }
}
