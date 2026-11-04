namespace Idb.Commercial.Repositories;

/// <summary>Compatibility adapter; generation is implemented by the free MIT core.</summary>
[Obsolete("Use Idb.Libraries.Implementation.Serialization.MonotonicRevisionTimestampGenerator. Repositories generate revisions in their storage write path.")]
public sealed class MonotonicRevisionTimestampGenerator(TimeProvider? timeProvider = null) : IRevisionTimestampGenerator
{
    private readonly Idb.Libraries.Implementation.Serialization.MonotonicRevisionTimestampGenerator _core = new(timeProvider);
    public long Next(long observedRevisionTimestampUtcMicroseconds) => _core.Next(observedRevisionTimestampUtcMicroseconds);
}
