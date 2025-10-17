using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extentions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
}));

services.AddLocalScopeStorage(builder =>
{
    builder.AssembliesToScan = new[] { typeof(IPerson).Assembly };
    builder.RootDirectory = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "DemoStorage"));
});

services.AddDefaultObjectWriterAndReader();

await using var provider = services.BuildServiceProvider();
var repository = provider.GetRequiredService<IRepository>();

await repository.InitializeAsync();

var people = Enumerable.Range(1, 100)
    .Select(i => new Person
    {
        Id = $"person-{i:D3}",
        FirstName = $"First{i}",
        LastName = $"Last{i}",
        Age = 20 + (i % 30)
    })
    .ToList();

foreach (var person in people)
{
    await repository.SaveAsync<IPerson>(person, cancellationToken: CancellationToken.None);
}

var reloaded = new List<IPerson>(people.Count);
foreach (var person in people)
{
    var result = await repository.LoadAsync<IPerson>(p => p.Id == person.Id, CancellationToken.None);
    reloaded.Add(result.Value);
}

var target = people[42];
var exists = await repository.ExistsAsync<IPerson>(new Person { Id = target.Id }, CancellationToken.None);

Console.WriteLine($"Stored {people.Count} people.");
Console.WriteLine($"Reloaded {reloaded.Count} people.");
Console.WriteLine($"Person '{target.Id}' exists: {exists}");
