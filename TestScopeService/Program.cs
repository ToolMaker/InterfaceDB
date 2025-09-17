using System;
using System.IO;
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
                options.AssembliesToScan = new[] { typeof(Person).Assembly };
            });

            services.AddJsonSerializer();
            services.AddDefaultObjectWriterAndReader();

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            var scopeService = scope.ServiceProvider.GetRequiredService<ILocalScopeService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IObjectSerializer>();

            var personV1 = new Person
            {
                Name = "Ada",
                Surname = "Lovelace"
            };

            var writeResultV1 = await repository.SaveAsync(personV1, schemaVersion: 1);
            Console.WriteLine($"Saved {writeResultV1.ObjectType.Name} #{writeResultV1.Id} to {writeResultV1.File.FullName}");

            var personV2 = new Person
            {
                Name = "Ada",
                Surname = "Byron"
            };

            var writeResultV2 = await repository.SaveAsync(personV2, writeResultV1.Id, schemaVersion: (short)(writeResultV1.SchemaVersion + 1));
            Console.WriteLine($"Saved v{writeResultV2.SchemaVersion} for {writeResultV2.Id}");

            var loadedLatest = await repository.LoadAsync<IPerson>(writeResultV1.Id);
            Console.WriteLine($"Latest: {loadedLatest.Value.Name} {loadedLatest.Value.Surname} (v{loadedLatest.SchemaVersion})");

            var loadedV1 = await repository.LoadAsync<IPerson>(writeResultV1.Id, writeResultV1.SchemaVersion);
            Console.WriteLine($"Original: {loadedV1.Value.Name} {loadedV1.Value.Surname} (v{loadedV1.SchemaVersion})");

            var hasVersion3 = await repository.ExistsAsync<Person>(writeResultV1.Id, schemaVersion: 3);
            Console.WriteLine($"Version 3 exists? {hasVersion3}");

            var personDirectory = scopeService.GetDirectory<Person>();
            Console.WriteLine($"Person scope directory: {personDirectory?.FullName}");

            Console.WriteLine();
            Console.WriteLine("Stored files:");

            var pattern = $"*{serializer.FileExtension}";
            foreach (var file in new DirectoryInfo(personDirectory!.FullName).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                Console.WriteLine($" - {file.Name}");
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
    }
}
