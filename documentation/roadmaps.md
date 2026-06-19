# InterfaceDB Roadmap

This document separates known roadmap work from smaller correctness or repository-hygiene gaps.

## Roadmap Items

### Enterprise Envelope Storage Backends

The current file-system implementation is a simple local object database baseline. Future enterprise storage should move the envelope persistence boundary behind provider implementations such as:

- memory-backed storage for very fast in-process or embedded workloads;
- single-file database storage with page/object pointers and a central catalog;
- SQL-backed envelope storage;
- document database envelope storage;
- object database storage;
- distributed cache or queue-backed write coordination where appropriate.

The application-facing contract should remain `IRepository<TContract>` and envelope-aware storage contracts. Storage backends should plug in below that boundary.

The memory-backed and single-file engines are important because the current file provider is one storage pattern, not InterfaceDB's performance ceiling. A memory provider can optimize hot object access directly. A single-file provider can use page/object pointers, skip reads for cached objects, reduce file metadata overhead, and compete with traditional database storage layouts while preserving InterfaceDB's interface/envelope model.

### Git-Backed Data Versioning

A future file-system provider mode can allow each repository data root to also be a Git repository. This is not positioned as backup. The purpose is data versioning: creating explicit commit points for the stored object files so the database can be inspected, diffed, branched, or reverted to a prior data state.

Possible flow:

1. Repository runs normally against the file data root.
2. A maintenance operation requests a data-version commit.
3. The repository enters an exclusive storage scope for that data root.
4. Writes are blocked and reads either pause or read from a stable snapshot while Git stages and commits the current files.
5. The exclusive scope is released after Git completes or fails.

Hard problems to design:

- Whether reads must block during commit, or whether a stable read snapshot can be exposed.
- How to handle long-running Git operations on large object stores.
- How to coordinate commit/revert operations across multiple repository instances and processes.
- How to prevent a revert from invalidating live envelope caches.
- Whether this belongs in the core file provider, the maintenance tool, or an optional provider wrapper.
- How to surface Git failures without corrupting the active storage root.

The first design should be conservative: maintenance-tool driven, explicit, and protected by the same storage coordination model used for migrations and encryption rotation.

### Secondary Indexing

The first in-memory search indexing layer is implemented. It supports opt-in `[SearchIndex]` / `[NotSearchIndexed]` metadata, exact equality, comparable ranges, string prefix lookup, expression parsing for `&&` and `||`, and fallback to predicate scans for unsupported expressions.

The current boundary is:

- equality on the storage identity/logical envelope key routes to the envelope cache;
- indexed property predicates route through property-specific search indexes;
- index results store physical envelope keys, not object copies;
- the repository materializes exact physical envelopes after index lookup.

Remaining indexing work should include:

- Cost/selectivity estimates so `AND` groups evaluate the smallest candidate set first.
- Query compilation and validation optimization. The current indexed query path compiles the expression and validates candidates after index lookup. That is a useful correctness guard, but the hot trace shows it is one of the remaining visible query-side costs. Future work should cache compiled predicates per expression shape where possible, and consider skipping validation only when the query planner can prove that supported indexes fully satisfy the predicate.
- Data-distribution-aware planning. The best index order depends on live data, not only the interface shape. The same contract can have very different selectivity in different applications or companies, so query planning should use per-index cardinality/selectivity estimates from the current repository data instead of hard-coded property order.
- Dedicated public index contracts for custom index implementations.
- Persistent or deterministic rebuild strategies for large stores.
- Additional string modes such as contains/full-text.
- Vector indexes.
- Deeper tests for watcher-driven external updates and migration-driven index refresh.

### Performance Profiling And Layer Polish

Before another tuning pass, capture `dotnet-trace` CPU and allocation traces for the direct InterfaceDB profiling targets documented in `documentation/performance-profiling.md`.

The profiling pass should answer where the write and indexed-query costs are concentrated:

- physical file IO and metadata operations;
- MessagePack serialization through `IObjectSerializer<TContract>`;
- envelope lifecycle and cache updates;
- sidecar write coordination;
- search index update allocation;
- expression planning and candidate set materialization;
- notification and watcher reconciliation.

Layer polish should keep the current boundaries explicit and swappable:

- repository facade: `IRepository<TContract>`;
- query/index orchestration: `IIndexManager<TContract>`;
- envelope persistence: `IEnvelopeRepository<TContract>`;
- physical reader/writer: `IReaderAndWriter<TContract>`, `IObjectReader<TContract>`, `IObjectWriter<TContract>`;
- serialization: `IObjectSerializer<TContract>`;
- encryption: `IEncryptor`;
- write coordination: `IFileWriteCoordinator` and `IStorageWriteLock`;
- storage identity: `IEnvelopeStorageIdentityFactory<TContract>`.

The immediate optimization focus after tracing is search index allocation and query planning. The file storage model should stay simple until a separate storage-engine provider is designed.

### Native Hot Path Provider Research

The current direction is not to rewrite InterfaceDB in Rust or C++. The better option is to keep the public C# interface-first API and allow selected lower layers to be replaced when profiling proves a need.

Potential native or low-level providers:

- native in-memory index engine behind `IIndexManager<TContract>`;
- memory-mapped catalog or object page provider behind the envelope repository boundary;
- append-log or segment-file storage engine;
- native compression/encryption implementation behind existing serializer/encryptor boundaries.

Important constraints:

- Native code should sit behind existing abstractions, not leak into consumer contracts.
- The first implementation should remain C# unless a trace shows a specific hot path that justifies native complexity.
- Interop overhead must be measured; native code is only useful for coarse-grained operations such as batch index updates, storage page writes, or search execution.

### Additional Encryption Key Stores

Windows Credential Manager is the first implemented `IEncryptionKeyStore`. Future providers should preserve the same active-key and historical-key lookup contract for:

- Azure Key Vault;
- AWS KMS or Secrets Manager;
- HashiCorp Vault;
- Apple Keychain;
- Android Keystore;
- hardware security modules;
- development-only environment or configuration stores.

### Encryption Profiles And Secure Unlock Providers

Field-level encryption is implemented, but profile resolution is still basic. The next security design step is a profile system where `[EncryptedStorage("Profile")]` and `[EncryptedStorageField("Profile")]` point to named security configuration instead of directly implying one global encryptor.

Future profile capabilities:

- profile-to-key-store resolution;
- different key stores per object or field;
- passphrase-based unlock;
- app-mediated approval or two-step unlock flows;
- cloud key vault and mobile keychain providers;
- envelope integrity signatures for plaintext or mixed-mode stores;
- device/user-bound save or document files.

Attributes must not contain raw keys. They should contain stable profile names only.

### Adaptive Storage Policy Resolution

`StoragePolicy` is currently explicit configuration. Repository registration accepts an optional policy override:

```csharp
services.AddRepository<IPerson>(
    personOptions,
    storagePolicy: StoragePolicy.Default with
    {
        MaxConcurrency = Environment.ProcessorCount
    });
```

Future work can add an optional resolver layer that detects local or cloud storage characteristics and fills in a policy when the caller has not supplied one.

Suggested abstraction:

```csharp
public interface IStoragePolicyResolver
{
    ValueTask<StoragePolicy> ResolveAsync(
        StoragePolicyContext context,
        CancellationToken cancellationToken = default);
}
```

Suggested resolution order:

```text
AddRepository storagePolicy
    > contractConfig.StoragePolicy
        > IStoragePolicyResolver result
            > StoragePolicy.Default
```

The resolver should plug into repository registration rather than becoming a separate required setup step:

```csharp
services.AddRepository<IPerson>(
    personOptions,
    storagePolicyResolver: new AutoStoragePolicyResolver());
```

or, if DI-owned:

```csharp
services.AddRepository<IPerson>(
    personOptions,
    storagePolicyResolverKey: "local-nvme");
```

Resolver implementations should be conservative and provider-specific:

- local drive heuristic resolver using OS/runtime drive information;
- Azure VM resolver using Azure Instance Metadata Service storage profile, disk caching, IOPS and throughput throttle data;
- AWS EC2 resolver using instance metadata plus EBS/NVMe volume information;
- benchmark/probe resolver that measures actual small-object and sequential throughput before choosing concurrency and buffer settings.

Detection must never override an explicit policy. It should only fill gaps or provide a recommended baseline. This keeps deployments deterministic while allowing optimized defaults for local M.2/NVMe, HDD/network shares, and provisioned cloud disks.

### Maintenance Tool Rename

The existing `MigrationTool` project now presents itself as the InterfaceDB Maintenance Tool and includes encryption rotation. A physical project and executable rename should be reserved for a major version, with a compatibility alias or forwarding command so existing scripts do not break.

## Completed Cleanup

### Encryption Key Rotation

Key-aware `IDBE` version 2 payloads identify the exact historical key required for decryption. Windows Credential Manager stores active and historical keys. Rotation supports direct lazy re-encryption on read and an explicit full-store operation under the exclusive storage scope. Existing single-key providers and legacy `IDBE` version 1 payloads remain supported.

### Contract Rename Protection

`Idb.Analyzers` and immutable `.interfacedb-baseline` manifests warn when storage contracts are renamed, moved, removed, introduced without a baseline, or edited in place without following the versioned migration workflow.

### Runtime Migration Hardening

Migration chains now require one deterministic forward link per source version, preserve legacy storage on failure, support raw-payload lazy migration, and have multi-step, missing-link, ambiguous-link, backward-link, execution-failure, encryption, pending-write, and process-interruption coverage.

### Filesystem Watcher Hardening

The watcher suppresses local event echoes, maintains a payload snapshot, rescans after errors or overflow, supports explicit reconciliation, and has external create/update/delete plus rename/delete race coverage.

### Factory-Based Identity and Cardinality

Storage identity remains delegated to `IEnvelopeStorageIdentityFactory<TContract>` and does not require an ID property or attribute.

`StorageObjectCardinality.Single` rejects detached duplicate creation at an existing factory-resolved path while allowing envelope-aware updates. `Multiple` assigns a unique physical identity to each new object. Single creation is enforced under the out-of-process payload lock.

### Package Vulnerabilities

NuGet packages were updated and the `MessagePack` vulnerability warning was removed.

### Build Warnings

The solution was retargeted to .NET 10 and compiler warnings were cleaned up. A forced rebuild currently completes with zero warnings and zero errors.

### Demo Storage Tracking

The broad `IdbDemoStorage/*` ignore rule was removed so required demo storage source files can be tracked while `bin` and `obj` outputs remain ignored.

### Registration-Level Storage Policy

Repository registration now accepts an optional `StoragePolicy` override. The effective policy is registered in DI per database and low-level storage services consume that resolved policy instead of repeatedly reading the contract config.

### Field-Level Encryption

Interface-level `[EncryptedStorage]` and property-level `[EncryptedStorageField]` are implemented. Marked properties are encrypted into `Envelope.EncryptedFields`, concrete backing properties are cleared after save, and loaded objects are returned through an interface dispatch proxy that decrypts marked getters on demand.
