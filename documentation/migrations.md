# Contract Versioning And Migrations

InterfaceDB treats a stored interface version as a persistence contract. Do not edit an already-persisted contract shape in place. Create a new baseline, implement an adjacent migration, and then move the repository's `ActiveVersion` forward.

## Choose A Runtime Strategy

| Strategy | Behavior | Typical use |
|---|---|---|
| `None` | No version conversion is attempted. | A new store or a contract that has never changed. |
| `Lazy` | Converts an older object when it is read. A failed conversion leaves the legacy record available. | Large stores where startup time matters. |
| `EagerStrict` | Converts all older records during initialization and fails startup on any error. | Controlled deployments that require an all-current store. |
| `EagerTolerant` | Attempts all conversions during initialization and retains records that fail. | Recovery and staged cleanup workflows. |

## Create Or Increment A Baseline

Run the maintenance tool from the repository root. Point it either at the contract-baseline root or at an options file that contains `ContractPath`, `ImplementationsPath`, and `MigrationsPath`.

```powershell
dotnet run --project MigrationTool -- `
  baseline Person `
  --contracts-dir .\IdbDemoStorage\Contracts\Person `
  --storage-dir .\IdbDemoStorage\Storage\Person `
  --migration-dir .\IdbDemoStorage\Migrations\Person `
  --increment patch
```

For an existing options class:

```powershell
dotnet run --project MigrationTool -- `
  baseline Person `
  --config-file-dir .\IdbDemoStorage\Options\PersonOptions.cs `
  --increment patch
```

The tool creates an immutable baseline manifest and, when two adjacent versions exist, scaffolds the missing migration. Review generated files before compiling or touching production data.

## Implement The Migration

A migration implements the source contract, target contract, and `IMigration`. Its state is deserialized from the old payload; `Up()` maps or initializes the new shape.

```csharp
using Idb.Libraries.Abstractions.Services.Migrations;

public sealed class Person_1_0_0_To_1_0_1 :
    Contracts.v1_0_0.IPerson,
    Contracts.v1_0_1.IPerson,
    IMigration
{
    public string SourceVersion => "1.0.0";
    public string TargetVersion => "1.0.1";

    public bool Up()
    {
        Surname = string.IsNullOrWhiteSpace(Surname) ? "Unknown" : Surname;
        return true;
    }

    public bool Down() => true;

    // Implement members from both versioned contracts, including their envelopes.
}
```

Keep migrations deterministic and side-effect free. Preserve unchanged values explicitly, initialize new required values deliberately, and never silently discard data in `Down()` without documenting that decision.

## Register And Activate

Register every migration before the repository, select a strategy, and set the options object's `ActiveVersion` to the target version:

```csharp
services.AddSingleton<IMigration, Person_1_0_0_To_1_0_1>();
services.AddRepository<Contracts.v1_0_1.IPerson>(
    personOptions,
    MigrationStrategyType.EagerStrict);
```

`InitializeAsync()` executes eager strategies. Lazy conversion runs on access. Migration chains must form an unambiguous path from every stored source version to the active version; missing, duplicate, or cyclic definitions are rejected.

## Migrate A SegmentCore Store

SegmentCore migration is an explicit provider maintenance operation. It never scans sibling
directories automatically, never deletes the source, and requires source and target to be
different store roots. Register the complete `IMigration` chain, configure the repository at the
target `ActiveVersion`, initialize it, and then invoke:

```csharp
var maintenance = provider.GetSegmentCoreMaintenance<IPerson>();

SegmentCoreMigrationResult result = await maintenance.MigrateFromAsync(
    sourceStoreDirectory: @"D:\Data\People\.segment-core\1.0.0",
    sourceVersion: "1.0.0",
    options: new SegmentCoreMigrationOptions
    {
        BatchSize = 256,
        ContinueOnError = false,
        VerifyAfterMigration = true
    });

Console.WriteLine(
    $"Migrated {result.Migrated:N0}; already present {result.AlreadyPresent:N0}; " +
    $"failures {result.Failures.Count:N0}.");
```

The operation reads validated `IDB2`, `IDB3`, `IDB4`, or `IDB5` envelopes, decrypts them with the
configured provider encryptor, executes each adjacent migration, writes target envelopes through
normal atomic SegmentCore batches, publishes a target checkpoint, and optionally runs integrity
verification. Existing physical keys are skipped, making a stopped run resumable. Strict mode is
the default; `ContinueOnError = true` records per-object failures and continues. Retain the source
and its encryption keys until application-level verification and backup promotion are complete.

`IDB5` extension metadata is preserved across file-provider and SegmentCore migrations
and is rejected when it exceeds the 64 KiB envelope limit. Preview format compatibility is
forward-only for this change: preview.6 reads `IDB2` through `IDB5`, while preview.5 cannot read an
`IDB5` payload. Keep a pre-upgrade backup if application rollback must remain possible.

## Deployment Checklist

1. Back up the data root and encryption keys.
2. Test against a copy containing every known stored version and concrete implementation.
3. Verify success, failure, restart, and rollback behavior.
4. Prefer `EagerStrict` when deployment must stop on the first incompatible record.
5. Do not remove old contracts or migration steps while older payloads or backups may still require them.

The executable integration harness covers adjacent and chained migration behavior, strict and tolerant failures, lazy migration, concurrent initialization, and encrypted migration paths.
