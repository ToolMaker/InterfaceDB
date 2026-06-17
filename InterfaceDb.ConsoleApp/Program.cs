using System.Diagnostics;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extentions;
using IdbDemoStorage.Options;
using Microsoft.Extensions.DependencyInjection;
using ContractPerson = MigrationTool.Contracts.Person.v1_0_0;
using PersonStorage = IdbDemoStorage.Storage.Person.v1_0_0;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var personOptions = new PersonOptions
{
    DataRoot = Path.Combine("Data")
};
var storagePolicy = StoragePolicy.Default with
{
    MaxConcurrency = Environment.ProcessorCount
};

var services = new ServiceCollection();
services.AddRepository<ContractPerson.IPerson>(
    personOptions,
    storagePolicy: storagePolicy);

using var serviceProvider = services.BuildServiceProvider();
var repository = serviceProvider.GetRequiredService<IRepository<ContractPerson.IPerson>>();
var indexManager = serviceProvider.GetRequiredService<IIndexManager<ContractPerson.IPerson>>();

await repository.InitializeAsync().ConfigureAwait(false);

const int totalRecords = 500;
Console.WriteLine($"Persisting IPerson records to {personOptions.DataRoot} ...");
Console.WriteLine($"Sequential batch: {totalRecords}");
Console.WriteLine($"Bulk batch:       {totalRecords}");
Console.WriteLine($"Bulk concurrency: {storagePolicy.MaxConcurrency}");
Console.WriteLine();

var sequentialPersons = CreatePeople(totalRecords, seed: 100);
var bulkPersons = CreatePeople(totalRecords, seed: 200);

var stopwatch = Stopwatch.StartNew();
foreach (var person in sequentialPersons)
{
    await repository.SaveAsync(person).ConfigureAwait(false);
}
stopwatch.Stop();
var sequentialElapsed = stopwatch.Elapsed;

stopwatch.Restart();
var savedBulkPersons = await repository.SaveManyAsync(bulkPersons).ConfigureAwait(false);
stopwatch.Stop();
var bulkElapsed = stopwatch.Elapsed;

var speedup = bulkElapsed.TotalMilliseconds <= 0
    ? 0
    : sequentialElapsed.TotalMilliseconds / bulkElapsed.TotalMilliseconds;

Console.WriteLine("Persist complete.");
Console.WriteLine($"Sequential SaveAsync loop: {sequentialElapsed.TotalMilliseconds:N0} ms");
Console.WriteLine($"SaveManyAsync bulk save:   {bulkElapsed.TotalMilliseconds:N0} ms");
Console.WriteLine($"Observed speedup:          {speedup:N2}x");
Console.WriteLine();
Console.WriteLine("Running sample queries...\n");

var firstStudent = await repository.QueryAsync(p => p.Role == "Student").ConfigureAwait(false);
if (firstStudent is not null)
{
    Console.WriteLine($"First student: {firstStudent.Name} ({firstStudent.Age})");
}

var existing = await repository.ExistsAsync(savedBulkPersons[0]).ConfigureAwait(false);
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

static List<ContractPerson.IPerson> CreatePeople(int count, int seed)
{
    var random = new Random(seed);
    var firstNames = new[] { "Alex", "Jordan", "Taylor", "Casey", "Morgan", "Riley", "Sydney", "Robin" };
    var departments = new[] { "Engineering", "Finance", "Marketing", "Operations", "Research" };
    var courses = new[] { "Mathematics", "Physics", "Computer Science", "History", "Economics" };
    var persons = new List<ContractPerson.IPerson>(capacity: count);

    for (var i = 0; i < count; i++)
    {
        var name = $"{firstNames[random.Next(firstNames.Length)]} {firstNames[random.Next(firstNames.Length)]}";
        var age = random.Next(18, 60);

        ContractPerson.IPerson person = i % 2 == 0
            ? new PersonStorage.Student(Guid.NewGuid(), name, age, courses[random.Next(courses.Length)])
            : new PersonStorage.Employee(Guid.NewGuid(), name, age, departments[random.Next(departments.Length)]);

        persons.Add(person);
    }

    return persons;
}
