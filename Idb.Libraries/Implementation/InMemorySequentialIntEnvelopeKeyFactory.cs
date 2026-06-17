using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

public sealed class InMemorySequentialIntEnvelopeKeyFactory<TContract> : IEnvelopeKeyFactory<TContract, int>
    where TContract : class, IStoredContract<TContract>
{
    private int _current;

    public int CreateKey(TContract instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Interlocked.Increment(ref _current);
    }
}
