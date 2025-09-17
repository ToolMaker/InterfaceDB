using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Dtos;
using TransactionManager.Extentions;
using TransactionManager.Repository;

namespace TestScopeService
{
    internal class Program
    {
        private static async Task Main()
        {
            var storageRoot = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "demo-storage"));

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSimpleConsole());

            services.AddLocalScopeStorage(options =>
            {
                options.RootDirectory = storageRoot;
                options.AssembliesToScan = new[] { typeof(IPerson).Assembly };
            });

            services.AddJsonSerializer();
            services.AddDefaultObjectWriterAndReader();

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            var scopeService = scope.ServiceProvider.GetRequiredService<ILocalScopeService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IObjectSerializer>();

            var descriptor = scopeService.GetScope<IPerson>()
                            ?? throw new InvalidOperationException("IPerson scope not registered.");

            Console.WriteLine($"Version tag for IPerson: '{descriptor.VersionTag}'");

            var person = new Person
            {
                Name = "Ada",
                Surname = "Lovelace"
            };

            var writeResult = await repository.SaveAsync<IPerson>(person);
            Console.WriteLine($"Saved {writeResult.ObjectType.Name} #{writeResult.Id} to {writeResult.File.FullName}");

            var loaded = await repository.LoadAsync<IPerson>(writeResult.Id);
            Console.WriteLine($"Loaded: {loaded.Value.Name} {loaded.Value.Surname} (tag '{loaded.Envelope.VersionTag}') [{loaded.Value.GetType().Name}]");

            Console.WriteLine();
            Console.WriteLine("Stored files:");

            foreach (var versionDir in descriptor.Directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                Console.WriteLine($"Version folder {versionDir.Name}:");
                foreach (var file in versionDir.EnumerateFiles($"*{serializer.FileExtension}", SearchOption.TopDirectoryOnly))
                {
                    Console.WriteLine($" - {file.Name}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Tip: Update the IPerson [StorageScope(VersionTag = ...)] attribute and rerun to create a new version folder.");
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
