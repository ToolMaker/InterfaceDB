namespace TransactionManager.Abstraction.Services
{
    public interface IScopeService
    {
        IEnumerable<DirectoryInfo> GetScopeDirectories<T>();
        Type? GetScopeType(DirectoryInfo path);
    }

}
