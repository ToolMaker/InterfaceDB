# Changes in V1.1
- Added configurable storage roots and automatic per-scope directory creation.
- Introduced async object writer/reader services with envelope-aware persistence.
- Exposed serializer-specific file extensions across JSON, MessagePack, and encrypted implementations.
- Provided filesystem-backed repository facade with existence checks and version targeting.
- Updated DI bootstrapping to register scope service, reader, writer, and repository.
- Expanded demo program to demonstrate multi-version save/load using the new pipeline.
- Extended `ObjectEnvelope` with stored type metadata and broadened serializer contracts so interfaces and base types can be round-tripped.
- Enhanced `ILocalScopeService` discovery so readers can locate instances assignable to requested interfaces, falling back across compatible scopes.

# Storage + Versioning Specification (v1.2)

## 1. Scope Discovery & Topology
- Assemblies are discovered from `ScopeStorageBuilder.AssembliesToScan`; if empty, all non-dynamic `AppDomain` assemblies are scanned.
- `[StorageScope]` applied to concrete types marks them for persistence.
- `ScopeTreeBuilder` ensures a physical folder exists for each scoped type under a configurable root (default: `<AppContext.BaseDirectory>/Storage`).
- `ILocalScopeService` exposes both type-to-directory and directory-to-type lookups; directories are created on demand, mapped case-insensitively, and enumerable for compatible-type probing.

## 2. File & Envelope Format
- Every stored instance lives in a single file named `<Id>.v<SchemaVersion><SerializerExtension>`.
- `Id` must be file-name safe; the writer generates a GUID when one is not supplied.
- `SchemaVersion` is a positive short; higher versions replace lower ones but co-exist on disk unless explicitly removed.
- The stored payload is always an `ObjectEnvelope` serialized with the active `IObjectSerializer`:
  - `SchemaVersion`: mirrors the version in the file name for quick recovery.
  - `Payload`: raw bytes of the DTO serialized with the same serializer.
  - `TypeName`: assembly-qualified name (when available) of the concrete CLR type written to disk, enabling interface/base reads.
- Serializers advertise their file extension via `IObjectSerializer.FileExtension`.

## 3. Serialization Pipeline
- `IObjectSerializer` defines `Serialize<T>`, `Serialize(object, Type)`, `Deserialize<T>`, and `Deserialize(byte[], Type)` to cover both compile-time and runtime type scenarios.
- Built-ins:
  - `JsonObjectSerializer` => `.json` files (System.Text.Json).
  - `MessagePackObjectSerializer` => `.mpk` files (with composite resolver, LZ4 compression, untrusted-data security profile).
  - `EncryptedObjectSerializer` wraps another serializer and AES-GCM encryptor => `.enc` files.
- Additional serializers must honor the envelope contract and provide a distinct extension. Implementations should delegate generic overloads to the runtime-type variants to keep behavior consistent.

## 4. Persistence Services
- `IObjectWriter.WriteAsync<T>`
  - Validates scope registration and object payload.
  - Captures the runtime CLR type (`obj.GetType()`), serializes DTO -> envelope -> bytes, writing through a deterministic temp file before atomic rename.
  - Returns `ObjectWriteResult` (id, version, type requested, file info, raw envelope).
- `IObjectReader.ReadAsync<T>`
  - Resolves the scope directory; if no direct registration exists for `T`, iterates known scopes to find a stored type assignable to the requested interface/base.
  - Selects the requested or highest available version, deserializes the envelope, resolves the stored CLR type via `TypeName`, and casts to `T` when compatible.
  - Emits telemetry via `ILogger<ObjectReader>`; logging can be disabled with null logger.
- `ExistsAsync<T>` allows cheap probes for a specific version when a scope for `T` exists.

## 5. Repository Facade
- `IRepository` composes reader/writer services to expose `SaveAsync`, `LoadAsync`, and `ExistsAsync` with matching signatures while allowing repository-specific indexing strategies.
- `FileSystemRepository` is the default implementation registered by `AddScopeStorage`/`AddDefaultObjectWriterAndReader`.

## 6. Dependency Injection
- `ScopeStorageServiceCollectionExtensions.AddLocalScopeStorage` wires:
  - The scope service singleton (`ILocalScopeService`).
  - Configurable root directory selection.
  - Assembly scanning for `[StorageScope]` types.
- Additional helper extension `AddDefaultObjectWriterAndReader` registers the MessagePack serializer alongside the repository pipeline. A higher-level convenience method may compose them for common scenarios.
- Applications must register a concrete `IObjectSerializer` (and optional encryptor/key provider) matching their storage requirements.

## 7. Operational Notes
- Temp files use `.<id>.v<version>.<guid>.tmp`; crashes leave debris safe to delete.
- Latest-version reads pick the highest schema version matching `<Id>.v*<ext>`.
- Interface/base-type reads work provided the stored concrete type is assignable to the requested type.
- All IO is asynchronous; callers should honor cancellation tokens for graceful shutdown.
- Logging relies on `Microsoft.Extensions.Logging.Abstractions`; plug in desired providers.

# Roadmap
- **Serializer Selection per Scope**: Allow registering multiple serializers keyed by scope or version.
- **Upgrade Steps**: Formalize `IUpgradeStep` execution for lazy migrations between schema versions.
- **Integrity Checks**: Optional hashing/signing of envelopes to detect tampering.
- **Batch Operations**: Introduce streaming APIs for bulk export/import with progress reporting.
- **Cross-Process Coordination**: File locking or transactional writes for multi-writer scenarios.
- **Test Harness**: Add automated integration tests covering repository operations against temporary storage roots.
- **Interface Scopes**: Extend exists/lookup helpers so interface-first calls avoid scanning when a matching concrete scope is known upfront.

