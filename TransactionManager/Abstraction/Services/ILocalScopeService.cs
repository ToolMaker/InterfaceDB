using TransactionManager.Abstraction.Dtos;


namespace TransactionManager.Abstraction.Services
{
    public interface ILocalScopeService
    {
        DirectoryInfo? GetDirectory<TContract>();
        IScopeDescriptor? GetScope<TContract>();
        IScopeDescriptor? GetScope(Type contractType);
        Type? GetContractByDirectory(DirectoryInfo dir);
        IEnumerable<IScopeDescriptor> GetScopes();
    }
}
