namespace TransactionManager.Abstraction.Services
{
    public interface IScopeService
    {
        DirectoryInfo? GetDirectory<TType>();
        Type? GetTypeByDirectory(DirectoryInfo dir);
    }

}
