using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;
using IdbDemoStorage.Options;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Factories;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Migrations;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using PersonStorage = IdbDemoStorage.Storage.Person.v1_0_0;
using IMigrationContract = Idb.Libraries.Abstraction.Services.Migrations.IMigration;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var personOptions = new PersonOptions();
var absoluteDataRoot = EnsureDataRoot(personOptions.DataRoot);

var services = new ServiceCollection();
services.AddRepository<ContractPerson.IPerson>(personOptions, MigrationStrategyType.None);

using var serviceProvider = services.BuildServiceProvider();
var repository = serviceProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
var indexManager = serviceProvider.GetRequiredService<IIndexManager<ContractPerson.IPerson>>();

await repository.InitializeAsync().ConfigureAwait(false);

const int totalRecords = 50;
Console.WriteLine($"Persisting {totalRecords} IPerson records to {absoluteDataRoot} ...");

var random = new Random();
var firstNames = new[] { "Alex", "Jordan", "Taylor", "Casey", "Morgan", "Riley", "Sydney", "Robin" };
var departments = new[] { "Engineering", "Finance", "Marketing", "Operations", "Research" };
var courses = new[] { "Mathematics", "Physics", "Computer Science", "History", "Economics" };

var persons = new List<ContractPerson.IPerson>(capacity: totalRecords);
for (var i = 0; i < totalRecords; i++)
{
    var name = $"{firstNames[random.Next(firstNames.Length)]} {firstNames[random.Next(firstNames.Length)]}";
    var age = random.Next(18, 60);

    ContractPerson.IPerson person = i % 2 == 0
        ? new PersonStorage.Student(Guid.NewGuid(), name, age, courses[random.Next(courses.Length)])
        : new PersonStorage.Employee(Guid.NewGuid(), name, age, departments[random.Next(departments.Length)]);

    persons.Add(person);
    await repository.SaveAsync(person).ConfigureAwait(false);
}

Console.WriteLine("Persist complete. Running sample queries...\n");

var firstStudent = await repository.QueryAsync(p => p is PersonStorage.PersonBase model && model.Role == "Student").ConfigureAwait(false);
if (firstStudent is PersonStorage.PersonBase student)
{
    Console.WriteLine($"First student: {student.Name} ({student.Age})");
}

var existing = await repository.ExistsAsync(persons[0]).ConfigureAwait(false);
Console.WriteLine($"First person exists in repository: {existing}");

Console.WriteLine("Enumerating up to 5 employees via index manager stream:");
var count = 0;
await foreach (var person in indexManager.QueryAsync(p => p is PersonStorage.PersonBase model && model.Role == "Employee"))
{
    if (person is PersonStorage.PersonBase employee)
    {
        Console.WriteLine($" - {employee.Name} ({employee.Age})");
        if (++count == 5)
        {
            break;
        }
    }
}

Console.WriteLine("\nDone.");

static string EnsureDataRoot(string? dataRoot)
{
    var resolved = ResolvePath(dataRoot);
    Directory.CreateDirectory(resolved);
    return resolved;
}

static string ResolvePath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return AppContext.BaseDirectory;
    }

    return Path.GetFullPath(Path.IsPathRooted(path)
        ? path
        : Path.Combine(AppContext.BaseDirectory, path));
}

internal static class RepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddRepository<TContract>(
        this IServiceCollection services,
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        MigrationStrategyType strategyType = MigrationStrategyType.None)
        where TContract : class, IRepoDto<TContract, FileInfo>
    {
        services.AddSingleton(contractConfig);
        services.AddSingleton<IDbContractConfigDto<TContract, FileInfo>>(contractConfig);
        services.AddSingleton<IFileNameFactory<TContract, FileInfo>, DefaultFileNameFactory<TContract>>();

        var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
        services.AddSingleton<IObjectSerializer<TContract>>(new BinaryObjectSerializer<TContract>(serializerOptions));
        services.AddSingleton<IEnvelopeRepository<TContract, FileInfo>, FileEnvelopeRepository<TContract>>();
        services.AddSingleton<IIndexManager<TContract>, PassthroughIndexManager<TContract>>();

        if (strategyType != MigrationStrategyType.None)
        {
            services.AddSingleton<IMigrationStrategy<TContract, FileInfo>>(sp =>
            {
                var serializer = sp.GetRequiredService<IObjectSerializer<TContract>>();
                var factory = sp.GetRequiredService<IFileNameFactory<TContract, FileInfo>>();
                var migrations = sp.GetServices<IMigrationContract>()
                    .Where(m => typeof(TContract).IsAssignableFrom(m.GetType()))
                    .ToList();
                return strategyType switch
                {
                    MigrationStrategyType.Lazy => new FileSystemLazyMigrationStrategy<TContract>(contractConfig, serializer, factory, migrations),
                    MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant =>
                        new FileSystemEagerMigrationStrategy<TContract>(contractConfig, serializer, factory, migrations, strategyType),
                    _ => throw new InvalidOperationException($"Unsupported migration strategy '{strategyType}'.")
                };
            });
        }

        services.AddSingleton<IRepository<TContract>>(sp =>
        {
            var manager = sp.GetRequiredService<IIndexManager<TContract>>();
            return new InterfaceRepositryDb<TContract, FileInfo>(manager);
        });

        return services;
    }
}
