# Security And Encryption

InterfaceDB can encrypt complete envelopes or selected contract properties. Encryption is opt-in; the default `EncryptionNone` provider writes plaintext `IDB6` payloads. In preview.9, AES-GCM repositories write context-bound `IDBE` version 3 frames.

## Security Boundary

The implemented controls protect persisted payload contents against offline inspection and detect ciphertext/header modification when AES-GCM is configured. Version 3 also rejects ciphertext presented under a different authenticated storage context. They do not provide:

- operating-system account isolation or file ACL management;
- protection after an attacker can execute code in the application process;
- secure deletion from SSDs, backups, snapshots, or synchronized copies;
- encrypted filenames, directory names, file sizes, or modification times;
- distributed authorization, audit logging, or remote access control;
- automatic key escrow or recovery;
- same-key anti-rollback for an older valid ciphertext at the same physical record key;
- a formal third-party cryptographic audit.

Use normal OS permissions, backup controls, process hardening, and secret-management practices around the library.

## Whole-Envelope Encryption

Whole-envelope encryption is applied after MessagePack serialization:

```text
object -> envelope -> IDB6 bytes -> contextual AES-GCM -> IDBE v3 file
IDBE v3 file -> contextual AES-GCM -> IDB6 bytes -> envelope -> object
```

`AesGcmEncryptor` uses:

- a cryptographically random nonce for every encryption;
- a 16-byte authentication tag;
- 128-, 192-, or 256-bit AES keys;
- authenticated framing and key-identifier bytes;
- a SHA-256 storage-context digest in the authenticated `IDBE` v3 header;
- AES-GCM associated data derived from the same storage context.

The storage context includes the provider identity, repository registration key, contract assembly/name, configured contract name, and physical record key. Copying or renaming an encrypted record into another provider, repository, contract, or physical key causes authentication to fail. It does not add a monotonic version to the context, so replacing a record with an older valid v3 ciphertext for the same key remains a separate anti-rollback policy decision.

Authentication failures throw `CryptographicException`; corrupted or context-mismatched ciphertext is not returned as an object.

Register encryption and the repository with the same key:

```csharp
services.AddInterfaceDbAesGcmEncryption("SecurePeople", keyProvider);
services.AddRepository<IPerson>(secureOptions, "SecurePeople");
```

The explicit named encryption registration replaces the repository's same-key fallback whether it is added before or after repository registration. With multiple repositories, always use explicit keys rather than relying on unkeyed compatibility aliases.

Custom whole-envelope encryption used by repository storage must implement `IContextualEncryptor`; a contextless `IEncryptor` fails closed when a contextual read or write is required.

## Key Providers

`IKeyProvider` supplies one current key. `IRotatingKeyProvider` additionally supplies:

- the active key identifier;
- historical keys by identifier;
- the active key material.

InterfaceDB stores a non-secret key ID in keyed `IDBE` headers. It does not store the encryption key in the data file.

Keys must remain available while any file references them. Removing a historical key before all files have been rotated makes those files unreadable.

## Windows Credential Manager

`WindowsCredentialEncryptionKeyStore` implements rotating key storage with Generic Credentials for the current Windows user.

```csharp
var keyStore = new WindowsCredentialEncryptionKeyStore("People");
keyStore.AddOrUpdateKey("key-2026", RandomNumberGenerator.GetBytes(32));
keyStore.SetActiveKey("key-2026");

services.AddInterfaceDbWindowsCredentialEncryption("People");
services.AddRepository<IPerson>(personOptions, "People");
```

This provider is Windows-specific. Other operating systems currently require a custom `IKeyProvider`/`IRotatingKeyProvider`; platform keychain, vault, and HSM providers remain roadmap work.

Do not place key bytes, passwords, or tokens in attributes, source control, `appsettings.json`, command-line arguments, or filenames.

## Key Rotation And Context Migration

Key-aware payloads record the key ID used for encryption. Rotation is provider-specific:

- `EncryptionRotationMode.ReencryptOnRead` rewrites an eligible payload after a successful read. File and SegmentCore repositories use compare-before-replace behavior so a concurrent write is not overwritten.
- The File provider registers `IEncryptionRotationOperation<TContract>.RotateAllAsync`; it acquires the migration/write scope and rewrites every File-provider payload that is not current.
- SegmentCore does not register `IEncryptionRotationOperation<TContract>`. A complete SegmentCore rotation or context migration must enumerate and materialize every record, then checkpoint and verify integrity.

Legacy `IDBE` version 1 has no key ID; configure `LegacyKeyId` when a rotating provider must read it. Version 2 has a key ID but no repository context. Contextual preview.9 reads reject both formats by default.

For a controlled encrypted-store upgrade:

1. Stop old writers and retain a verified full backup, the older runtime, and every referenced key.
2. Set `AesGcmEncryptorOptions.AllowContextlessPayloadMigration = true` with the unchanged repository/contract/key configuration.
3. For the File provider, run its complete `RotateAllAsync`, reopen, and verify every record.
4. For SegmentCore, enumerate and materialize every record to trigger bounded compare-before-replace conversion, then checkpoint and run `VerifyIntegrityAsync`.
5. Disable `AllowContextlessPayloadMigration`, reopen, and enumerate every record again before removing rollback material.

Read-on-access conversion affects only records that are actually materialized; an ordinary workload does not prove complete conversion. Preview.8 and earlier cannot read v3, so rollback after any v3 write requires restoring the full pre-upgrade backup.

## Field-Level Encryption And Indexes

Mark selected properties on the interface:

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

Before serialization, InterfaceDB serializes and encrypts each marked value into `Envelope.EncryptedFields`, then clears the concrete backing property. On read, a `DispatchProxy` decrypts an encrypted getter on demand and encrypts setter values back into the envelope.

Encrypted values are never written to a search index. A type-level `[SearchIndex]` declaration omits members marked `[EncryptedStorageField]`; applying `[SearchIndex]` directly to an encrypted member is rejected during index discovery.

SegmentCore's persistent search-index catalog is a rebuildable plaintext derivative. When repository encryption is active, catalog persistence is disabled, stale catalog artifacts are deleted at startup, and indexes are rebuilt in memory from authoritative decrypted records. This protects encrypted values at the cost of warm-start catalog acceleration.

Field-level encryption does not encrypt opaque `Envelope.ExtensionMetadata`. Without whole-envelope AES-GCM, extension metadata is stored as plaintext in the `IDB6` payload. Do not place secrets there, or enable whole-envelope encryption for the repository.

Current field-encryption limits remain:

- marked properties must have public get and set accessors;
- callers must use the returned interface/proxy instance to obtain encrypted values;
- profile names are persisted as labels; the registered repository encryptor performs the encryption;
- plaintext necessarily exists in application memory while a value is being used.

## Parser And Payload Admission

The default MessagePack options use `MessagePackSecurity.UntrustedData`. Before object construction, an `IDB2`–`IDB6` runtime-type identifier must resolve through the frozen `StoredRuntimeTypeAllowlist<TContract>`. Writes also require an enrolled closed concrete implementation of the requested contract.

The compatibility default enrolls concrete implementations declared in the contract assembly. Pass an explicit allowlist to repository registration when implementations live in another assembly or when the application requires a narrower set:

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

var allowedTypes = new StoredRuntimeTypeAllowlist<IPerson>(
    typeof(Student),
    typeof(Employee));

services.AddRepository(
    personOptions,
    runtimeTypeAllowlist: allowedTypes);
```

Inventory every concrete type in an existing store before narrowing the set. Unenrolled reads fail before construction and unenrolled writes fail before serialization.

Legacy non-`IDB` typeless MessagePack payloads are disabled by default. `BinaryObjectSerializer<TContract>` exposes an explicit `allowLegacyTypelessPayloads` constructor argument for trusted offline conversion, but normal repository registration leaves it false. Do not enable it on an untrusted live store. Dedicated migration tooling remains roadmap work.

`StoragePolicy.MaximumStoredObjectBytes` defaults to 256 MiB. File-backed readers reject larger envelopes before allocating their payload buffers, and the default serializer applies the same default admission. Set a lower positive value for applications with smaller legitimate objects.

## Plaintext Encryption Rollout

`AesGcmEncryptorOptions.AllowUnencryptedPayloads` identifies a deliberate plaintext-to-encrypted rollout. Under contextual repository reads, plaintext acceptance also requires `AllowContextlessPayloadMigration` because the old payload has no authenticated storage context.

These settings weaken enforcement. Enable both only for a controlled migration window, use the provider-specific complete conversion and verification process above, and then disable them.

## SegmentCore Backup And Restore

SegmentCore backup and restore reject observed symbolic links, junctions, device entries, and other reparse points in source, staging, destination-parent, and manifest-listed paths. Restore accepts only canonical paths from the known SegmentCore inventory and verifies every declared length and SHA-256 digest before publication.

`SegmentCoreRestoreOptions` defaults to:

| Admission | Default |
| --- | ---: |
| Manifest bytes | 16 MiB |
| Files | 100,000 |
| Aggregate file bytes | 1 TiB |
| One file | 256 GiB |
| Relative path depth | 4 |
| Duration | 24 hours |
| Free-space admission | Required |
| Remaining free-space reserve | 64 MiB |

The backup manifest is integrity metadata, not a signature. Protect or sign it separately when authenticity is required. Current path checks are not yet a claim of handle-pinned protection against every concurrent namespace swap; platform no-follow hardening and Windows/Linux adversarial-filesystem qualification remain on the roadmap.

## Visible Metadata

Whole-envelope encryption protects the serialized envelope body, including concrete type and encrypted-field dictionary. The file provider still reveals:

- the data root and active-version directory;
- object filenames/storage identities;
- file count, size, and timestamps;
- temporary lock/proposal names while writes are active.

Use opaque identity factories when meaningful identifiers must not appear in filenames.

## Security Verification

The current source tests cover:

- context-bound encrypted round trips and cross-context rejection;
- authentication failure after payload tampering;
- contextless-ciphertext migration and active-key rotation;
- named encryption registration in both orders;
- selected-field encryption and rejection/exclusion from search indexes;
- persistent-catalog purge for encrypted SegmentCore repositories;
- frozen runtime-type allowlist enforcement and default typeless rejection;
- stored-object size admission before file-backed allocation;
- SegmentCore restore manifest, file-count, byte, duration and free-space limits;
- link/reparse and unknown-manifest-path rejection;
- Windows Credential Manager active and historical keys.

Re-run dependency advisories, Windows and Linux tests, clean-package consumption, old-store upgrade, and package-boundary checks before publication:

```powershell
dotnet list InterfaceDB.sln package --vulnerable --include-transitive --no-restore
```

See the root [Security Policy](../SECURITY.md) for private vulnerability reporting and the [preview.9 release notes](preview9-release-notes.md) for migration and rollback.
