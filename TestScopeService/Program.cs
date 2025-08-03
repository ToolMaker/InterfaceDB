using Microsoft.Extensions.DependencyInjection;
using TransactionManager.Abstraction.Services;
using TransactionManager.Dtos;
using TransactionManager.Extentions;

namespace TestScopeService
{
    class Program
    {
        static void Main(string[] args)
        {
            var services = new ServiceCollection();
            services.AddScopeStorage();

            var provider = services.BuildServiceProvider();
            var scopeService = provider.GetRequiredService<IScopeService>();
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
