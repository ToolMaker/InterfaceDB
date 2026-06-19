# Security And Encryption

InterfaceDB security is layered. The current implementation supports whole-envelope encryption and property-level encryption through interface contracts.

## Security Goals

InterfaceDB aims to protect stored object data while preserving the interface-first programming model.

Current security capabilities:

- encrypted files at rest;
- authenticated AES-GCM payloads;
- key IDs for historical key lookup;
- key rotation;
- Windows Credential Manager key storage;
- selected field encryption inside the envelope;
- plaintext backing-field clearing after encrypted-field save;
- interface proxy decryption on property access.

## Whole-Envelope Encryption

Whole-envelope encryption runs after serialization:

```text
object -> envelope -> MessagePack bytes -> encrypt -> storage
storage -> decrypt -> MessagePack bytes -> envelope -> object
```

Plain files use internal `IDB*` envelope headers. AES-GCM encrypted files use an outer `IDBE` frame.

Register encryption before the repository:

```csharp
services.AddInterfaceDbAesGcmEncryption(keyProvider);
services.AddRepository<IPerson>(personOptions);
```

Custom encryption can implement `IEncryptor`.

## Key Storage

The attribute should not contain secrets. Attribute parameters should be profile names only:

```csharp
[EncryptedStorage("PeopleSecure")]
public interface IPerson : IStoredContract<IPerson>
{
    [EncryptedStorageField("NationalId")]
    string NationalIdentifier { get; set; }
}
```

The profile name points to configuration or DI:

```json
{
  "InterfaceDb": {
    "EncryptionProfiles": {
      "PeopleSecure": {
        "Provider": "WindowsCredentialManager",
        "KeyName": "InterfaceDb.People.Active"
      },
      "NationalId": {
        "Provider": "WindowsCredentialManager",
        "KeyName": "InterfaceDb.People.NationalId"
      }
    }
  }
}
```

Current implementation stores the profile name with encrypted field metadata. A full profile resolver is a roadmap item.

## Windows Credential Manager

`WindowsCredentialEncryptionKeyStore` stores active and historical keys for the current Windows user.

```csharp
var keyStore = new WindowsCredentialEncryptionKeyStore("People");
keyStore.AddOrUpdateKey("key-2025", oldKey);
keyStore.AddOrUpdateKey("key-2026", currentKey);
keyStore.SetActiveKey("key-2026");
```

Then register:

```csharp
services.AddInterfaceDbWindowsCredentialEncryption(
    "People",
    new AesGcmEncryptorOptions
    {
        LegacyKeyId = "key-2025",
        RotationMode = EncryptionRotationMode.ReencryptOnRead
    });
```

## Key Rotation

Key-aware encrypted payloads store a non-secret key ID. That lets InterfaceDB select the correct historical key directly.

Rotation modes:

- `ReadHistoricalOnly`: read old payloads without rewriting.
- `ReencryptOnRead`: decrypt with the historical key and write once with the active key.
- forced rotation through `IEncryptionRotationOperation<TContract>.RotateAllAsync`.

Historical keys must remain available until every payload that references them has been rewritten.

## Field-Level Encryption

Field-level encryption is declared on interface properties:

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

Write behavior:

1. InterfaceDB detects encrypted properties from the contract.
2. The property value is serialized and encrypted.
3. Ciphertext is stored in `Envelope.EncryptedFields`.
4. The concrete backing property is cleared to its default value.
5. The object payload is serialized without the plaintext field value.

Read behavior:

1. The object and envelope are loaded.
2. InterfaceDB returns an interface proxy.
3. Normal properties delegate to the concrete object.
4. Encrypted property getters decrypt the envelope field value on demand.
5. Encrypted property setters encrypt the new value and clear the concrete backing property.

This reduces long-lived plaintext in memory. It does not make process memory impossible to inspect. Any value that application code reads must briefly exist as plaintext.

## Tamper Detection Direction

AES-GCM already authenticates encrypted payloads. Future non-encrypted or mixed-mode stores should add envelope integrity signatures/HMACs so external edits can be detected before materialization.

Potential future features:

- envelope HMAC/signature for plaintext storage;
- device/user-bound keys;
- profile-based unlock policies;
- app-mediated file opening with passphrase or external approval;
- secure document viewer flow for double-clicked encrypted objects;
- field-level provider selection through encryption profiles.

## Secure File Access Concept

A future secure-file mode can use InterfaceDB as the registered file handler:

1. User opens an encrypted InterfaceDB-backed file.
2. The app reads non-secret metadata.
3. The app asks for passphrase, device approval, or external key release.
4. The key provider unlocks the active key.
5. InterfaceDB decrypts and displays the object/document.

This is a good fit for sensitive document storage. It is separate from runtime anti-cheat and should be positioned as app-mediated encrypted object access.
