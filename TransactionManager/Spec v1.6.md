# Changes in v1.6
- Added planned support for code-supplied delegates that determine both file names and version tags per storage contract.
- Documented that these delegates will be configured alongside the interface attribute metadata, enabling customizable naming strategies without per-call overrides.

# Interface Storage Specification (v1.6 Draft)

## 1. Scope Discovery & Topology
- [StorageScope] continues to mark interfaces for persistence. In addition to VersionTag, the attribute (or associated configuration) may supply:
  - Func<string> **VersionTagFactory**: evaluated at startup to determine the version tag folder name.
  - Func<string, string> **FileNameFactory**: invoked for each write to compute the file name (given the object id) before the serializer extension is appended.
- Because delegates cannot be expressed as attribute parameters directly, the plan is to pair the attribute with registration APIs that accept these factories when wiring the scope (e.g., during AddScopeStorage). Reflective discovery still runs once per assembly set and caches the resulting descriptors.

## 2. Versioned Folder & Envelope Format
- Version tags now originate from the configured VersionTagFactory; default implementation returns the static tag declared in code (e.g., "1.0").
- File names are produced by FileNameFactory(id), defaulting to the normalized id if no factory is registered. The serializer extension is still appended automatically.
- Envelope metadata retains the resolved VersionTag and runtime type information.

## 3. Serialization Pipeline
- Unchanged: serializers remain runtime-aware, with JSON/MessagePack/Encrypted implementations supported out-of-the-box.

## 4. Persistence Services
- IObjectWriter.WriteAsync<T>:
  - Resolves the contract descriptor, calling VersionTagFactory() once per registration (result cached) and the per-write FileNameFactory(id) when composing the target path.
  - Writes directly to <Interface>/<VersionTag>/<FileName><ext> with exclusive access.
- IObjectReader.ReadAsync<T> and ExistsAsync<T> honour explicit version tags; when none is provided they use the descriptor’s cached version tag and fall back to latest folders as before.

## 5. Repository Facade
- SaveAsync<T> still omits version arguments; repository code will simply rely on the descriptor’s cached version tag and file-name factory.

## 6. Dependency Injection
- AddScopeStorage (or follow-up extension methods) will expose configuration hooks for supplying the version/file-name factories during service registration. These delegates are stored in the cached ScopeDescriptor so all scoped services reuse them without repeated reflection or allocation.

## 7. Operational Notes
- Custom factories must return folder/file-safe strings (no path separators, no invalid characters). Validation occurs during descriptor creation.
- Deployments that alter version/file-name factories should be coordinated carefully; migration tooling (hash comparison + scripts) will use the tag factory to determine when upgrades are required.

# Migration Strategy (Future Work)
- Store version/file-name factory metadata (e.g., tags + hash) alongside persisted folders to detect drift.
- Hash mismatches still trigger migration scripts in Migrations/<Interface>/<FromTag>_to_<ToTag>.
- Older binaries encountering unknown tags or filenames will treat them as read-only or prompt the operator to upgrade.

# Roadmap
- **Factory Registration APIs**: Implement DI hooks for configuring VersionTagFactory and FileNameFactory per contract.
- **Migration Hash Storage**: Persist factory metadata to detect drift across deployments.
- **Serializer Overrides per Interface**: Revisit once factories and migrations are in place.
- **Deduplicated Multi-Interface Persistence**: Investigate linking to avoid redundant payloads.
- **Tooling**: Surface diagnostics for conflicting factories, tag mismatches, and concurrency issues.
- **Version Tag/File Name Validation**: Add reusable helpers for validating factory output (length, characters, uniqueness).
