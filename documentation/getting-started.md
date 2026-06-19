# Getting Started

This guide shows the smallest useful InterfaceDB setup: define a contract, implement it, register a repository, save objects, and query them.

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

## 2. Implement The Contract

Concrete storage classes expose the envelope with a non-public setter. InterfaceDB sets it internally.

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

public sealed class Student : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "Student";
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}
```

Multiple classes can implement the same contract. InterfaceDB stores the concrete implementation type in the envelope so it can reload the right object later.

## 3. Configure The Repository

Your options object supplies the database name, data root, active version, file extension, indexes, repository type, and storage policy.

```csharp
public sealed class PersonOptions : IDbContractConfigDto<IPerson>
{
    public string EnvelopeDICacheKey { get; } = "People";
    public string ContractName { get; } = "People";
    public string FileExtension { get; } = ".mpk";
    public string? DataRoot { get; } = @"C:\Data\People";
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public IIndex<IPerson>[]? Indexes { get; } = Array.Empty<IIndex<IPerson>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; } = IndexProvisioningModeType.None;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default;
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
}
```

Register it:

```csharp
services.AddRepository<IPerson>(new PersonOptions());
```

Resolve and initialize:

```csharp
await using var provider = services.BuildServiceProvider();
var people = provider.GetRequiredService<IRepository<IPerson>>();
await people.InitializeAsync();
```

## 4. Save And Query

```csharp
await people.SaveAsync(new Student
{
    Name = "Jane",
    Age = 22
});

var firstAdult = await people.QueryFirstAsync(x => x.Age >= 18);

await foreach (var person in people.QueryManyAsync(x => x.Role == "Student"))
{
    Console.WriteLine(person.Name);
}
```

`QueryManyAsync(Func<T,bool>)` is a predicate scan. Use `SearchManyAsync(Expression<Func<T,bool>>)` when you want the index manager to inspect the expression and use configured indexes.

## 5. Save Many

```csharp
var saved = await people.SaveManyAsync(new IPerson[]
{
    new Student { Name = "Jane", Age = 22 },
    new Employee { Name = "John", Age = 34 }
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
