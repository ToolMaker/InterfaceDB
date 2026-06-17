using System.Numerics;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

public sealed class InMemorySequentialBigIntegerEnvelopeKeyFactory<TContract> : IEnvelopeKeyFactory<TContract, BigInteger>
    where TContract : class, IStoredContract<TContract>
{
    private readonly object _gate = new();
    private BigInteger _current;

    public BigInteger CreateKey(TContract instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            _current += BigInteger.One;
            return _current;
        }
    }
}
