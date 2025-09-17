namespace TransactionManager.Abstraction.Services
{
    public interface ILocalScopeService
    {
        DirectoryInfo? GetDirectory<TType>();
        Type? GetTypeByDirectory(DirectoryInfo dir);
    }

}
