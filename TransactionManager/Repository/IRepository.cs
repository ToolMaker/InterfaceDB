namespace TransactionManager.Repository
{
    // IRepository.cs
    public interface IRepository
    {
        ValueTask SaveAsync<TContract>(TContract entity, CancellationToken ct = default);
        ValueTask<TContract?> LoadAsync<TContract>(string id, CancellationToken ct = default);
    }

}
