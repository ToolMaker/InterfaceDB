namespace TestScopeService.Abstrations.Dtos
{
    using TestScopeService.Implementations.Factories;
    using TransactionManager.Abstraction.Attributes;

    [StorageScope(VersionTag = "2.0", FileNameFactory = typeof(PersonFileNameFactory))]
    public interface IPerson
    {
        string Name { get; }
        string Surname { get; }
        int Age { get; }
    }
}
