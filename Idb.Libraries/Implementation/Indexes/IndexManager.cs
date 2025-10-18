using System;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Coordinates index selection and delegates persistence work to the envelope repository.
/// Responsible for wrapping contracts into envelopes when persisting (clearing the contract's envelope reference) and unwrapping them on reads (restoring the reference and clearing the envelope) to avoid circular references.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the manager.</typeparam>
internal sealed class IndexManager<TContract> : IIndexManager<TContract>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IEnvelopeRepository<TContract, FileInfo> _envelopeRepository;

    public IndexManager(IEnvelopeRepository<TContract, FileInfo> envelopeRepository)
    {
        _envelopeRepository = envelopeRepository ?? throw new ArgumentNullException(nameof(envelopeRepository));
    }

    public ValueTask<bool> ExistsAsync(TContract objToCheck, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<TContract> QueryAsync(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(); // TODO: unwrap envelope, reattach to DTO, and clear envelope.ObjectInstance before returning.
    }

    public ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(); // TODO: wrap entity into envelope, set entity.Envelope = null prior to persistence.
    }
}
