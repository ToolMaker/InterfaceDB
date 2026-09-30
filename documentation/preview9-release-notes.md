# InterfaceDB 1.0.0-preview.9

The source tree is prepared for `1.0.0-preview.9`. This change does not publish a NuGet package or create a release tag.

Preview.9 is a runtime and encrypted-payload format update after preview.8. Plaintext InterfaceDB envelopes remain `IDB6`; repositories using AES-GCM now write context-bound `IDBE` version 3 frames.

## Security hardening

- New AES-GCM writes authenticate a stable storage context together with the ciphertext. The context covers the storage provider, repository registration, contract identity and physical record key. Moving a v3 payload to a different context fails authentication.
- Contextless `IDBE` version 1 and version 2 payloads are rejected by contextual repository reads unless `AesGcmEncryptorOptions.AllowContextlessPayloadMigration` is enabled for a controlled migration. Accepted legacy ciphertext is marked for replacement with v3.
- Legacy non-`IDB` typeless MessagePack envelopes are disabled by default. Normal `IDB2` through `IDB6` envelope reads remain supported when their concrete type is enrolled.
- Stored-object reads have a 256 MiB default `StoragePolicy.MaximumStoredObjectBytes` limit, enforced before file-backed readers allocate the payload buffer.
- A member marked `[EncryptedStorageField]` is excluded from a type-level search-index declaration. Combining `[EncryptedStorageField]` with an explicit member `[SearchIndex]` is rejected rather than persisting a searchable plaintext derivative.
- SegmentCore persistent search-index catalogs are disabled for encrypted repositories. Startup removes stale plaintext catalog artifacts and rebuilds the required indexes in memory from decrypted authoritative records.
- An explicit named encryption registration wins for the same dependency-injection key whether it is added before or after repository registration.
- MessagePack readers use the untrusted-data security profile, and stored runtime types must resolve through a frozen `StoredRuntimeTypeAllowlist<TContract>` before object construction.

## SegmentCore backup and restore

Backup and restore now reject observed symbolic links, junctions, device entries and other reparse points in the paths they consume. Restore accepts only canonical, known SegmentCore manifest paths and continues to verify every listed length and SHA-256 digest before publishing a new destination.

The existing restore overload remains available. A new overload accepts `SegmentCoreRestoreOptions`:

```csharp
var limits = new SegmentCoreRestoreOptions
{
    MaximumManifestBytes = 16L * 1024 * 1024,
    MaximumFiles = 100_000,
    MaximumTotalBytes = 1024L * 1024 * 1024 * 1024,
    MaximumFileBytes = 256L * 1024 * 1024 * 1024,
    MaximumPathDepth = 4,
    MaximumDuration = TimeSpan.FromHours(24),
    RequireFreeSpaceAdmission = true,
    MinimumFreeSpaceReserveBytes = 64L * 1024 * 1024
};

var restored = await SegmentCoreStore.RestoreBackupAsync(
    backupDirectory,
    destinationDirectory,
    storeOptions,
    limits,
    cancellationToken);
```

The values shown are the defaults. Restore checks the manifest-size, file-count, aggregate-byte, per-file-byte, path-depth and duration budgets, and by default requires enough destination capacity for all admitted files plus the 64 MiB reserve. Tighten them for the deployment's expected backup size.

These path checks reduce link-based traversal but do not yet claim handle-pinned protection against every concurrent filesystem namespace swap. The remaining qualification work is tracked in the [roadmap](roadmaps.md).

## Encrypted-store upgrade

Before running preview.9 against an encrypted preview.8 or earlier store:

1. Stop every old writer and take a verified backup with the old runtime. Retain the old runtime and every required encryption key for rollback.
2. Start preview.9 with the same repository registration, contract identity, physical keys and encryption keys, and temporarily set `AllowContextlessPayloadMigration = true`.
3. Use the provider-specific complete conversion path:
   - File provider: run its `IEncryptionRotationOperation<TContract>.RotateAllAsync`, then reopen and verify every record.
   - SegmentCore: no full-store rotation operation is registered. Enumerate and materialize every record so its bounded compare-before-replace read path rewrites contextless frames, then checkpoint and run `ISegmentCoreRepositoryMaintenance<TContract>.VerifyIntegrityAsync`.
4. Disable `AllowContextlessPayloadMigration`, reopen, and enumerate every record again. Confirm that a contextless frame is rejected before removing the rollback backup or old keys.

Do not rename, copy or transplant encrypted records during the migration. The physical record key and repository context are authenticated by v3.

A contextual encrypted repository accepts plaintext rollout data only when both `AllowUnencryptedPayloads` and `AllowContextlessPayloadMigration` are enabled. Rewrite and verify those records before disabling the migration settings.

There is no automatic in-place path for legacy non-`IDB` typeless payloads. If a trusted old store contains that format, use an explicitly configured `BinaryObjectSerializer<TContract>` only in an offline, source-preserving conversion process. Dedicated legacy migration tooling remains roadmap work.

## Stored runtime-type enrollment

Repository registration now accepts a `StoredRuntimeTypeAllowlist<TContract>`. The compatibility
default enrolls closed concrete implementations declared in the contract assembly and remains frozen
for the process lifetime; it does not expand because another implementation assembly is loaded
later.

Before upgrading an existing store, inventory every persisted concrete type. Pass an explicit
allowlist when an implementation lives outside the contract assembly or when a deliberately
narrower production set is required:

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

var personTypes = new StoredRuntimeTypeAllowlist<IPerson>(
    typeof(Student),
    typeof(Employee));

services.AddRepository(
    personOptions,
    runtimeTypeAllowlist: personTypes);
```

Reads reject an unenrolled persisted type before construction, and writes reject an unenrolled
runtime type before serialization.

## Index compatibility

Contracts that explicitly apply both `[EncryptedStorageField]` and `[SearchIndex]` to one member now fail initialization. Remove the index declaration before upgrading. Type-level index declarations continue to index eligible ordinary members and silently omit encrypted members.

Encrypted SegmentCore repositories rebuild search indexes in memory on startup and do not publish a persistent search-index catalog. Existing plaintext catalog files are deleted because they are derived data; authoritative encrypted records are unchanged.

## Rollback

Preview.8 and earlier cannot read `IDBE` v3. After preview.9 has rewritten any encrypted payload, rollback requires stopping preview.9 and restoring the complete pre-upgrade backup with the older runtime. Copying selected v3 files into an older store is unsupported.

The plaintext `IDB6` envelope format and SegmentCore engine formats 1.0/1.1 are unchanged by this release. The ciphertext transition is separate from the optional SegmentCore 1.1 independent-compaction transition.

## Remaining qualification

Preview.9 does not close the stable-1.0 qualification gate. Remaining security work includes platform-specific no-follow/handle-pinned namespace-swap hardening, adversarial backup/restore qualification on Windows and Linux, a same-key anti-rollback policy, dedicated contextless-ciphertext and typeless legacy migration tooling, and pinning release workflow actions to immutable commit SHAs. Physical power-cut and target-device qualification also remain separate release evidence.
