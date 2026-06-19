# InterfaceDB

InterfaceDB is an interface-first object database for .NET. It stores concrete objects behind versioned C# interfaces, keeps storage metadata in envelopes, and lets the storage engine, identity model, indexing, migrations, and encryption live below the repository API.

The current provider is a high-capability file-backed object store. It is aimed at desktop, mobile, Unity, local-first, embedded, and application-owned data where object-native persistence matters. The roadmap also includes memory-backed storage and a single-file database storage engine so the same InterfaceDB model can compete in different performance profiles.

## Why It Exists

Most databases ask you to model storage as tables, documents, or key/value records. InterfaceDB starts from the application contract:

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }
    int Age { get; set; }
}
```

You save and query the interface:

```csharp
await repository.SaveAsync(new Student { Name = "Jane", Age = 22 });

var adults = repository.SearchManyAsync(x => x.Age >= 18);
```

InterfaceDB stores enough metadata to recover the correct concrete type later, migrate versions, apply indexes, handle file changes, and protect data through full-envelope or field-level encryption.

## Current Capabilities

- Interface-first repositories through `IRepository<TContract>`.
- Polymorphic concrete implementations behind one contract.
- File-backed envelope storage with flat versioned directories.
- Envelope metadata catalog for constant-time storage-key equality lookup.
- Single and multiple object cardinality models.
- Configurable storage identity factories, including GUID v7 and sequential factories.
- `SaveAsync`, `SaveManyAsync`, streaming queries, first/single query helpers, delete, and existence checks.
- Opt-in search indexes for equality, range, and string prefix queries.
- Concrete-type-specific query overloads.
- Cross-process file write coordination with sidecar locks and last-write-wins proposal commits.
- File-system watcher reconciliation for external create, update, rename, and delete events.
- Runtime migrations with eager and lazy strategies.
- Whole-envelope AES-GCM encryption with key rotation.
- Field-level encryption through interface attributes and secure getter/setter proxy behavior.
- Windows Credential Manager key store.
- Roslyn analyzer protection against accidental storage contract renames.
- Benchmark projects comparing InterfaceDB file storage with SQL Server, Dapper, and EF Core.

## Documentation

- [Getting Started](documentation/getting-started.md)
- [Feature Guide](documentation/features.md)
- [Architecture](documentation/architecture.md)
- [Security And Encryption](documentation/security.md)
- [Storage, Indexing, And Performance](documentation/storage-indexing-performance.md)
- [Product Positioning And Benchmarks](documentation/product-positioning.md)
- [Performance Profiling](documentation/performance-profiling.md)
- [Roadmap](documentation/roadmaps.md)

## Install And Build

InterfaceDB currently targets .NET 10.

```powershell
dotnet build InterfaceDB.sln
```

Run the integration harness:

```powershell
dotnet run -c Release --project InterfaceDb.IntegrationTests
```

Run the console demo:

```powershell
dotnet run -c Release --project InterfaceDb.ConsoleApp
```

## Minimal Registration

```csharp
services.AddRepository<IPerson>(personOptions);

await using var provider = services.BuildServiceProvider();
var repository = provider.GetRequiredService<IRepository<IPerson>>();

await repository.InitializeAsync();
await repository.SaveAsync(new Student { Name = "Jane", Age = 22 });
```

Multiple databases can use the same contract with keyed registration:

```csharp
services.AddRepository<IPerson>(localOptions, "LocalPeople");
services.AddRepository<IPerson>(secureOptions, "SecurePeople");

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
var secure = provider.GetInterfaceDbRepository<IPerson>("SecurePeople");
```

## Encryption Example

Whole-envelope encryption:

```csharp
services.AddInterfaceDbAesGcmEncryption(keyProvider);
services.AddRepository<IPerson>(personOptions);
```

Field-level encryption:

```csharp
[StorageScope(VersionTag = "1.0.0")]
[EncryptedStorage("PeopleSecure")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }

    [EncryptedStorageField("NationalId")]
    string NationalIdentifier { get; set; }
}
```

Encrypted fields are stored in the envelope as ciphertext and are decrypted through the InterfaceDB contract proxy when accessed through the interface.

## Benchmarks

The transaction benchmark defaults to 25,000 persons. For the older 50,000-person full run:

```powershell
$env:INTERFACEDB_BENCH_PERSON_COUNT="50000"
$env:INTERFACEDB_BENCH_MAX_CONCURRENCY="16"
$env:INTERFACEDB_BENCH_BULK_BATCH_SIZE="16384"
$env:INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER="true"

dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --run-all
```

The current file provider is not the final performance ceiling. It is one storage pattern. InterfaceDB has already shown strong indexed-read potential while doing more storage-model work than a scalar row benchmark: envelopes, concrete type metadata, migrations, encryption hooks, file coordination, and object materialization. Future memory-backed and single-file storage engines are intended to push the same repository model into higher-throughput profiles.

## Current Storage Format

Plain envelope files use internal `IDB*` headers:

- `IDB2`: older binary envelope format.
- `IDB3`: added per-object cache preference.
- `IDB4`: current format, adds encrypted field metadata.

AES-GCM encrypted files use an outer `IDBE` frame. Existing `IDB2` and `IDB3` payloads remain readable.

## Project Status

This is an active product/library project. The current file-backed implementation is usable for local object storage scenarios, and the roadmap is focused on polish, documentation, storage-engine extensibility, indexing depth, security providers, and performance profiling.
