# Security And Encryption

InterfaceDB can encrypt complete envelopes or selected contract properties. Encryption is opt-in; the default `EncryptionNone` provider writes plaintext `IDB5` payloads.

## Security Boundary

The implemented controls protect persisted payload contents against offline inspection and detect ciphertext/header modification when AES-GCM is configured. They do not provide:

- operating-system account isolation or file ACL management;
- protection after an attacker can execute code in the application process;
- secure deletion from SSDs, backups, snapshots, or synchronized copies;
- encrypted filenames, directory names, file sizes, or modification times;
- distributed authorization, audit logging, or remote access control;
- automatic key escrow or recovery;
- a formal third-party cryptographic audit.

Use normal OS permissions, backup controls, process hardening, and secret-management practices around the library.

## Whole-Envelope Encryption

Whole-envelope encryption is applied after MessagePack serialization:

```text
object -> envelope -> IDB5 bytes -> AES-GCM -> IDBE file
IDBE file -> AES-GCM -> IDB5 bytes -> envelope -> object
```

`AesGcmEncryptor` uses:

- a cryptographically random nonce for every encryption;
- a 16-byte authentication tag;
- 128-, 192-, or 256-bit AES keys;
- authenticated header bytes, including framing and key ID;
- `IDBE` framing with keyed and legacy formats.

Authentication failures throw `CryptographicException`; corrupted ciphertext is not returned as an object.

Register a key provider before the repository:

```csharp
services.AddInterfaceDbAesGcmEncryption(keyProvider);
services.AddRepository<IPerson>(personOptions);
```

For keyed databases, use the same registration key for encryption and repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption("SecurePeople", keyProvider);
services.AddRepository<IPerson>(secureOptions, "SecurePeople");
```

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
services.AddRepository<IPerson>(personOptions);
```

This provider is Windows-specific. Other operating systems currently require a custom `IKeyProvider`/`IRotatingKeyProvider`; platform keychain, vault, and HSM providers remain roadmap work.

Do not place key bytes, passwords, or tokens in attributes, source control, `appsettings.json`, command-line arguments, or filenames.

## Key Rotation

Key-aware payloads record the key ID used for encryption. Two rotation paths are implemented:

- `EncryptionRotationMode.ReencryptOnRead` rewrites an old payload after successfully reading it with a historical key. Replacement uses compare-before-replace behavior so a concurrent write is not overwritten.
- `IEncryptionRotationOperation<TContract>.RotateAllAsync` acquires the migration/write scope and rewrites every payload that does not use the active key.

Legacy version-1 `IDBE` payloads do not contain a key ID. Configure `LegacyKeyId` when a rotating provider must read them.

Keep a verified backup and every referenced historical key until a full rotation and reopen validation have succeeded.

## Field-Level Encryption

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

Field-level encryption does not encrypt opaque `Envelope.ExtensionMetadata`. Without whole-envelope
AES-GCM, extension metadata is stored as plaintext in the `IDB5` payload. Do not place secrets there,
or enable whole-envelope encryption for the repository.

Current limitations:

- marked properties must have public get and set accessors;
- callers must use the returned interface/proxy instance to obtain encrypted values;
- profile names are persisted as labels, but a per-profile encryptor resolver is not implemented yet;
- the currently registered repository encryptor performs the actual field encryption;
- plaintext necessarily exists in application memory while a value is being used.

## Plaintext Rollout

`AesGcmEncryptorOptions.AllowUnencryptedPayloads` permits old plaintext `IDB*` files to be read during an encryption rollout. New writes still use `IDBE`.

This option weakens enforcement because any syntactically valid plaintext envelope is accepted. Enable it only for a controlled migration window, rotate/rewrite the complete store, verify the result, and then disable it.

## Visible Metadata

Whole-envelope encryption protects the serialized envelope body, including concrete type and encrypted-field dictionary. The file provider still reveals:

- the data root and active-version directory;
- object filenames/storage identities;
- file count, size, and timestamps;
- temporary lock/proposal names while writes are active.

Use opaque identity factories when meaningful identifiers must not appear in filenames.

## Security Verification

The integration harness covers:

- encrypted round trips;
- authentication failure after payload tampering;
- lazy direct-to-active-key rotation;
- selected-field proxy encryption/decryption;
- forced full-store rotation;
- legacy encrypted payload lookup;
- Windows Credential Manager active and historical keys;
- encrypted eager migration.

The repository dependency audit reported no known vulnerable direct or transitive NuGet packages on 2026-08-23. Re-run the advisory check before every public release:

```powershell
dotnet list InterfaceDB.sln package --vulnerable --include-transitive --no-restore
```

See the root [Security Policy](../SECURITY.md) for vulnerability reporting.
