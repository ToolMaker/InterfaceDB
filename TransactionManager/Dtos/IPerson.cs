namespace TransactionManager.Dtos
{
    using TransactionManager.Attributes;
    using TransactionManager.Services;

    [StorageScope(VersionTag = "2.0", FileNameFactory = typeof(PersonFileNameFactory))]
    public interface IPerson
    {
        string Name { get; }
        string Surname { get; }
        int Age { get; }
    }
}
