using TransactionManager.Attributes;

namespace TransactionManager.Dtos
{
    [StorageScope]
    public class Person : IPerson
    {
        public string Name { get; set; } = string.Empty;

        public string Surname { get; set; } = string.Empty;
    }
}
