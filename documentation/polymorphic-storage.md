# Storing Multiple Concrete Types

An InterfaceDB repository is defined by its interface contract, not by one concrete class. `IRepository<IPerson>` can therefore store a `Student`, an `Employee`, and a `SeniorCitizen` together when all three implement `IPerson`.

`SeniorCitizen` is used for an international audience; the equivalent example would commonly be called `Pensioner` in South African applications.

This is polymorphic storage:

```text
IRepository<IPerson>
├── Student
├── Employee
└── SeniorCitizen
```

You do not create one repository for students, another for employees, and another for senior citizens unless they truly require separate storage boundaries.

## Define The Shared Contract

The interface contains the properties and behavior every stored person must expose:

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }
    int Age { get; set; }
    string Role { get; set; }
}
```

## Implement Different Person Types

Each implementation may also contain properties that do not exist on the shared interface:

```csharp
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

## Enroll The Runtime Types

InterfaceDB resolves a persisted runtime-type identifier through a frozen
`StoredRuntimeTypeAllowlist<IPerson>` before construction and rejects writes of unenrolled types.
The compatibility default enrolls concrete implementations declared in the same assembly as
`IPerson`. When implementations live in another assembly, register them explicitly:

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

Inventory every concrete type already present before narrowing an existing repository's allowlist.

## Save Them Through One Repository

The repository accepts the interface, so every implementation can be written through the same abstraction:

```csharp
IRepository<IPerson> people = provider
    .GetRequiredService<IRepository<IPerson>>();

await people.InitializeAsync();

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

InterfaceDB writes the enrolled concrete type identity into each object's envelope. Its serializer stores the complete concrete object, including implementation-only properties.

## Read The Mixed Collection

Query through the shared interface when the operation applies to every person:

```csharp
await foreach (var person in people.QueryManyAsync(person => person.Age >= 18))
{
    Console.WriteLine($"{person.GetType().Name}: {person.Name}");
}
```

Although the variable is typed as `IPerson`, its runtime object is the original concrete class. Pattern matching exposes concrete-only data:

```csharp
await foreach (var person in people.QueryManyAsync(_ => true))
{
    switch (person)
    {
        case Student student:
            Console.WriteLine($"{student.Name} studies {student.Course}");
            break;

        case Employee employee:
            Console.WriteLine($"{employee.Name} works in {employee.Department}");
            break;

        case SeniorCitizen seniorCitizen:
            Console.WriteLine($"{seniorCitizen.Name} uses card {seniorCitizen.DiscountCardNumber}");
            break;
    }
}
```

## Query One Implementation Type

Generic query overloads narrow the same repository to one concrete implementation:

```csharp
await foreach (var employee in people.QueryManyAsync<Employee>(
    employee => employee.Department == "Engineering"))
{
    Console.WriteLine(employee.Name);
}
```

The important distinction is:

- `IRepository<IPerson>` defines the shared storage boundary.
- `Student`, `Employee`, and `SeniorCitizen` define the runtime object shapes.
- The envelope preserves which runtime shape must be reconstructed.
- Common queries use `IPerson`; implementation-specific queries use the generic concrete-type overloads.

That combination is the main reason InterfaceDB is interface-first rather than class-first persistence.
