using Microsoft.Extensions.DependencyInjection;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Dtos;
using TransactionManager.Extentions;
using TransactionManager.Serializers;
using TransactionManager.Serializers.TransactionManager.Serialization;

namespace TestScopeService
{
    class Program
    {
        static void Main(string[] args)
        {
            var services = new ServiceCollection();
            services.AddScopeStorage();
            services.AddSingleton<IPerson, Person>();
            services.AddScoped<Person>();
            services.AddScoped<IObjectSerializer, MessagePackObjectSerializer>();

            var provider = services.BuildServiceProvider();
            var scopeService = provider.GetRequiredService<ILocalScopeService>();
            var objectSerializer = provider.GetRequiredService<IObjectSerializer>();

            Person person = new Person
            {
                Name = "John",
                Surname = "Doe"
            };

            var bytes = objectSerializer.Serialize(person);
            Console.WriteLine("Serialized Person:");
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
            Person deserializedPerson = objectSerializer.Deserialize<Person>(bytes);
            Console.WriteLine("Deserialized Person:");
            Console.WriteLine($"Name: {deserializedPerson.Name}, Surname: {deserializedPerson.Surname}");
            List<DirectoryInfo> directories = new List<DirectoryInfo>();
            directories.Add(scopeService.GetDirectory<Person>());
            Console.WriteLine("Scope Directories for Person:");
            foreach (var dir in directories)
            {
                Console.WriteLine("- " + dir.FullName);
            }

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
