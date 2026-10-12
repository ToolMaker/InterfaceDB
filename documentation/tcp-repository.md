# Online TCP repository

The optional MIT `InterfaceDB.Transport` package supplies `TcpRepository<TContract>` using
the existing `IRepository<TContract>` API. Install version `1.0.0-preview.11` and keep all
InterfaceDB package versions aligned. See the [release status and upgrade notes](preview11-release-notes.md).

```text
Application -> TcpRepository<T> -> TLS TCP -> one server database -> persistent server files
            <- disposable pub/sub refresh hints <- server
```

The client has no persistent local database and no offline write queue. Each operation uses
a bounded in-memory snapshot and indexes. A disconnected read or write fails; the connector
does not switch to a local repository. The host runs the storage engine and owns its files.
An unversioned host stores opaque envelopes. A versioned host deploys trusted validation and
migration code with the server.

## Supported behavior

Each storage API call opens TLS TCP with the signed-in user's JWT. The server validates signature, exact
issuer, audience, expiry and configured scopes, then derives a partition from the validated
**issuer plus subject**. Client keys, cursors and claimed parent IDs cannot select another partition.

Under the host's exclusive subject lease, the client reads a bounded fresh snapshot, rebuilds indexes,
performs the operation and closes the session. Enumerations buffer results before yielding to
application code. A second client sees completed changes on its next call.

Returned entities carry revisions. A later save reloads current state under a new lease and checks
that exact revision. Two clients updating the same revision produce one success and one
`WriteConflictException<TContract>`. Its `ReadCurrentAsync` opens a fresh operation and can return
a newer revision or null after deletion. Reuse the saved entity for updates; a fresh DTO is an insert.

Envelope mutations and a private revision-clock record commit in one atomic batch. The clock
survives deletion and host/container recreation. Success acknowledges durable engine completion.
`RemoteRepositoryCommitOutcomeUnknownException.AttemptedRevisions` exposes the attempted keys
and revisions (null for deletion), including insert keys whose envelopes were not published.
Read fresh state and reconcile before another mutation. Later writes/deletion can prevent proving
the historical outcome. There is no automatic mutation replay or durable deduplication.

## Application integration

Reference `InterfaceDB.Transport/InterfaceDB.Transport.csproj` while the new package is unreleased. Preserve interface contracts, MessagePack implementations, indexes and normal
`IRepository<T>` calls. Supply an explicit runtime type allowlist.

Adapt the application's existing signed-in user token store:

```csharp
using Idb.Authentication;
sealed class UserAccessTokenProvider(
    Func<CancellationToken, ValueTask<string>> getCurrentUserAccessToken) : IAccessTokenProvider
{
    public async ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken token = default)
        => new(await getCurrentUserAccessToken(token));
}
```

The function retrieves/refreshes the **user access token** through the existing OAuth/OIDC SDK.
No service-account secret belongs in a native app. Login and its UI remain application work.

```csharp
using Idb.Transport;
using Idb.Transport.Repositories;

var options = new TcpRepositoryOptions<IMyContract>
{
    Contract = new ContractConfig(),
    RuntimeTypes = new(typeof(ContractImplementation)),
    Connection = new()
    {
        Host = "storage.example.test", Port = 7443, StoreAlias = "my-contract",
        Manifest = manifest, // Exact host compatibility identity.
        AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
        TokenProvider = userAccessTokenProvider
    }
};
await using var repository = new TcpRepository<IMyContract>(options);
```

`AddTcpRepository<TContract>(options, databaseKey)` registers a keyed `IRepository<TContract>` and
an unkeyed default if absent. It uses singleton lifetime in that service provider: use a provider
owned by one signed-in account, dispose asynchronously on logout, and never share it across users
in a server application. Explicit construction/disposal is also supported.

The repository pins the first token's issuer/subject to guard account switching. Same-account
refresh is accepted; a different account needs a new repository. This client parse supplies no
authorization: the server independently validates every connection. Retain TLS chain, hostname
and server-authentication checks when configuring a private CA.

## Reusing the configured envelope serializer — preview.11

TCP now resolves a configured `IObjectSerializer<TContract>` instead of constructing a
MessagePack serializer for every operation. Existing callers keep the MessagePack default.
The serializer must also implement `IRevisionObjectSerializer<TContract>`.

For direct construction, set `TcpRepositoryOptions<TContract>.Serializer` to the fully
configured serializer. TCP borrows it and does not dispose it. With dependency injection,
register one serializer under the TCP database key before `AddTcpRepository`, or explicitly
select a serializer already registered for storage:

```csharp
services.AddKeyedSingleton<IObjectSerializer<IMyContract>>("local", serializer);
services.AddSegmentCoreRepository<IMyContract>(localConfig, encryptor: encryptor,
    runtimeTypeAllowlist: allowedTypes); // localConfig.EnvelopeDICacheKey == "local"

services.AddTcpRepository(tcpOptions, databaseKey: "remote", serializerKey: "local");
```

The local provider and TCP share that exact instance. Different format implementations can
be registered under different keys. A selected key must resolve exactly one serializer;
missing or duplicate registrations fail. An explicit `serializerKey` never falls back to
MessagePack. Choose either an options-supplied serializer or a DI-supplied serializer.

The selected format identity is `SerializerEnvelopeFormat`; its default preserves
`IDB6-TCP-Repository-v1`. For another format, explicitly supply its serializer and matching
`Connection.Manifest.EnvelopeFormat`. The server manifest must match too. The format name
is configuration, not code attestation. The deployed server version validator/migrations
must understand the selected format. New formats have distinct cache identities; existing
default-format cache identities remain unchanged.

For encryption, supply a fully configured `IContextualObjectSerializer<TContract>` using
the same `IEncryptor` instance as `tcpOptions.Encryptor`. The public
`EncryptingObjectSerializer<TContract>` can now wrap any revision-aware serializer.
Configuration fails if an encrypted repository receives a contextless serializer or a
different encryptor policy. Authenticated context stays specific to each storage location;
sharing a serializer does not make encrypted files transferable across locations.

TCP applies the smaller of its storage policy and negotiated payload limit before custom
deserialization and after serialization, and retains its aggregate snapshot limits.
Shared serializer instances must support concurrent callers. Custom serializers are trusted
code and remain responsible for safe decoding and runtime-type restrictions.

Built-in version migrations remain MessagePack-specific: another format requires its own
verified migration implementation. TCP control frames still use their existing MessagePack protocol independently
of envelope payload encoding.

This abstraction is implemented in source; no Preview 11 publication or new serializer
implementation is included in this change.

## Host and Keycloak

Use the [typed sample](../samples/InterfaceDB.TcpRepository.Client/README.md) on a **new alias and
volume/directory**. Both sides explicitly select `JwtBearer`. Set host `isolateBySubject: true`.
The typed client requires `SubjectIsolation` and refuses an older/nonisolating host.

Issue user access tokens with the exact realm issuer, stable nonempty `sub`, a dedicated database
audience (for example `interfacedb-storage`), approved scope (for example `interfacedb.storage`),
configured signing algorithm, expiry and access-token discriminator.

A dedicated database audience can be added to the existing user token with an authorized audience
mapper/client scope; it need not introduce a second login. A separate public native client is
another option. The library does not accept an unrelated API audience as a substitute.

Preserve Keycloak's `basic` scope/subject mapper. Omitting it from an imported client can produce
user tokens without `sub`; see the official [Keycloak migration notes](https://github.com/keycloak/keycloak/blob/main/docs/documentation/upgrading/topics/changes/changes-25_0_0.adoc).
Tokens without a subject are deliberately rejected.

The application owns parent/child associations, device permissions and cross-account sharing. A child device principal does not
inherit its parent's partition. Delegated access needs an application-owned authorization boundary.

The sample grants its configured scope all operations within the principal partition. Compatibility
manifests prevent accidental mismatches; they do not attest client code. An unversioned encoded store
still accepts opaque bytes, so domain validation there requires an application service. A
`VersionedTcpStore` validates every live non-clock upsert with its active trusted version module,
including all batch entries before commit, and revalidates an active generation on reopen.
Revision-conflict guarantees apply to cooperating repository clients.

## Limits and deferred work

- One host owns a physical volume. Non-isolated stores have one active session per alias;
  subject-isolated JWT stores have one active session per validated issuer/subject. Every storage
  session stops admitting operations after five minutes by default; in-flight work is cancelled and
  must settle before its lease is released.
  Do not run independent writable instances over the same volume behind a load balancer.
- Each call transfers the principal's entire snapshot and rebuilds indexes. Defaults: 10,000
  envelopes and 64 MiB encoded bytes including clock metadata. Materialized objects/indexes and
  transport buffers need additional memory. No throughput/mobile-device qualification is claimed.
- Writes exceeding configured snapshot limits fail before dispatch. The host also rejects new keys
  before the SegmentCore total capacity (100,000 by default) or authenticated-subject capacity
  (10,000 by default) is crossed. Existing keys remain readable and overwrites/deletes remain
  available at capacity. Keep client snapshot limits consistent with the server subject capacity.
- Default transport batches contain 64 items including one clock record: up to 63 entities.
  Negotiated smaller limits and frame/payload ceilings apply; batches are not silently split.
  Subject prefixes consume 71 physical UTF-8 key bytes; the host advertises the remaining budget.
- Only rejected `Busy` admission is retried. Predicates run while the lease is held: keep them fast
  and avoid nested calls to the same repository. `SubscribeChanges` supplies a separate disposable push listener.
- Exact manifests/versions are required. Use `EnvelopeFormat = "IDB6-TCP-Repository-v1"` and align
  `ContractVersion` with `ActiveVersion`. Editing a manifest does not migrate raw/local data.
  Remote migration needs separately staged, verified targets and explicit cutover.
- Direct `TcpRepository<T>` does not perform remote migrations. `VersionedTcpStore` supplies trusted server startup migration. Retained editing, per-entity event snapshots and distributed domain merging remain unsupported.
- Keycloak password acquisition is confined to disposable test users in an isolated test realm.
  It verifies real user JWTs and storage, not browser PKCE/native login. The app owns its login flow.
- Total/per-subject quotas are implemented; deployment sizing, backup operations, cloud and intended-device qualification remain application/operator work.

## Pub/sub and reconnection

```csharp
await using var changes = repository.SubscribeChanges(async () =>
{
    // Refresh the application's display from the remote database.
    var current = await repository.QueryFirstAsync(_ => true);
    // Dispatch current to your application/UI using its own lifetime and threading rules.
});
await changes.Ready; // Registration is acknowledged before the initial catch-up read.
var initial = await repository.QueryFirstAsync(_ => true);
```

The first callback requests catch-up. Later callbacks are coalesced current-state hints; they
do not contain every intermediate event or create a local database. Observe `Completion`
and handle its failure; `DisposeAsync` drains the listener and can surface the same failure:
disconnect, token expiry, authorization failure or a callback error ends the subscription.
Dispose it, obtain a fresh token if required, explicitly register again, await `Ready`, and
refresh from remote. Use bounded backoff for connection failures. Healthy subscriptions
perform no storage polling. Disposing the repository also stops its subscriptions.

## Server versions

`VersionedTcpStore.OpenAsync` selects the latest trusted version deployed on the server.
It migrates into a separate generation, validates records and publishes the active pointer
before the host accepts requests. Failed upgrades preserve the old generation. An existing
unversioned root requires explicit adoption into a separate version root. Back up before upgrade.
The sample host accepts an operator-deployed `ITcpStoreVersionModule`; inspect its configuration
and module-loading code in the [host](../samples/InterfaceDB.TcpStore.Host/Program.cs).

`TcpEncodedEnvelopeStore.DiscoverAsync` authenticates and returns the server manifest. The
application must have matching contract/serializer code and construct matching options before
access; no client-supplied migration code runs on the host. Deploy clients that understand the
current and next baseline before upgrading the server. A direct client with an incompatible
manifest receives `IncompatibleManifest`; it cannot silently use local data or upgrade itself.

## Verification

Run `dotnet test InterfaceDB.Transport.Tests -c Release` for online CRUD, revision conflicts,
authentication, isolation, serializer selection, push lifetimes, server migrations and limits.
Explicitly run `tests/InterfaceDB.Authentication.KeycloakTests/run-typed-tests.ps1` for isolated
Docker/Keycloak qualification and `tests/InterfaceDB.Transport.DockerTests/run-tests.ps1` for
storage/migration qualification. These use their own test resources.
