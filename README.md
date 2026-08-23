# InterfaceDB

InterfaceDB is an interface-first object repository and file-backed object database for .NET 10. Application code works with versioned interfaces and `IRepository<TContract>`; serialization, concrete types, file layout, indexes, migrations, caching, concurrency, and encryption stay behind that boundary.

The central design rule is simple:

> Let the application core depend on contracts and repository abstractions, not storage implementations.

InterfaceDB is not an ORM. An ORM maps objects to a relational model. InterfaceDB persists polymorphic object implementations behind interface contracts and provides the repository, envelope, migration, index, and storage lifecycle itself.

## Why Use It?

- Keep file I/O, serialization, and storage locations out of domain and application services.
- Store several concrete implementations behind one interface.
- Change the storage provider below `IRepository<TContract>` without changing callers.
- Version contracts and migrate stored objects deliberately.
- Add exact, range, prefix, suffix, and ordinal contains indexes with attributes.
- Choose metadata-only, weak, or strong object caching per workload.
- Use authenticated AES-GCM encryption, field-level encryption, and key rotation when configured.
- Run locally without a database server or connection string.
- Keep each stored object as an inspectable, independently managed file.

InterfaceDB is a strong fit for desktop applications, local-first tools, embedded application data, offline workloads, and systems where object-native persistence and architectural boundaries matter more than relational joins.

It is not currently a replacement for a server database when you need distributed transactions, SQL joins, remote multi-user access, replication, or a persistent full-text/vector engine. See [Product Positioning](documentation/product-positioning.md).

## The Programming Model

Define a versioned interface in the application contract layer:

```csharp
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;

[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    [SearchIndex]
    string Name { get; set; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
    int Age { get; set; }
}
```

Implement the interface without inheriting a persistence base class:

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

public sealed class Student : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}
```

At the composition root, register the file provider and resolve the repository abstraction:

```csharp
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddRepository<IPerson>(new PersonOptions());

await using var provider = services.BuildServiceProvider();
var people = provider.GetRequiredService<IRepository<IPerson>>();

await people.InitializeAsync();
await people.SaveAsync(new Student { Name = "Jane", Age = 22 });

await foreach (var person in people.SearchManyAsync(
    person => person.Age >= 18 &&
              person.Name.StartsWith("Ja", StringComparison.Ordinal)))
{
    Console.WriteLine(person.Name);
}
```

The application service sees `IPerson` and `IRepository<IPerson>`. Only the composition root selects the file-backed implementation.

See [Getting Started](documentation/getting-started.md) for the complete `PersonOptions` class and query examples.

## Implemented Features

- File-per-object storage in flat, versioned directories.
- Single- and multiple-object logical cardinality.
- GUID v7, sequential, and custom storage identity factories.
- Single and concurrent bulk saves, delete, existence, streaming, first, and single-result APIs.
- Polymorphic concrete type recovery.
- Exact, range, ordinal prefix, ordinal suffix, and literal ordinal contains indexes.
- Cost-aware `AND` intersections, safe `OR` unions, bounded ranges, residual predicates, and query-plan diagnostics.
- File change watching and in-process added/stored/removed/deleted notifications.
- Cross-process last-operation-wins coordination and abandoned-proposal recovery.
- Performance and Durable file-write modes.
- Eager strict, eager tolerant, and lazy runtime migrations.
- MessagePack `IDB4` envelopes with backward reads for `IDB2` and `IDB3`.
- Whole-envelope and selected-field AES-GCM encryption.
- Active and historical key handling, lazy rotation, and full-store rotation.
- Windows Credential Manager key storage.
- Roslyn diagnostics that protect immutable contract baselines.

## Current Performance Snapshot

On the repository's Windows/NVMe development machine, the 1,000-object quick gate measured one independent Durable write per object at a 5.626 ms median. The equivalent SQLite FULL autocommit measurements were 3.019 ms through `Microsoft.Data.Sqlite` and 3.094 ms through Dapper. The optimized file protocol is 2.7 times faster than InterfaceDB's earlier 15.299 ms Durable baseline while preserving all forced-process recovery and cross-process tests.

These are local diagnostic measurements, not universal rankings. Queries return fully materialized objects for every engine, verify count and checksum, and include mapping from the storage API into the object model. SQL scalar reads are not treated as equivalent to InterfaceDB object retrieval.

See [Current Performance Results](documentation/performance-results.md) and [Benchmark Methodology](documentation/production-benchmark-gate.md).

## Documentation

- [Documentation Index](documentation/README.md)
- [Why InterfaceDB](documentation/why-interfacedb.md)
- [Getting Started](documentation/getting-started.md)
- [Configuration](documentation/configuration.md)
- [Feature Guide](documentation/features.md)
- [Architecture](documentation/architecture.md)
- [Security And Encryption](documentation/security.md)
- [Contract Versioning And Migrations](documentation/migrations.md)
- [Storage, Indexing, And Performance](documentation/storage-indexing-performance.md)
- [Query Planning](documentation/query-planning.md)
- [Search Index Strategies](documentation/search-index-strategies.md)
- [Current Performance Results](documentation/performance-results.md)
- [Production Benchmark And Stress Gate](documentation/production-benchmark-gate.md)
- [Roadmap](documentation/roadmaps.md)

## Build And Verify

Prerequisite: .NET 10 SDK.

```powershell
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore
dotnet test InterfaceDb.Tests\InterfaceDb.Tests.csproj -c Release --no-build --no-restore
dotnet test Idb.Analyzers.Tests\Idb.Analyzers.Tests.csproj -c Release --no-build --no-restore
dotnet run -c Release --no-build --project InterfaceDb.IntegrationTests
```

Run the example:

```powershell
dotnet run -c Release --project InterfaceDb.ConsoleApp
```

Run the fair embedded-storage quick gate:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick
```

Generated traces, BenchmarkDotNet artifacts, test data, and gate reports are intentionally ignored. Curated, reproducible results belong in `documentation/performance-results.md`.

## Project Status

The file provider is functional and extensively tested, but the project should be treated as pre-release until a public API/versioning policy, package identity, license, CI release workflow, and longer multi-platform soak results are established. The next storage-engine research item is an append-only journal; it can be added below the existing repository/index boundary without changing application code.
