# InterfaceDB Roadmap

This document separates known roadmap work from smaller correctness or repository-hygiene gaps.

## Roadmap Items

### Enterprise Envelope Storage Backends

The current file-system implementation is a simple local object database baseline. Future enterprise storage should move the envelope persistence boundary behind provider implementations such as:

- SQL-backed envelope storage;
- document database envelope storage;
- object database storage;
- distributed cache or queue-backed write coordination where appropriate.

The application-facing contract should remain `IRepository<TContract>` and envelope-aware storage contracts. Storage backends should plug in below that boundary.

### Secondary Indexing

The first in-memory search indexing layer is implemented. It supports opt-in `[SearchIndex]` / `[NotSearchIndexed]` metadata, exact equality, comparable ranges, string prefix lookup, expression parsing for `&&` and `||`, and fallback to predicate scans for unsupported expressions.

The current boundary is:

- equality on the storage identity/logical envelope key routes to the envelope cache;
- indexed property predicates route through property-specific search indexes;
- index results store physical envelope keys, not object copies;
- the repository materializes exact physical envelopes after index lookup.

Remaining indexing work should include:

- Cost/selectivity estimates so `AND` groups evaluate the smallest candidate set first.
- Dedicated public index contracts for custom index implementations.
- Persistent or deterministic rebuild strategies for large stores.
- Additional string modes such as contains/full-text.
- Vector indexes.
- Deeper tests for watcher-driven external updates and migration-driven index refresh.

### Additional Encryption Key Stores

Windows Credential Manager is the first implemented `IEncryptionKeyStore`. Future providers should preserve the same active-key and historical-key lookup contract for:

- Azure Key Vault;
- AWS KMS or Secrets Manager;
- HashiCorp Vault;
- Apple Keychain;
- Android Keystore;
- hardware security modules;
- development-only environment or configuration stores.

### Field-Level Encryption

Current encryption protects the complete serialized envelope payload. Future work can add property-level encryption for contracts that need selected fields encrypted independently before full payload serialization.

One possible developer experience:

```csharp
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; }

    [EncryptedStorageField]
    string NationalIdentifier { get; }
}
```

Suggested flow:

1. During write, inspect the contract/implementation metadata for encrypted fields.
2. Encrypt only those values before the object payload is serialized.
3. Store enough non-secret field encryption metadata to choose the correct key on read.
4. During read, decrypt the field once while materializing the object.
5. Keep the decrypted value in memory on the materialized object, so property access does not decrypt repeatedly.

The implementation should avoid mutating user objects unexpectedly. A safer design may require serializer-level hooks, generated storage proxies, or envelope-side encrypted field templates rather than reflection that changes live object state. This should be designed carefully after the indexing and batch-write layers are stable.

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
