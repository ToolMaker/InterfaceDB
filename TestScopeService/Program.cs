using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestScopeService.Abstrations.Dtos;
using TestScopeService.Implementations.Dtos;
using TransactionManager.Abstraction.Repository;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Implementation.Extentions;

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
                Surname = "Lovelace",
                Age = 28
            };

            var personWrite = await repository.SaveAsync<IPerson>(person);
            Console.WriteLine($"Saved Person #{personWrite.Id} to {personWrite.File.FullName}");

            var student = new Student
            {
                Name = "Grace",
                Surname = "Hopper",
                Age = 21,
                StudentId = "CS-2025"
            };

            var studentWrite = await repository.SaveAsync<IPerson>(student, student.StudentId);
            Console.WriteLine($"Saved Student #{studentWrite.Id} to {studentWrite.File.FullName}");

            var loadedPerson = await repository.LoadAsync<IPerson>(personWrite.Id);
            Console.WriteLine($"Loaded Person: {loadedPerson.Value.Name} {loadedPerson.Value.Surname} (tag '{loadedPerson.Envelope.VersionTag}') [{loadedPerson.Value.GetType().Name}]");

            var loadedStudent = await repository.LoadAsync<IPerson>(studentWrite.Id);
            Student test = (Student)loadedStudent.Value;
            Console.WriteLine($"Loaded Student: {loadedStudent.Value.Name} {loadedStudent.Value.Surname} {test.StudentId} (tag '{loadedStudent.Envelope.VersionTag}') [{loadedStudent.Value.GetType().Name}]");

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
