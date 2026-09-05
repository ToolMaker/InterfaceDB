using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

internal static class EnvelopeBatchEnumeration
{
    internal static async IAsyncEnumerable<IObjectEnvelope<TContract>> ReadAsync<TContract>(
        IEnvelopeRepository<TContract> repository,
        IEnumerable<string> keys,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TContract : class, IStoredContract<TContract>
    {
        using var iterator = keys.GetEnumerator();
        // Do no speculative I/O before yielding the first candidate. SearchFirst can stop here.
        if (!iterator.MoveNext())
        {
            yield break;
        }

        var first = await repository.GetPhysicalAsync(iterator.Current, cancellationToken).ConfigureAwait(false);
        if (first is not null)
        {
            yield return first;
        }

        if (repository is not IBatchedEnvelopeRepository<TContract> batchReader)
        {
            while (iterator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = await repository.GetPhysicalAsync(iterator.Current, cancellationToken).ConfigureAwait(false);
                if (envelope is not null)
                {
                    yield return envelope;
                }
            }

            yield break;
        }

        const int windowSize = 64;
        var window = new List<string>(windowSize);
        while (true)
        {
            window.Clear();
            while (window.Count < windowSize && iterator.MoveNext())
            {
                window.Add(iterator.Current);
            }

            if (window.Count == 0)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var envelopes = await batchReader.GetPhysicalManyAsync(window, cancellationToken).ConfigureAwait(false);
            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
        }
    }
}
