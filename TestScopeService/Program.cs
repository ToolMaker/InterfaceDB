using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
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
            directories.AddRange(scopeService.GetScopeDirectories<Person>());
            //directories.AddRange(scopeService.GetScopeDirectories<Alien>().ToList());
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
