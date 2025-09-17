using TransactionManager.Attributes;

namespace TransactionManager.Dtos
{
    [StorageScope]
    public interface IPerson
    {
        string Name { get; }
        string Surname { get; }
    }
}
