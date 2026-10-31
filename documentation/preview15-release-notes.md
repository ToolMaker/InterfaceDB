# InterfaceDB 1.0.0-preview.15

Status: published on NuGet.org. See the [current verification receipt](preview15-validation.md).
Update `InterfaceDB`, `InterfaceDB.Storage.SegmentCore`, `InterfaceDB.Authentication` and
`InterfaceDB.Transport` together, then rebuild the application and host.

## Correctness fixes

- Encrypted-field saves preserve value types and explicit null assignments, whether saving a
  read proxy or the original object. File and SegmentCore migrations restore encrypted fields
  before the first migration step. Later steps retain earlier edits and intentional nulls.
- Concrete-implementation queries materialize independent readable objects when encrypted
  proxies are involved. Index discovery and cache attributes use implementation metadata.
  Custom conflict-resolution candidates retain logical encrypted values until storage preparation.
- String range indexes use ordinal ordering consistently across sorted and radix strategies.
- Paged checkpoint enumeration retains its open file handle when old checkpoint files are pruned.
  Full checkpoint publication retains paging while carrying forward concurrent changes.
- A late put cannot resurrect a key after a newer delete. A cancelled or rejected WAL preparation
  records stripes that already received data, so later checkpoints flush them correctly without
  exposing the aborted transaction.

## Authorization and resource handling

- Direct deletes pass through the same configured write-policy validation as batch deletes.
  A policy rejection occurs before the store is called; permitted deletes still work normally.
- Native password work uses a bounded admission queue. Defaults are 32 waiting requests and
  a two-second wait, configurable through `MaximumQueuedPasswordChecks` and
  `PasswordAdmissionTimeout`. Capacity exhaustion returns `Busy`; cancelled or rejected waiters
  do not consume the password-work rate budget. Wrong credentials still fail authentication.
- JWT key refresh survives an individual caller's cancellation. Other callers share the refresh;
  cached-key age limits and successful key rotation remain enforced.
- Subscription control frames have a small allocation bound. Valid idle subscriptions remain
  idle; no application heartbeat or connection pooling is added.
- Delta checkpoints are read incrementally and reject impossible declared counts before large
  allocations. Existing checkpoint encodings, checksums and corruption rejection remain intact.

See [native account configuration](native-account-authentication.md) and
[encryption behaviour](architecture.md#field-level-encryption).

## Field encryption and remote revisions

Encrypted fields now use the existing context-bound encryptor format. The outer IDB6 envelope
format stays unchanged. The context binds the configured repository, contract, final physical
record key and declared field name. File and SegmentCore migrations decrypt using the original
source context before migration code runs. Identity allocation and subsequent saves bind the
result to its destination context. Returned edits remain detached from committed data.

`StoragePolicy.EncryptedFieldWrites` defaults to `EncryptedFieldWriteMode.ContextBound`.
Use `LegacyContextless` only during a reader-first rollout, with explicit legacy acceptance;
see [field upgrade steps](security.md#field-encryption-upgrade-preview15). Older field readers
cannot read new context-bound field ciphertext. Custom encryptors need `IContextualEncryptor`
when context-bound field writes are enabled.

The TCP host owns the repository revision clock. A typed writer requests a bounded revision
range over its exclusive session; the host appends the reserved clock to the same atomic batch
as the records. Rejected writes and abandoned sessions leave durable data and clock unchanged.
The extra reservation is one request/response, not an additional durable disk commit. Revisions
remain monotonic when system time moves backwards; no client-clock skew limit is imposed.

Imports still preserve source revisions. Remote imports require an explicit
`IRepositoryRevisionImportPolicy` on `TcpStoreRegistration`; null denies them. The policy sees
the authenticated identity, alias and server-approved scope. Ordinary saves, local imports and
local-first/remote-first recovery that imports remote revisions into a local store are unaffected.
See [remote revision authority](tcp-repository.md#server-owned-revisions-preview15).

## Upgrade requirements and remaining qualification

Upgrade the TCP host before enabling updated typed writers. Writers negotiate the new
`ReserveRevisions` capability and fail against older hosts rather than falling back to client
clock writes. Older writers cannot modify the protected clock on an updated host. Pause writes
while upgrading applications that share a database. The request frame shape and stored clock
encoding are unchanged; existing data and clocks remain readable. This is a coordinated
client/server feature upgrade, not a rolling-write compatibility promise.

Durability defaults, atomicity, strict revision checks, committed-copy reads, data isolation,
serializer registration and migration ownership remain intact. No performance improvement or
new physical-device qualification is claimed. Windows/Linux CI and the immutable release
workflow passed; the verification receipt records published-package checks. Local packages built
on 30 October predate these changes; use the published preview.15 packages instead.

The [preview.14 release notes](preview14-release-notes.md) describe inherited authentication
and connection features. Older deployments must also follow the preview.13 upgrade requirements.
