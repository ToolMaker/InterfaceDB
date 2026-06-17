using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

public sealed class InMemorySequentialLongEnvelopeKeyFactory<TContract> : IEnvelopeKeyFactory<TContract, long>
    where TContract : class, IStoredContract<TContract>
{
    private long _current;

    public long CreateKey(TContract instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Interlocked.Increment(ref _current);
    }
}
