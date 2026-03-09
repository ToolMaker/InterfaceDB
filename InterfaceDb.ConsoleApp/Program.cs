using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extentions;
using IdbDemoStorage.Options;
using Microsoft.Extensions.DependencyInjection;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;
using PersonStorage = IdbDemoStorage.Storage.Person.v1_0_0;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var personOptions = new PersonOptions();

var services = new ServiceCollection();
services.AddFileRepository<ContractPerson.IPerson>(personOptions, MigrationStrategyType.None);

using var serviceProvider = services.BuildServiceProvider();
var repository = serviceProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
var indexManager = serviceProvider.GetRequiredService<IIndexManager<ContractPerson.IPerson>>();

await repository.InitializeAsync().ConfigureAwait(false);

const int totalRecords = 500;
Console.WriteLine($"Persisting {totalRecords} IPerson records to {personOptions.DataRoot} ...");

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

var firstStudent = await repository.QueryAsync(p => p.Role == "Student").ConfigureAwait(false);
if (firstStudent is not null)
{
    Console.WriteLine($"First student: {firstStudent.Name} ({firstStudent.Age})");
}

var existing = await repository.ExistsAsync(persons[0]).ConfigureAwait(false);
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
