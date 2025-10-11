# Changes in v1.3
- Shifted storage scopes to interface-first: [StorageScope] now targets interfaces/contracts instead of concrete types.
- Directories are created per interface scope while persisted envelopes continue to store concrete type metadata.
- Scope discovery records all concrete implementations for each scoped interface via reflection at startup.
- Writer/reader pipeline validates interface assignments and resolves directories from interface contracts.
- Multi-interface implementations are supported; a single concrete object can be persisted under each scoped interface it implements.
- Deferred schema-versioning features until a full migration plan is established.
- Version identifiers moved into sub-folders (<Contract>/<SchemaVersion>/<Id><ext>), replacing the previous .v<Schema> filename suffix.
- Storage contracts can declare a default schema version via [StorageScope(Version = "1.0")], which writers/readers honour when callers omit explicit versions.

# Interface Storage Specification (v1.3 Draft)

## 1. Scope Discovery & Topology
- [StorageScope] must decorate interfaces or abstract contracts intended for persistence.
- The attribute optionally carries a Version string (e.g., "1.0"); when omitted, implementations fall back to 1.0.
- ScopeStorageBuilder scans configured assemblies (or all non-dynamic AppDomain assemblies) to gather interface scopes.
- For each scoped interface, reflection identifies all loaded, concrete, non-abstract types that implement the interface.
- ScopeTreeBuilder provisions a directory per interface scope under the configured root and records the default version alongside discovered implementations.
- ILocalScopeService exposes:
  - Interface-to-directory map.
  - Directory-to-interface reverse lookup.
  - Enumeration of known scopes with their admissible implementation types and declared default version.

## 2. Versioned Folder & Envelope Format
- Each contract directory contains version sub-folders named with the schema version (e.g., 1.0, 2, 2024.09).
- Stored files live at <Contract>/<SchemaVersion>/<Id><SerializerExtension>.
- ObjectEnvelope fields:
  - SchemaVersion: matches the version folder name used during persistence.
  - Payload: serialized bytes produced by the active IObjectSerializer.
  - TypeName: assembly-qualified name of the concrete CLR type persisted.

## 3. Serialization Pipeline
- IObjectSerializer contract remains runtime-aware with Serialize(object, Type) / Deserialize(byte[], Type).
- MessagePack is the baseline storage serializer; Json or Encrypted variants remain optional opt-in components.

## 4. Persistence Services
- IObjectWriter.WriteAsync<TContract>:
  - Validates that TContract is a registered interface scope.
  - Ensures the supplied object implements TContract.
  - Uses the contract?s declared default version when the caller omits schemaVersion; otherwise honours the explicit version parameter.
  - Creates (or reuses) the version sub-folder and serializes <Id><ext> within it.
- IObjectReader.ReadAsync<TContract>:
  - Resolves the interface directory, then either the requested version or the highest available version when unspecified.
  - Rehydrates the concrete implementation using TypeName; returns the value cast to TContract.
- ExistsAsync<TContract> checks the requested version folder, defaulting to the contract?s version when none is provided.

## 5. Repository Facade
- IRepository.SaveAsync<TContract> / LoadAsync<TContract> operate in terms of interface contracts and apply the default version semantics above.

## 6. Dependency Injection
- AddScopeStorage registers interface scope discovery and directory provisioning, producing a ScopeDescriptor per contract with directory, implementations, and default version.
- Writer/reader/repository consume descriptors to create version folders and resolve files.

## 7. Operational Notes
- Interface folder names match the interface type name (e.g., IPerson).
- Version folders should be numeric/semantic strings parseable by the reader; the attribute value must follow that convention.
- Schema version metadata remains in the envelope.
- Multi-interface implementations result in separate versioned folders per contract.

# Roadmap
- **Implementation Registry Options**: Allow explicit registration of permitted concrete types.
- **Schema Migration Plan**: Design upgrade pipeline now that version metadata is embedded in directories.
- **Serializer Overrides per Interface**: Revisit once the interface storage flow stabilizes.
- **Deduplicated Multi-Interface Persistence**: Investigate cross-interface linking to avoid redundant payloads.
- **Tooling**: Provide diagnostics for missing implementations or ambiguous mappings.
- **Version Folder Conventions**: Consider enforcing stricter parsing/validation.

