using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Factories;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using InterfaceDb.ConsoleApp;
using InterfaceDb.ConsoleApp.Models;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var personConfig = configuration.GetSection("PersonContract").Get<PersonContractConfig>()
    ?? throw new InvalidOperationException("PersonContract configuration is missing.");

if (string.IsNullOrWhiteSpace(personConfig.DataRoot))
{
    throw new InvalidOperationException("PersonContract configuration requires DataRoot.");
}

var absoluteDataRoot = Path.IsPathRooted(personConfig.DataRoot)
    ? personConfig.DataRoot
    : Path.Combine(AppContext.BaseDirectory, personConfig.DataRoot);
Directory.CreateDirectory(absoluteDataRoot);
personConfig.DataRoot = absoluteDataRoot;

var services = new ServiceCollection();
services.AddSingleton(configuration);
services.AddSingleton(personConfig);
services.AddSingleton<IDbContractConfigDto<IPerson, FileInfo>>(personConfig);
services.AddSingleton<IFileNameFactory<IPerson, FileInfo>, DefaultFileNameFactory<IPerson>>();
var messagePackOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
services.AddSingleton<IObjectSerializer<IPerson>>(new BinaryObjectSerializer<IPerson>(messagePackOptions));
services.AddTransient<IObjectEnvelope<IPerson, FileInfo>, FileBackedObjectEnvelope<IPerson>>();
services.AddSingleton<IEnvelopeRepository<IPerson, FileInfo>, EnvelopeRepository<IPerson>>();
services.AddSingleton<IIndexManager<IPerson>, PassthroughIndexManager<IPerson>>();
services.AddSingleton<IRepository<IPerson>>(sp =>
{
    var manager = sp.GetRequiredService<IIndexManager<IPerson>>();
    return new InterfaceRepositryDb<IPerson, FileInfo>(manager);
});

using var serviceProvider = services.BuildServiceProvider();
var repository = serviceProvider.GetRequiredService<IRepository<IPerson>>();
var indexManager = serviceProvider.GetRequiredService<IIndexManager<IPerson>>();

await repository.InitializeAsync();

Console.WriteLine($"Persisting 100 IPerson records to {absoluteDataRoot} ...");

var random = new Random();
var firstNames = new[] { "Alex", "Jordan", "Taylor", "Casey", "Morgan", "Riley", "Sydney", "Robin" };
var departments = new[] { "Engineering", "Finance", "Marketing", "Operations", "Research" };
var courses = new[] { "Mathematics", "Physics", "Computer Science", "History", "Economics" };

var persons = new List<IPerson>(capacity: 100);
for (var i = 0; i < 100; i++)
{
    var name = $"{firstNames[random.Next(firstNames.Length)]} {firstNames[random.Next(firstNames.Length)]}";
    var age = random.Next(18, 60);

    IPerson person = i % 2 == 0
        ? new Student(Guid.NewGuid(), name, age, courses[random.Next(courses.Length)])
        : new Employee(Guid.NewGuid(), name, age, departments[random.Next(departments.Length)]);

    persons.Add(person);
    await repository.SaveAsync(person);
}

Console.WriteLine("Persist complete. Running sample queries...\n");

var firstStudent = await repository.QueryAsync(p => p.Role == "Student");
if (firstStudent is not null)
{
    Console.WriteLine($"First student: {firstStudent.Name} ({firstStudent.Age})");
}

var existing = await repository.ExistsAsync(persons[0]);
Console.WriteLine($"First person exists in repository: {existing}");

Console.WriteLine("Enumerating up to 5 employees via index manager stream:");
var count = 0;
await foreach (var person in indexManager.QueryAsync(p => p.Role == "Employee"))
{
    Console.WriteLine($" - {person.Name} ({person.Age})");
    if (++count == 5)
    {
        break;
    }
}

Console.WriteLine("\nDone.");

