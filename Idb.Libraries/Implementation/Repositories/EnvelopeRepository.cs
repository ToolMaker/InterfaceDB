using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Default envelope repository that composes reader, writer, and serializer services while
/// maintaining an internal cache for quick lookups. Methods are stubbed for now so we can
/// align on the design before wiring behaviour.
/// </summary>
/// <typeparam name="TContract">Contract type serviced by this repository.</typeparam>
internal sealed class EnvelopeRepository<TContract> : IEnvelopeRepository<TContract>
    where TContract : class, IRepoDto<TContract>
{
    private readonly IObjectReader<TContract> _reader;
    private readonly IObjectWriter<TContract> _writer;
    private readonly IObjectSerializer<TContract> _serializer;

    // Keyed by envelope.CacheKey so callers can rebuild the identifier regardless of instance identity.
    private readonly ConcurrentDictionary<string, IObjectEnvelope<TContract>> _envelopes = new();

    public EnvelopeRepository(
        IObjectReader<TContract> reader,
        IObjectWriter<TContract> writer,
        IObjectSerializer<TContract> serializer)
    {
        _reader = reader;
        _writer = writer;
        _serializer = serializer;
    }

    public IObjectSerializer<TContract> Serializer => _serializer;

    public ValueTask<IObjectEnvelope<TContract>> ReadAsync(CancellationToken cancellationToken = default) =>
        throw new System.NotImplementedException();

    public ValueTask<IObjectEnvelope<TContract>> WriteAsync(
        TContract obj,
        IObjectEnvelope<TContract> envelope,
        CancellationToken cancellationToken = default) =>
        throw new System.NotImplementedException();
}
