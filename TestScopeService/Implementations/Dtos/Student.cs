using TestScopeService.Abstrations.Dtos;

namespace TestScopeService.Implementations.Dtos
{
    public sealed class Student : IPerson
    {
        public string Name { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public int Age { get; set; }
        public string StudentId { get; set; } = string.Empty;
    }
}
