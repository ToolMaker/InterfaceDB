# InterfaceDB

InterfaceDB is a sandbox for an interface-first object storage stack. The current baseline is a simple flat file-backed repository for desktop/mobile style local storage, plus a contract baseline and migration scaffolding CLI, a sample storage project, and performance benchmarks.

For product positioning, tradeoffs, and current performance notes, see [Why InterfaceDB Exists](documentation/product-positioning.md).

## Solution layout

- `Idb.Libraries/`
  Core abstractions and the default file-system implementation. The library uses MessagePack envelopes, an in-memory envelope cache, and optional migration strategies.
- `MigrationTool/`
  CLI for creating versioned contract baselines and scaffolding migration classes between adjacent versions.
- `IdbDemoStorage/`
  Sample `Person` contract, concrete storage types, contract options, and a sample migration.
- `InterfaceDb.ConsoleApp/`
  Small console demo that writes and queries `Person` records through `IRepository<TContract>`.
- `PerformanceCommon/`
  Shared benchmark data generation helpers.
- `StorageBenchmarks/`
  Throughput comparison between the file-system repository, SQL Server, Dapper, and EF Core.
- `StorageBenchmarks.Transactions/`
  Transaction-oriented benchmark variant for the same `Person` sample payloads.

## Build and run

1. Install the .NET 10 SDK.
2. Build the solution:

```bash
dotnet build InterfaceDB.sln
```

3. Run the console demo:

```bash
dotnet run --project InterfaceDb.ConsoleApp/InterfaceDb.ConsoleApp.csproj
```

The console app writes 500 sample `Person` records to a local `Data/` folder under its output directory, then runs a few simple existence and query checks.

4. Run the migration tool:

```bash
dotnet run --project MigrationTool/MigrationTool.csproj -- <command>
```

## Storage model

The storage stack is built around versioned interfaces:

1. Define a contract interface, make it inherit `IStoredContract<TContract>`, and decorate it with `[StorageScope(VersionTag = "...")]`.
2. Implement the contract in one or more concrete classes.
3. Register the repository with `AddRepository<TContract>(...)` or `AddFileRepository<TContract>(...)`.
4. Persist and query contracts through `IRepository<TContract>`.
5. When the contract shape changes, generate the next baseline and implement the scaffolded migration logic.

`IStoredContract<TContract>` exposes a getter-only `Envelope`. Concrete implementations should use a non-public setter such as `public IObjectEnvelope<TContract>? Envelope { get; private set; }`. The library sets and clears that property internally during wrap and unwrap operations.

The file-backed implementation stores MessagePack payloads on disk under a versioned directory layout.

Flat file-system layout:

```text
Data/
  1.0.0/
    <key>.mpk
```

This flat layout is the only active filesystem layout. Higher-throughput storage backends, such as SQL/document/object database envelope stores, are future roadmap work. `StoragePolicy` controls write concurrency, cache hydration, notifications, sidecar write locks, and file change watching.

File watching synchronizes independent repository instances for committed create, update, rename, and delete operations. Local operations acknowledge their provider locator so watcher echoes do not duplicate notifications. The watcher maintains a payload snapshot and rescans after watcher errors or buffer overflow; `IEnvelopeStorageChangeSource<T>.RescanAsync` also supports explicit reconciliation.

Storage identity is factory-driven. `StorageObjectCardinality.Single` allows one object per factory-resolved path and rejects detached duplicate creation, while `Multiple` gives every new envelope-less object a unique physical key. Custom `IEnvelopeStorageIdentityFactory<TContract>` implementations can derive keys from object state without requiring a prescribed ID property.

Repository registrations are keyed by `EnvelopeDICacheKey`, falling back to `ContractName`. Multiple databases can use the same contract:

```csharp
services.AddRepository<IPerson>(localOptions);
services.AddRepository<IPerson>(secureOptions);

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
var secure = provider.GetInterfaceDbRepository<IPerson>("SecurePeople");
```

The registration key can also be supplied at registration time when the options object should stay shared:

```csharp
services.AddRepository<IPerson>(personOptions, "LocalPeople");

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
```

Per-database encryption and filename factories can be registered with the same key before `AddRepository`. Unkeyed resolution remains available and points to the first registered database for backward compatibility.

Repository queries support streaming and bounded cardinality:

```csharp
await foreach (var person in repository.QueryManyAsync(x => x.Role == "Student"))
{
}

var first = await repository.QueryFirstAsync(x => x.Role == "Student");
var exactlyOne = await repository.QuerySingleAsync(x => x.Name == "Jane");
var optional = await repository.QuerySingleOrDefaultAsync(x => x.Name == "Jane");

var deleted = await repository.DeleteAsync(exactlyOne);
```

Single-result methods accept optional exception factories for cardinality violations.

`DeleteAsync` deletes the exact storage object identified by its envelope. It returns `true` when active storage was removed and `false` when the same object was already deleted. Envelope-less objects are rejected, and saving an object after its envelope was deleted throws `StorageObjectDeletedException`.

Storage exceptions are provider-neutral. File, SQL, document, event-stream, and memory providers can report the same `StorageException` hierarchy while retaining provider-specific failures as inner exceptions.

Optional encryption at rest is applied to the complete serialized envelope. The default `EncryptionNone` implementation keeps normal `IDB2` files unchanged. AES-GCM can be enabled before repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption(keyProvider);
services.AddRepository<IMyContract>(options);
```

AES-GCM files use an authenticated `IDBE` outer frame containing the encrypted `IDB2` payload.

Key rotation uses `IRotatingKeyProvider`. The `IDBE` version 2 header stores a non-secret key ID so the exact historical key can be selected without trying every key. Reads can optionally re-encrypt directly to the current key:

```csharp
var keyStore = new WindowsCredentialEncryptionKeyStore("People");
keyStore.AddOrUpdateKey("key-2025", oldKey);
keyStore.AddOrUpdateKey("key-2026", currentKey);
keyStore.SetActiveKey("key-2026");

services.AddInterfaceDbWindowsCredentialEncryption(
    "People",
    new AesGcmEncryptorOptions
    {
        LegacyKeyId = "key-2025",
        RotationMode = EncryptionRotationMode.ReencryptOnRead
    });
```

For a forced full-store rotation, resolve `IEncryptionRotationOperation<TContract>` and call `RotateAllAsync`. Old keys should be removed only after all payloads have been rewritten.

Lazy rotation is enabled in the AES registration options. This is application configuration because it changes normal repository read behavior:

```csharp
services.AddInterfaceDbWindowsCredentialEncryption(
    "People",
    new AesGcmEncryptorOptions
    {
        LegacyKeyId = "key-2025",
        RotationMode = EncryptionRotationMode.ReencryptOnRead
    });
```

Use `ReadHistoricalOnly` when reads should decrypt historical payloads without rewriting them.

Forced rotation can also be run from the InterfaceDB maintenance CLI. The project remains named `MigrationTool` for command and script compatibility:

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  encryption rotate `
  --database People `
  --data-root C:\Data\People `
  --extension .mpk `
  --legacy-key-id key-2025
```

The command reads the active and historical keys from Windows Credential Manager, drains pending writes, acquires the exclusive storage scope, and re-encrypts each payload directly to the active key.

## Contract rename protection

`Idb.Analyzers` provides Roslyn diagnostics for immutable InterfaceDB baseline identities. Roslyn analyzers run in Visual Studio, VS Code with C# tooling, Rider, `dotnet build`, and CI.

The migration tool writes a `.interfacedb-baseline` manifest beside every generated contract baseline. The analyzer reports:

- `IDB001`: a previously recorded contract type was removed, renamed, or moved;
- `IDB002`: a `[StorageScope]` contract was added without a generated baseline manifest;
- `IDB003`: an existing baseline's `VersionTag` was changed in place;
- `IDB004`: a baseline manifest is malformed.

During repository development, reference the analyzer project:

```xml
<ProjectReference Include="..\Idb.Analyzers\Idb.Analyzers.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
<AdditionalFiles Include="Contracts\**\.interfacedb-baseline" />
```

For distributed applications, reference the `Idb.Analyzers` package with `PrivateAssets="all"`. Its build-transitive props automatically include baseline manifests:

```xml
<PackageReference Include="Idb.Analyzers" Version="1.0.0" PrivateAssets="all" />
```

The diagnostics are warnings by default and can be promoted to errors in `.editorconfig`:

```ini
dotnet_diagnostic.IDB001.severity = error
dotnet_diagnostic.IDB002.severity = error
dotnet_diagnostic.IDB003.severity = error
dotnet_diagnostic.IDB004.severity = error
```

## Sample contract in this repo

The sample storage project currently centers on `Person`:

- Contracts live under `IdbDemoStorage/Contracts/Person/`.
- Runtime implementations live under `IdbDemoStorage/Storage/Person/`.
- Contract options live in `IdbDemoStorage/Options/PersonOptions.cs`.
- Migration stubs live under `IdbDemoStorage/Migrations/Person/`.

The sample includes two contract versions:

- `v1_0_0` defines `Id`, `Name`, `Age`, and `Role`.
- `v1_0_1` adds `Surname`.

There is already a scaffolded migration at `IdbDemoStorage/Migrations/Person/Person_v1_0_0_To_v1_0_1_Migration.cs`. It is still sample code and should be treated as a starting point, not finished migration logic.

## Migration tool workflow

The `baseline` command creates or advances a contract under a contracts root such as `IdbDemoStorage/Contracts/<Contract>/vX_Y_Z/`.

The command requires exactly one of:

- `--contracts-dir <path>`
- `--config-file-dir <path>`

Important behavior:

- `--contracts-dir` should point at the contracts root folder, not a single version folder.
- `--config-file-dir` can point at a specific `*Options.cs` file or a directory that contains one.
- When the tool increments an existing contract version, it also scaffolds any missing adjacent migration files automatically.
- `migrate scaffold` can be used later to backfill missing migration files without creating a new baseline.
- If a contract options file does not already exist, the tool scaffolds one into a sibling `Options/` folder.

### Use absolute paths for CLI examples

The tool normalizes user-supplied paths from its executable directory. In practice, the least surprising way to run it is to pass absolute paths.

PowerShell example:

```powershell
$repo = (Resolve-Path .).Path
$contracts = Join-Path $repo "IdbDemoStorage\Contracts"
$storage = Join-Path $repo "IdbDemoStorage\Storage"
$migrations = Join-Path $repo "IdbDemoStorage\Migrations"
$personOptions = Join-Path $repo "IdbDemoStorage\Options\PersonOptions.cs"
```

### Create an initial contract baseline

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  baseline Dog `
  --contracts-dir $contracts `
  --storage-dir $storage `
  --migration-dir $migrations `
  --impl Dog `
  --version 1.0.0
```

This creates a new `Dog/v1_0_0/` baseline folder under the contracts root, prepares matching storage and migration folders, and scaffolds a contract options file if one does not already exist.

### Increment an existing contract

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  baseline Person `
  --config-file-dir $personOptions `
  --increment patch
```

If a previous baseline exists, the tool copies the latest version into the next semantic-version folder, updates the namespace and `VersionTag`, and scaffolds any missing adjacent migration classes.

Runtime migration chains are strict and deterministic. For every stored version below `ActiveVersion`, exactly one forward migration must exist for each step until the active version is reached. Missing links, duplicate source migrations, malformed versions, and backward links raise migration-specific exceptions.

`EagerStrict` propagates migration failures and retains legacy storage. `EagerTolerant` skips objects with invalid or failed chains without modifying their legacy payloads. `Lazy` keeps legacy envelopes unhydrated during initialization and migrates their raw payload on the first matching query.

Cross-process writes and deletes use last-operation-wins ordering. A delete completed after a write removes the object. A write completed after another process deletes an object can recreate it when that writer loaded the valid envelope before deletion. Stale-write rejection belongs to the optional optimistic-concurrency capability.

### Scaffold migrations without creating a new baseline

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  migrate scaffold Person `
  --config-file-dir $personOptions
```

This walks the known adjacent baseline versions and creates any missing migration stubs under the configured migrations root.

## Benchmarks

The benchmark projects are useful for comparing the file-backed repository against database-backed inserts, but they are not zero-config:

- `StorageBenchmarks/` and `StorageBenchmarks.Transactions/` expect a SQL Server connection string in their `appsettings.json` files or environment variables.
- The file-system benchmark setup currently writes under `G:\PerformanceTest\...`. Update the code if that path does not exist on your machine.

Run them with:

```bash
dotnet run --project StorageBenchmarks/StorageBenchmarks.csproj -c Release
dotnet run --project StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj -c Release
```

The current best measured InterfaceDB store-only benchmark for 50k `Person` objects is:

```text
Mean: 5.261 s
Allocated: 160.82 MB
Settings:
  INTERFACEDB_BENCH_MAX_CONCURRENCY=16
  INTERFACEDB_BENCH_BULK_BATCH_SIZE=16384
  INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER=true
```

Run just the InterfaceDB transaction benchmark with:

```powershell
$env:INTERFACEDB_BENCH_MAX_CONCURRENCY = "16"
$env:INTERFACEDB_BENCH_BULK_BATCH_SIZE = "16384"
$env:INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER = "true"

dotnet run -c Release --project StorageBenchmarks.Transactions -- --filter *FileSystem_StoreTransaction*
```

The query benchmark is intentionally still a predicate scan over the envelope cache. Secondary indexing is the next major performance feature.

## Tests

The solution has a dedicated MSTest project plus a console integration harness:

```powershell
dotnet test InterfaceDB.sln
dotnet run --project InterfaceDb.IntegrationTests/InterfaceDb.IntegrationTests.csproj
```

The integration harness covers:

- single and bulk save behavior, including partial failures;
- delete semantics and stale-object protection;
- sidecar write locking and process interruption recovery;
- keyed DI isolation and registration behavior;
- storage identity factories and cardinality;
- file watcher synchronization and rescan recovery;
- eager and lazy migration chains;
- encryption, historical key reads, lazy key rotation, forced rotation, and encrypted migration.

## Current state

- The solution builds cleanly with `dotnet build InterfaceDB.sln`.
- `InterfaceDb.Tests` wraps the integration harness so `dotnet test` exercises the current product scenarios.
- Some sample namespaces still use the `MigrationTool.Contracts.*` naming pattern even when the files live under `IdbDemoStorage/`. Follow the actual project paths when navigating the repo.
