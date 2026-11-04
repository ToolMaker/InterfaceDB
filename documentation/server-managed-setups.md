# Server-approved managed repositories

Available under MIT in `InterfaceDB.Transport.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

## Names and interfaces

`LocalRemoteRepositoryFactory` is the provider-independent management entry point.
`LocalRemoteRepository<T>` consumes `IRepositoryVersionAuthority` and `IRepositoryVersionPlan<T>`.
The factory composes `IManagedLocalRepositoryProvider<T>`, `IManagedRemoteRepositoryProvider<T>`
and `IManagedRepositorySettings`. Providers return the existing `IRepository<T>` interface;
composition additionally requires durable, conditional envelope metadata and change subscriptions.
The coordinator does not downcast to a storage engine implementation.

| Adapter | Provider identifier | Delivery |
| --- | --- | --- |
| `FileManagedProvider<T>` | `file` | `InterfaceDB.Transport.Repositories` |
| `SegmentCoreManagedProvider<T>` | `segment-core` | `InterfaceDB.Transport.Repositories` |
| `AtlasManagedProvider<T>` | `atlas` | Optional `InterfaceDB.Transport.Atlas` project |
| `TcpManagedRemoteRepositoryProvider<T>` | Remote endpoint adapter | `InterfaceDB.Transport.Repositories` |

`SegmentCoreManagedRepository.Create` remains a compatibility wrapper over the generic factory.
Its old client-selected settings are not the new server-managed setup flow. SegmentCore itself
remains the name of the segmented storage engine; it is not the management coordinator.

### Source API naming update

The current source uses `LocalRemoteRepository<T>` and `LocalRemoteRepositoryFactory`, renamed
from `ManagedRepository<T>` and `ManagedRepositoryFactory` on 13 October 2026. Update explicit
type references, constructors and factory calls, then rebuild consuming applications. The
namespace remains `Idb.Transport.Repositories`. Register the composed instance as `IRepository<T>`
or `IResilientRepository<T>` according to the capabilities the application needs; consumers should
depend on these abstractions. `ManagedRepositoryModel<T>` and the provider/settings interfaces
retain their existing names.

The current API uses provider-independent names. It does not change
contract identities, setup names, cache directories, stored envelopes, cursor keys or wire formats.
The existing modes synchronize with durable remote storage. The separately implemented
`PeerReplicatedRepository<T>` exchanges device-held history through its own relay; see
[the peer guide](peer-replicated-repository.md).

## What belongs on each side

The server configures client setups by **contract interface identity + setup name**. Each specifies the permitted local provider,
LocalFirst/RemoteFirst mode, compatible contract manifest, opaque data alias, optional data selector and policy revision.
The private registration also holds the actual `IEncodedEnvelopeStore` and an authorization
delegate. It is never serialized. A File client can connect to a SegmentCore server or another
server provider implementing the encoded-store contract; these are independent choices.

The client's repository bootstrap identifies the endpoint, contract and requested setup name.
For an application with one setup per contract, the composition root can supply the setup name
from that contract. Request each configured repository separately: there is no discovery catalogue.
An optional requested provider is an assertion that must match the server's approval, not an override.

Use a stable, namespace-qualified interface identity in `Manifest.ContractIdentity`, for example
`Example.Contracts.IProfile`. Setup names are unique within that interface. The same server
can register `IProfile/mobile` and `IStory/mobile`, both using SegmentCore, with different read
modes and policy revisions. It can also register several setups for one interface. The provider
identifier (`file`, `segment-core`, `atlas`) selects an implementation; it is not the configuration key.
The wire setup alias is derived from both identity fields, and duplicate pairs are rejected.
Existing deployed contract identities must remain stable: renaming one requires an explicit transition.

Conceptually, server configuration has this hierarchy (this is not a JSON binding API):

```text
Example.Contracts.IProfile
  mobile  -> segment-core, RemoteFirst, profile policy
  desktop -> file, LocalFirst, desktop policy
Example.Contracts.IStory
  mobile  -> segment-core, LocalFirst, story policy
```

Each pair has its own authorization check. Permission to use `IProfile/mobile` gives no permission
to use `IStory/mobile`. The client receives only its approved projection; it cannot list the hierarchy.

JWT acquisition, TLS trust, setup-signature verification, an account-owned cache directory,
entity mappings, allowed runtime types, serializers and migration code are application DI services.
They are not downloaded configuration or credentials returned by the server. Ship all still-supported
contract versions before upgrading the server. No server response loads a type name, assembly,
executable migration, arbitrary path or serializer implementation into the client.

## Server registration

Create the actual store through the chosen server provider and keep its private options on the
server. Then build a separate client projection:

```csharp
var clientManifest = ManagedSetupProtocol.ClientManifest(activeContractManifest);
var mobile = new ManagedClientSetup(
    "mobile-profiles", "file", RepositoryReadPreference.RemoteFirst,
    clientManifest, "profiles-data", "policy-1");
var desktop = mobile with
{
    Name = "desktop-profiles",
    LocalProviderId = "atlas",
    ReadPreference = RepositoryReadPreference.LocalFirst
};

var registry = new ManagedSetupRegistry(
    [new(mobile, store, CanUseMobileProfiles), new(desktop, store, CanUseDesktopProfiles)],
    TcpManagedSetupAuthority.Audience(publicClientEndpoint),
    new RsaManagedSetupSigner(serverSetupSigningKey),
    offlineLease: TimeSpan.FromHours(24));

await using var host = await TcpStoreHost.StartAsync(registry.Configure(hostOptions with
{
    Stores = [],
    AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
    TokenValidator = jwtValidator
}));
```

The application supplies `CanUseMobileProfiles`/`CanUseDesktopProfiles`; they inspect the validated
identity and enforce its scopes/roles/account permissions. If `hostOptions.AuthorizeIdentity` is
also supplied, both checks must pass. The registry requires an empty incoming store list so an
alternate manually registered alias cannot accidentally bypass this policy.

Use the public-facing endpoint's host, port and TLS target hostname for `publicClientEndpoint`;
the audience is computed identically on the client. It can differ from the internal listen address
behind a TCP load balancer. Use the same canonical hostname spelling on both sides.

All profiles for one dataset use one data alias, one client compatibility manifest and the same
store instance. The registry rejects multiple aliases for that store because exclusive sessions
must share one lock. Each profile has a separate read-only setup endpoint. No catalogue or raw
host-options endpoint is registered. Borrowed stores and the signing key remain application-owned;
dispose the host before those resources.

## Server-owned data policies

Register `IDataAccessPolicy` on `ManagedSetupRegistration`, not on the serialized
`ManagedClientSetup`. Policies remain server-side; the signed receipt includes only the
resolved opaque data scope and the approved request selector. The setup endpoint remains
private to the validated issuer/subject.

```csharp
var messages = new ManagedClientSetup(
    "messages", "file", RepositoryReadPreference.RemoteFirst,
    ManagedSetupProtocol.ClientManifest(messageManifest), "messages-data", "policy-2");
var groupPolicy = new ClaimDataAccessPolicy("groups"); // Normally supplied through DI.
var registry = new ManagedSetupRegistry(
    [new(messages, messageStore, CanUseMessages) { AccessPolicy = groupPolicy }],
    TcpManagedSetupAuthority.Audience(publicClientEndpoint),
    new RsaManagedSetupSigner(serverSetupSigningKey), TimeSpan.FromHours(24));
```

The caller can supply `RequestedScope` on the connection to `TcpManagedSetupAuthority`.
The server checks that selection against the validated token's group claim. Alternatively,
set `ManagedClientSetup.DataScope` to pin a server-approved group for that setup. Do not use
client-supplied group IDs as proof of membership. Claim policies use exact membership, not
hierarchical prefix matching. The application's alias authorization remains required.

The default registration policy is `SubjectDataAccessPolicy`. Use
`UnrestrictedDataAccessPolicy.Instance` to intentionally share an entire authorized alias,
or inject a custom policy for application permissions. Multiple profiles sharing a data
alias must share the same policy instance and physical store. Policies and provider secrets
are never included in the client projection.

`TcpManagedSetupAuthority` verifies the signed receipt and supplies `ExpectedScopeIdentity`
to the typed data connection. This keeps client caches and pending writes bound to the actor
and resolved data scope. A changed scope requires an explicit cache migration or a new setup.
File, SegmentCore and Atlas use the same contract. Restarting offline can use an existing,
unexpired approval. On reconnect, the client reauthorizes the scope and checks the signed
setup once after subscribing. A host restart between discovery and subscription can change
the read preference even when the contract version stays the same. The legacy
`SegmentCoreManagedRepository` composition entry point persists scope identity through an
`IRepositoryCursorStore` as identity metadata, not as an authorization grant.

When a verified approval expires during an outage, local reads and writes are denied with
`RepositoryAccessRenewalRequiredException`. The client keeps retrying authenticated discovery,
including after an offline restart, and resumes only after receiving a fresh signed approval.
The connection can report `Faulted` while approval is required; this specific failure is renewable.
Pending writes stay in the existing cache. Forged receipts, changed accounts/scopes and explicit
authorization denials remain separate failures and never authorize an offline fallback.

This is a breaking preview change: `ManagedClientSetup.AccessMode` and old capability flags
are removed, the setup receipt schema is version 2, and the public transport frame is version 3.
Upgrade all InterfaceDB packages together. Renew older setup receipts online and explicitly rebuild or migrate caches and pending writes when scope or contract compatibility changes.

Group members receive disposable change notifications within the same data scope. Client-owned
reconnect, registration and reconciliation remain unchanged. Per-message authorship and field
permissions still require application logic. Token lifetime/revocation policy remains in the
identity-provider integration; the database introduces no group polling or token introspection.

### Interface-declared ownership criteria

The public transport also supplies `ContractDataAccessPolicy<T>` and
`[StorageAccessClaim("tenant_id")]`. Register it through `AddContractDataAccessPolicy`
with the keyed storage serializer, then resolve the policy once and use that instance on
`ManagedSetupRegistration.AccessPolicy`. The registry passes the same policy to the data
registration; only the opaque approved scope enters the signed receipt. Do not serialize
the policy, key provider or serializer in `ManagedClientSetup`.

The policy checks ownership fields against validated claims before any record in a batch
is stored. Remote reads select the authorized partition without decoding every object.
Local File, SegmentCore and Atlas query indexes automatically index declared criteria;
the attribute alone does not authorize local reads or protect downloaded files offline.
Server enforcement applies again when queued writes reconnect. Rejected ownership changes
must be resolved by the application, not retried into another tenant's cache.

Pass an injected `IPartitionKeyIndexFactory` when opening the server store. The default
bounded lazy index caches committed key metadata, not authorization decisions. Each store
gets its own index; client-owned connections, subscription registration, reconnect and
reconciliation retain their existing ownership. Changing criteria changes the scope
identity and requires explicit data/cache migration and renewed setup approval.

See the public `documentation/data-access-policies.md` for registration, claim selection,
validation limits and index budgets. Multiple independent claim arrays express independent
memberships; use a composite claim or custom policy for authorized tenant/department pairs.

For policy and index selection, follow the public
[performance recommendations](data-access-policies.md#choosing-a-policy-for-performance).
Choose the required sharing/ownership boundary first. The raw transport timings do not measure
local-cache reads, queued-write replay, reconnect reconciliation or peer replication.
Measure those application paths separately, including cold reconnect and the intended concurrency;
retain signed scope binding, write validation and existing synchronization semantics when tuning.

### Preview.15 revision authority

The host supplies revision reservations; raw providers need durable read/write and atomic batches,
not an implementation of the wire reservation command. The host adds that capability.
`ManagedSetupRegistration.RevisionImportPolicy` is an optional server-only
`IRepositoryRevisionImportPolicy`. Configure it only for trusted import/replication identities
that must preserve external revisions on the remote store. All setup profiles for the same
remote alias must share this policy instance. Null denies remote imports, while ordinary saves
and remote-to-local cache imports continue normally. Coordinate client/host upgrades; older
client-maintained clock writes are rejected by the updated host.
