# Getting Started

This guide shows the smallest useful InterfaceDB setup: define a contract, implement it, register a repository, save objects, and query them.

Install the pre-release package and the standard DI implementation used by these examples:

Use a .NET 10 application. These examples describe preview.10; until that version is published,
use a project reference or the locally prepared NuGet feed. Pin `--version 1.0.0-preview.10`
when adopting that release. Source builds also require the compiler version in `global.json`.

```powershell
dotnet add package InterfaceDB --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

## 1. Define A Storage Contract

Contracts are interfaces. They must inherit `IStoredContract<TContract>` and declare a storage version with `[StorageScope]`.

```csharp
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;

[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }
    int Age { get; set; }
    string Role { get; set; }
}
```

## 2. Implement The Contract More Than Once

Concrete storage classes expose the envelope with a non-public setter. InterfaceDB sets it internally.

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

public sealed class Student : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "Student";
    public string Course { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}

public sealed class Employee : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "Employee";
    public string Department { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}

public sealed class SeniorCitizen : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "SeniorCitizen";
    public string DiscountCardNumber { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}
```

These three classes live in the same `IRepository<IPerson>`. InterfaceDB stores the concrete implementation type in each envelope, so a `Student` reloads as a `Student`, an `Employee` as an `Employee`, and a `SeniorCitizen` as a `SeniorCitizen`. Concrete-only properties such as `Course`, `Department`, and `DiscountCardNumber` are preserved too.

## 3. Configure The Repository

Your options object supplies the database name, data root, active version, file extension, repository type, identity cardinality, and storage policy. Search indexes are declared on contract members with `[SearchIndex]`, not through a second configuration collection.

```csharp
public sealed class PersonOptions : IDbContractConfigDto<IPerson>
{
    public string EnvelopeDICacheKey { get; } = "People";
    public string ContractName { get; } = "People";
    public string FileExtension { get; } = ".mpk";
    public string? DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "People");
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Multiple;
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
}
```

Place the following namespaces at the top of your application/configuration files as needed:

```csharp
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;
```

Register it:

```csharp
using Idb.Libraries.Implementation.Extensions;

var services = new ServiceCollection();
services.AddRepository<IPerson>(new PersonOptions());
```

The default `StoredRuntimeTypeAllowlist<IPerson>` enrolls concrete implementations declared in the
same assembly as `IPerson`. If `Student`, `Employee`, or another stored implementation lives in
a different assembly, register the frozen set explicitly:

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

var personTypes = new StoredRuntimeTypeAllowlist<IPerson>(
    typeof(Student),
    typeof(Employee),
    typeof(SeniorCitizen));

services.AddRepository(
    new PersonOptions(),
    runtimeTypeAllowlist: personTypes);
```

Use this registration instead of the preceding one when supplying the explicit allowlist.

Resolve and initialize:

```csharp
await using var provider = services.BuildServiceProvider();
var people = provider.GetRequiredService<IRepository<IPerson>>();
await people.InitializeAsync();
```

## 4. Save And Query

Save different implementations through the same repository:

```csharp
await people.SaveManyAsync(new IPerson[]
{
    new Student
    {
        Name = "Jane",
        Age = 22,
        Course = "Physics"
    },
    new Employee
    {
        Name = "John",
        Age = 41,
        Department = "Engineering"
    },
    new SeniorCitizen
    {
        Name = "Mary",
        Age = 72,
        DiscountCardNumber = "SC-1042"
    }
});
```

Read every implementation through the shared interface and inspect its actual runtime type:

```csharp
await foreach (var person in people.QueryManyAsync(_ => true))
{
    var detail = person switch
    {
        Student student => student.Course,
        Employee employee => employee.Department,
        SeniorCitizen seniorCitizen => seniorCitizen.DiscountCardNumber,
        _ => string.Empty
    };

    Console.WriteLine($"{person.GetType().Name}: {person.Name} ({detail})");
}
```

Or ask the same repository for only one implementation type:

```csharp
await foreach (var employee in people.QueryManyAsync<Employee>(
    employee => employee.Department == "Engineering"))
{
    Console.WriteLine(employee.Name);
}
```

The non-generic query overloads work across the complete `IPerson` collection:

```csharp

var firstAdult = await people.QueryFirstAsync(x => x.Age >= 18);

await foreach (var person in people.QueryManyAsync(x => x.Role == "Student"))
{
    Console.WriteLine(person.Name);
}
```

`QueryManyAsync(Func<T,bool>)` is a predicate scan. Use `SearchManyAsync(Expression<Func<T,bool>>)` when you want the index manager to inspect the expression and use configured indexes.

```csharp
await foreach (var person in people.SearchManyAsync(
    person => person.Age >= 18 &&
              person.Name.StartsWith("Ja", StringComparison.Ordinal)))
{
    Console.WriteLine(person.Name);
}
```

For `StartsWith` and `EndsWith`, use `StringComparison.Ordinal` so the expression semantics match the radix index. Unsupported or culture-sensitive expressions safely fall back to candidate validation or a scan.

See [Storing Multiple Concrete Types](polymorphic-storage.md) for the complete polymorphic storage model.

## 5. Save Many

```csharp
var saved = await people.SaveManyAsync(new IPerson[]
{
    new Student { Name = "Jane", Age = 22, Course = "Physics" },
    new Employee { Name = "John", Age = 34, Department = "Engineering" },
    new SeniorCitizen { Name = "Mary", Age = 72, DiscountCardNumber = "SC-1042" }
});
```

`SaveManyAsync` uses the storage policy for batching and concurrency. It reports partial failures through `StorageBulkOperationException`.

## 6. Delete

Delete requires a persisted object with an envelope.

```csharp
var person = await people.QuerySingleAsync(x => x.Name == "Jane");
var deleted = await people.DeleteAsync(person);
```

After delete, saving the same storage-aware object throws `StorageObjectDeletedException`. Recreate deliberately with a new envelope-less object.

## 7. Run The Demo

```powershell
dotnet run -c Release --project InterfaceDb.ConsoleApp
```

The demo uses the `IdbDemoStorage` project and writes sample people through `IRepository<IPerson>`.

## 8. Choose Durability Deliberately

`StoragePolicy.Default` uses `StorageDurabilityMode.Performance`. It writes canonical object files directly for low local latency. Select Durable mode when proposal recovery and cross-process coordination are required:

```csharp
var policy = StoragePolicy.Default with
{
    DurabilityMode = StorageDurabilityMode.Durable,
    MaxConcurrency = Environment.ProcessorCount
};

services.AddRepository<IPerson>(new PersonOptions(), storagePolicy: policy);
```

Durable mode protects InterfaceDB's tested process-crash and multi-process protocol. It is not a substitute for backups, filesystem permissions, or platform-level power-loss testing.

## 9. Continue

- Add indexes with the [Feature Guide](features.md).
- Choose a cache mode in [Storage, Indexing, And Performance](storage-indexing-performance.md).
- Configure encryption with [Security And Encryption](security.md).
- Register disposable [notifications](repository-notifications.md) and await `Ready` before your initial query.
- Introduce new contract versions with [Contract Versioning And Migrations](migrations.md).
- Tune repository behavior with [Configuration](configuration.md).
- Check [preview.10 upgrade guidance](preview10-release-notes.md) before opening an existing store.
