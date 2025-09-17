# Changes in v1.3
- Shifted storage scopes to interface-first: `[StorageScope]` now targets interfaces/contracts instead of concrete types.
- Directories are created per interface scope while persisted envelopes continue to store concrete type metadata.
- Scope discovery records all concrete implementations for each scoped interface via reflection at startup.
- Writer/reader pipeline validates interface assignments and resolves directories from interface contracts.
- Multi-interface implementations are supported; a single concrete object can be persisted under each scoped interface it implements.
- Deferred schema-versioning features until a full migration plan is established.
- **New**: Version identifiers moved into sub-folders (`<Contract>/<SchemaVersion>/<Id><ext>`), replacing the previous `.v<Schema>` filename suffix.

# Interface Storage Specification (v1.3 Draft)

## 1. Scope Discovery & Topology
- `[StorageScope]` must decorate interfaces or abstract contracts intended for persistence.
- `ScopeStorageBuilder` scans configured assemblies (or all non-dynamic AppDomain assemblies) to gather interface scopes.
- For each scoped interface, reflection identifies all loaded, concrete, non-abstract types that implement the interface.
- `ScopeTreeBuilder` provisions a directory per interface scope under the configured root (default: `<AppContext.BaseDirectory>/Storage/<InterfaceName>`).
- `ILocalScopeService` exposes:
  - Interface-to-directory map.
  - Directory-to-interface reverse lookup.
  - Enumeration of known scopes with their admissible implementation types.

## 2. Versioned Folder & Envelope Format
- Each contract directory contains version sub-folders named with the schema version (e.g., `1.0`, `2`, or `2024.09`).
- Stored files live at `<Contract>/<SchemaVersion>/<Id><SerializerExtension>` (no `.v` suffix).
- `ObjectEnvelope` fields:
  - `SchemaVersion`: matches the version folder name used during persistence.
  - `Payload`: serialized bytes produced by the active `IObjectSerializer`.
  - `TypeName`: assembly-qualified name of the concrete CLR type persisted.
- Serializers remain global; no per-interface overrides yet.

## 3. Serialization Pipeline
- `IObjectSerializer` contract remains runtime-aware with `Serialize(object, Type)` / `Deserialize(byte[], Type)`.
- Built-in implementations (Json, MessagePack, Encrypted) continue to operate unchanged.
- When deserializing for an interface request, the reader resolves the stored `TypeName` to reconstruct the concrete object.

## 4. Persistence Services
- `IObjectWriter.WriteAsync<TContract>` (interface-oriented):
  - Validates that `TContract` is a registered interface scope.
  - Ensures the supplied object implements `TContract`.
  - Creates (or reuses) the version sub-folder, then serializes the object into `<Id><ext>` within that folder.
  - Multi-interface objects can be written multiple times—once per target scope—if the caller invokes write for each interface.
- `IObjectReader.ReadAsync<TContract>`:
  - Resolves the interface directory, then the requested version sub-folder (or discovers the highest version when unspecified).
  - Rehydrates the concrete implementation using `TypeName`; returns the value cast to `TContract`.
  - If the caller requests an interface with no direct scope, the read fails fast.
- `ExistsAsync<TContract>` checks for `<Id>` inside the chosen version folder.

## 5. Repository Facade
- `IRepository.SaveAsync<TContract>` / `LoadAsync<TContract>` operate in terms of interface contracts and surface versioned-folder semantics to callers.

## 6. Dependency Injection
- `AddScopeStorage` (or equivalent) registers interface scope discovery and directory provisioning, producing a `ScopeDescriptor` per contract.
- Writer/reader/repository consume descriptors to create version folders and resolve files.

## 7. Operational Notes
- Interface folder names match the interface type name (`IPerson`, `IOrder`); document any naming convention if a leading `I` is undesirable.
- Version folders can be numeric (`1`, `2`) or semantic (`1.0`, `2024.09`); consistently use the format understood by the reader when sorting latest.
- Schema version metadata remains in the envelope for forward compatibility.
- When a concrete type implements multiple scoped interfaces, persisting it via different interfaces results in separate versioned folders per contract.

# Roadmap
- **Implementation Registry Options**: Allow explicit registration of permitted concrete types (beyond reflection discovery) for tighter control.
- **Schema Migration Plan**: Design upgrade pipeline now that version placeholders exist and directories house versions.
- **Serializer Overrides per Interface**: Re-evaluate once the core interface storage flow stabilizes.
- **Deduplicated Multi-Interface Persistence**: Investigate cross-interface linking to avoid storing identical payloads multiple times.
- **Tooling**: Provide diagnostics for missing implementations or ambiguous interface mappings.
- **Version Folder Conventions**: Consider enforcing numeric/semantic version parsing with validation helpers.
