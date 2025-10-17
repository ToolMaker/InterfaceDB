using Idb.Libraries.Abstractions.Dtos;


namespace Idb.Libraries.Abstractions.Services.Repository.FileRepository
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

