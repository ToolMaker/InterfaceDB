# Conflict resolution with retained editing

Available under MIT in `InterfaceDB.ConflictResolution`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

## Selecting the supplied strategy

Using the configured contract configuration from the public getting-started pattern:

```csharp
var policy = StoragePolicy.Default with { RetainedVersions = new() };
var services = new ServiceCollection();
services.AddSingleton<IWriteConflictStrategy<IContact>>(
    new ThreeWayMergeStrategy<IContact>(new()
    {
        IdentityProperties = [nameof(IContact.Id)],
        MaximumProperties = 256,
        MaximumCollectionItems = 16_384,
        ValidateCandidateAsync = (candidate, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(
                candidate.Minimum > candidate.Maximum ? "invalid-range" : null);
        }
    }));
// Assign policy to config, then register AddFileRepository(config),
// AddSegmentCoreRepository(config), or AddAtlasCoreRepository(config).
```

`IdentityProperties` must be explicit and nonempty. Register the strategy before the provider. Retention options and strategy selection are independent; ordinary strict registration remains the default and needs neither retention nor this assembly.

```csharp
await using var a = await repository.BeginEditAsync(existing.Envelope!.EnvelopeCacheKey);
await using var b = await repository.BeginEditAsync(existing.Envelope.EnvelopeCacheKey);
a.Working.Name = "Current name";
await a.SaveAsync();
b.Working.Number = 7;
var merged = await b.SaveAsync(); // both disjoint changes, actual new revision
```

A later pair both changing `Name` is rejected with reason `overlapping-properties` and property `Name`, including when both chose the same new value. `conflict.ReadCurrentAsync()` obtains isolated current state. `b.ReadOriginalAsync()` remains B after rejection until release/expiry. An unchanged stale incoming entity returns current/no-change; the matched-revision fast route remains a normal validated save. Refresh/rebase is a new explicit scope.

The runnable versions use `IItem` in `MergeTests.cs`: disjoint/overlap/equal-target cases, nulls/dictionaries/byte arrays/lists, no-op, validation, identity, missing/wrong baseline, encryption, expiry while a merge is admitted, metadata-only acknowledgement and compaction. The core tests demonstrate independent strategy implementations without this optional package, strict retained scopes, mixed no-change batches, renewal/limits/cancellation and separate File instances.

## Comparison and validation policy

Only persisted, readable/writable contract properties participate. Envelope/system state is excluded. Identity properties cannot change through a stale merge. Metadata/accessors are cached; incompatible persisted concrete types/versions, additional undeclared runtime properties and non-ignored public instance fields are rejected. Encryption proxies use the configured lifecycle and encoded concrete type identity, including concrete-schema validation. Required encrypted values are compared semantically; denial or getter failure leaves data unchanged and no values/secrets enter conflict diagnostics.

Supported scalar values use value equality. Arrays/lists/dictionaries are whole-property values, recursively compared under a bounded work budget/depth; byte arrays use bounded span equality. Distinct dictionary entry edits still overlap because the property is the unit of merging. Unsupported property types require an explicit deterministic comparer at registration. Custom comparers and validators are trusted, pure, bounded, cancellation-aware application code. Work limits reject excessive input rather than silently switching to reference equality. Notification comparers are a separate configuration and may also be required for unsupported notification property types.

`MaximumCollectionItems` limits total structural comparison work across both B/C and B/I comparisons, not just the size of one collection. An unchanged 64 KiB byte array can consume over 131,072 units across those comparisons plus the other properties. The measured large-object configuration explicitly raises this limit to 1,000,000 and separately raises the notification collection limit. Application callbacks are trusted; cancellation is cooperative and there is no hard execution deadline for an uncooperative callback.

Candidate validation runs before insertion, matching updates and merged commits when configured. Validation is not authorization; it should not mutate its candidate. It must not make remote calls or reenter storage under an entity guard. Reentrant writes/acquisitions/maintenance fail before lock entry. Callback/validation exceptions and pre-commit cancellation do not publish speculative values. Post-commit notification behavior remains that of the foundation.

## Lifetimes, durability and boundaries

Defaults are 5-minute leases, 30-minute maximum lifetime, 256 sessions, 256 unique retained records, 64 MiB retained-byte admission, 15-second expiry cleanup. The validated maximum lifetime cannot exceed one day. Renewal cannot exceed the lease duration or maximum lifetime. Limits apply per opened registry; aggregate File multi-process admission is not a shared global quota. Expired scopes cannot be renewed. Already-admitted reads/saves retain their use hold. Dispose is idempotent; successful sessions are single-use.

History artifacts are session-scoped, not durable offline drafts. Repository close/restart invalidates handles. SegmentCore backups select current/recovery files and omit separate history; Atlas backups explicitly exclude `.idb-history`. External File backups should exclude it or allow enabled-store reopen to clean it. Restore requires closed stores and creates new session ownership. A live source's session remains valid only against that source, not a restored copy.

Failed archive deletion remains reported as physically present bytes and consumes admission space until retry succeeds. An enabled-store open examines up to 1,024 owner directories, preserving live owners; the cleanup guard has a bounded contention wait. Statistics count encoded/frame lengths, not allocation-unit disk consumption. Observed directory bytes in benchmarks are reported separately. File live-key markers and owner/cleanup lock metadata remain outside retained-payload-byte counts.

File Durable/Performance retain their foundation commit guarantees and mandatory sidecar coordination. Custom File coordinators/identity factories that cannot provide capture are explicitly unsupported for editing. SegmentCore/Atlas keep one owning writer process. ResilientRepository and third-party adapters do not silently turn local sessions into distributed histories; unsupported wrappers are rejected. No remote transport, event bus, cross-store transaction, distributed merge, offline lease persistence or point-in-time recovery was added.
