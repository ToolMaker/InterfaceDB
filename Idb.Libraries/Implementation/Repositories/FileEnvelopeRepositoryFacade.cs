using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Non-owning compatibility view of the file-provider singleton. Returning the disposable
/// repository itself from both keyed and unkeyed registrations would make both registrations
/// disposal owners, with ordering dependent on which alias an application resolves first.
/// </summary>
internal sealed class FileEnvelopeRepositoryFacade<TContract> :
    IEnvelopeRepository<TContract>,
    IBatchedEnvelopeRepository<TContract>,
    IEnvelopeRepositoryInitializationStream<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _repository;
    private readonly IEnvelopeRepositoryInitializationStream<TContract> _initializationStream;

    public FileEnvelopeRepositoryFacade(IEnvelopeRepository<TContract> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _initializationStream = repository as IEnvelopeRepositoryInitializationStream<TContract>
            ?? throw new InvalidOperationException("File repository has no initialization stream.");
    }

    public IReaderAndWriter<TContract> ReaderAndWriter => _repository.ReaderAndWriter;

    public bool Exists(string storageKey) => _repository.Exists(storageKey);

    public bool TryGet(
        string storageKey,
        out IReadOnlyList<IObjectEnvelope<TContract>> envelopes) =>
        _repository.TryGet(storageKey, out envelopes);

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _repository.InitializeAsync(cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(
        CancellationToken cancellationToken = default) =>
        _initializationStream.InitializeAndEnumerateAsync(cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(
        string storageKey,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(storageKey, cancellationToken);

    public ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default) =>
        _repository.GetPhysicalAsync(physicalStorageKey, cancellationToken);

    public async ValueTask<IReadOnlyList<IObjectEnvelope<TContract>>> GetPhysicalManyAsync(
        IReadOnlyList<string> physicalStorageKeys,
        CancellationToken cancellationToken = default)
    {
        if (_repository is IBatchedEnvelopeRepository<TContract> batched)
        {
            return await batched.GetPhysicalManyAsync(physicalStorageKeys, cancellationToken).ConfigureAwait(false);
        }

        ArgumentNullException.ThrowIfNull(physicalStorageKeys);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<IObjectEnvelope<TContract>>(physicalStorageKeys.Count);
        foreach (var key in physicalStorageKeys)
        {
            var envelope = await _repository.GetPhysicalAsync(key, cancellationToken).ConfigureAwait(false);
            if (envelope is not null)
            {
                results.Add(envelope);
            }
        }
        return results;
    }

    public IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        CancellationToken cancellationToken = default) =>
        _repository.EnumerateAsync(cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract =>
        _repository.EnumerateByImplementationTypeAsync<TImplementation>(cancellationToken);

    public ValueTask<TContract> WriteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(contractInstance, cancellationToken);

    public ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
        IEnumerable<TContract> contractInstances,
        CancellationToken cancellationToken = default) =>
        _repository.WriteManyAsync(contractInstances, cancellationToken);

    public ValueTask<bool> DeleteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(contractInstance, cancellationToken);

    public IContractSubscription SubscribeToAdded(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToAdded(handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToRemoved(handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToStored(handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToDeleted(handler, keepAlive);

    public ValueTask ContractAdded(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractAdded(@event, cancellationToken);

    public ValueTask ContractRemoved(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractRemoved(@event, cancellationToken);

    public ValueTask ContractStored(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractStored(@event, cancellationToken);

    public ValueTask ContractDeleted(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractDeleted(@event, cancellationToken);
}
