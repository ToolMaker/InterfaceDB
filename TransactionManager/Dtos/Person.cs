using TransactionManager.Attributes.TransactionManager.Attributes;

namespace TransactionManager.Dtos
{
    [StorageScope()]
    public class Person : IPerson
    {
        public string Name { get; set; }

        public string Surname { get; set; }
    }
}
