using TestScopeService.Abstrations.Dtos;

namespace TestScopeService.Implementations.Dtos
{
    public class Person : IPerson
    {
        public string Name { get; set; } = string.Empty;

        public string Surname { get; set; } = string.Empty;

        public int Age { get; set; }
    }
}
