using System;
using System.Globalization;
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

            var personV1 = new Person
            {
                Name = "Ada",
                Surname = "Lovelace"
            };

            var writeResultV1 = await repository.SaveAsync<IPerson>(personV1, schemaVersion: 1);
            Console.WriteLine($"Saved {writeResultV1.ObjectType.Name} #{writeResultV1.Id} to {writeResultV1.File.FullName}");

            var personV2 = new Person
            {
                Name = "Ada",
                Surname = "Byron"
            };

            var writeResultV2 = await repository.SaveAsync<IPerson>(personV2, writeResultV1.Id, schemaVersion: (short)(writeResultV1.SchemaVersion + 1));
            Console.WriteLine($"Saved v{writeResultV2.SchemaVersion} for {writeResultV2.Id}");

            var loadedLatest = await repository.LoadAsync<IPerson>(writeResultV1.Id);
            Console.WriteLine($"Latest: {loadedLatest.Value.Name} {loadedLatest.Value.Surname} (v{loadedLatest.SchemaVersion}) [{loadedLatest.Value.GetType().Name}]");

            var loadedV1 = await repository.LoadAsync<IPerson>(writeResultV1.Id, writeResultV1.SchemaVersion);
            Console.WriteLine($"Original: {loadedV1.Value.Name} {loadedV1.Value.Surname} (v{loadedV1.SchemaVersion}) [{loadedV1.Value.GetType().Name}]");

            var hasVersion3 = await repository.ExistsAsync<IPerson>(writeResultV1.Id, schemaVersion: 3);
            Console.WriteLine($"Version 3 exists? {hasVersion3}");

            var personDirectory = scopeService.GetDirectory<IPerson>();
            Console.WriteLine($"IPerson scope directory: {personDirectory?.FullName}");

            Console.WriteLine();
            Console.WriteLine("Stored files:");

            foreach (var versionDir in personDirectory!.EnumerateDirectories()
                                             .Select(d => new { Directory = d, Version = ParseVersion(d.Name) })
                                             .OrderBy(x => x.Version ?? decimal.MinValue))
            {
                Console.WriteLine($"Version folder {versionDir.Directory.Name}:");
                foreach (var file in versionDir.Directory.EnumerateFiles($"*{serializer.FileExtension}", SearchOption.TopDirectoryOnly))
                {
                    Console.WriteLine($" - {file.Name}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }

        private static decimal? ParseVersion(string name)
        {
            if (decimal.TryParse(name, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return null;
        }
    }
}
