# InterfaceDB 1.0.0-preview.11

Status: **release approved; publication verification pending**. All four public packages are version `1.0.0-preview.11`.

## Serializer abstraction groundwork

- Added `IContextualObjectSerializer<TContract>` so storage helpers preserve authenticated
  context through a configured serializer abstraction.
- Added an encryption-wrapper constructor accepting any `IRevisionObjectSerializer<TContract>`
  with an explicit payload limit.
- Retained the existing MessagePack constructor and stored formats.
- Added regression coverage for custom serializers, authenticated context and size limits.
- TCP metadata acknowledgements reuse their materialized snapshot instead of decoding the
  same envelope twice. Exact call-count tests cover writes, reads, batches, deltas and pub/sub
  with default, custom and encrypted serializers; metadata staging preserves committed state.

See [serializer registration](serializer-configuration.md) for configuration and boundaries.
No new serialization format or gRPC transport is implemented by these changes.

## Public online TCP packages

- `InterfaceDB.Transport`: direct `IRepository<T>` access to one remote database, TLS, atomic
  batches, strict revisions, pub/sub hints, current-state deltas and trusted server versioning.
- `InterfaceDB.Authentication`: JWT validation and supplied/service access-token providers.
- Both packages and their host/client examples are MIT. Embedded applications need neither.
- The connector creates no persistent client database, performs no offline fallback and queues
  no disconnected writes. Applications own subscription registration/re-registration and refresh.
- Existing MessagePack serialization remains MIT. No Protobuf serializer or gRPC adapter is added.
- Public namespaces are `Idb.Transport`, `Idb.Transport.Repositories` and `Idb.Authentication`.
  Shared notification/cursor interfaces use `Idb.Libraries.Repositories`. Earlier private source
  consumers must rebuild and update namespace imports; the TCP wire and envelope formats are unchanged.

See [online integration](tcp-repository.md), [hosting](tcp-transport.md) and [JWT setup](jwt-authentication.md).
Publication is being prepared through the tag-triggered release workflow. This release does not depend on a future gRPC implementation.

## Upgrade from preview.10

1. Update all referenced InterfaceDB packages to `1.0.0-preview.11` and rebuild. Embedded applications can continue using only core and SegmentCore.
2. Add `InterfaceDB.Transport` only for remote database access; it includes the public authentication dependency. Configure server TLS, JWT validation, authorization, contract manifests and runtime-type allowlists.
3. Update earlier source consumers from `Idb.Commercial.Transport` to `Idb.Transport`, from `Idb.Commercial.Transport.Repositories` to `Idb.Transport.Repositories`, and from `Idb.Commercial.Authentication` to `Idb.Authentication`.
4. The default envelope (`IDB6`), encrypted frame (`IDBE` v3) and TCP control-frame formats are unchanged. Switching to a custom serializer is a separate format conversion; registration alone never migrates data.
5. From preview.8 or earlier, first follow the [preview.10 upgrade checklist](preview10-release-notes.md#upgrade-checklist), including the required encrypted-store rewrite. Preserve a verified backup and encryption keys.
6. Preview.10 has no published TCP/auth packages. Rolling back an application using the new packages requires the matching prior application/deployment; preserve backups and do not downgrade a migrated server contract.

[Source validation](preview11-validation.md): public regressions, package boundaries and clean consumption.
