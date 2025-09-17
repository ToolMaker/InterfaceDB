namespace TransactionManager.Dtos
{
    using TransactionManager.Attributes;

    [StorageScope(VersionTag = "1.0")]
    public interface IPerson
    {
        string Name { get; }
        string Surname { get; }
    }
}
