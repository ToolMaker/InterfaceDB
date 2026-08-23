# Contributing To InterfaceDB

Thank you for helping improve InterfaceDB.

## Before Starting

- Read [Why InterfaceDB](documentation/why-interfacedb.md) and [Architecture](documentation/architecture.md).
- Keep application-facing APIs interface-first and provider-neutral.
- Open an issue for public API changes, storage format changes, or durability changes before implementing them.
- Report security problems privately through [SECURITY.md](SECURITY.md).

## Local Verification

Install the .NET 10 SDK, then run:

```powershell
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore
dotnet test InterfaceDb.Tests\InterfaceDb.Tests.csproj -c Release --no-build --no-restore
dotnet test Idb.Analyzers.Tests\Idb.Analyzers.Tests.csproj -c Release --no-build --no-restore
dotnet run -c Release --no-build --project InterfaceDb.IntegrationTests
```

Add or update tests for every behavioral change. Storage protocol changes require the cross-process/recovery harness and the quick production gate:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick
```

## Performance Changes

State the hypothesis before changing a hot path. Compare before and after on the same machine, runtime, power plan, disk, cache mode, durability mode, record count, and workload.

For SQL comparisons, include SQL-to-object mapping and complete result materialization. Use one transaction boundary per InterfaceDB operation unless the InterfaceDB API being compared also supplies a batch transaction. Validate counts, checksums, final data, reopen behavior, and recovery—not only elapsed time.

Do not commit generated `TestResults`, traces, or BenchmarkDotNet output. Promote reviewed measurements to [Current Performance Results](documentation/performance-results.md).

## Pull Requests

- Keep the change focused and explain user-visible behavior.
- Call out compatibility, storage-format, security, and durability effects.
- Include the verification commands and results.
- Update README/documentation when public behavior changes.
- Do not include secrets, personal paths, production data, or connection strings.
