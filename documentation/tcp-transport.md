# TLS TCP transport and hosting

The optional MIT transport provides an opaque encoded-store client, a TLS TCP listener and a durable
SegmentCore adapter. The runnable [host sample](../samples/InterfaceDB.TcpStore.Host/Program.cs)
hosts the listener and database engine in one process. The database files live in a configured
persistent directory on that host.

```text
Trusted application / service
  -> envelope serialization
  -> TcpEncodedEnvelopeStore
  -> TLS over TCP + JWT access token
  -> TcpStoreHost: token validation + alias/capability authorization + compatibility
  -> SegmentCoreEncodedEnvelopeStore
  -> SegmentCore files on a persistent volume
```

The public connector accesses one remote database without a persistent client database.
`IEncodedEnvelopeStore` defines storage operations, TCP control messages use MessagePack, and
envelope payloads use the configured object serializer. See [typed repositories](tcp-repository.md).
The transport and authentication packages are introduced in preview.11; see the [release status](preview11-release-notes.md).

## Design decisions

| Decision | Reason and deferred work |
| --- | --- |
| Shared opaque storage operations | Keeps domain serialization at the trusted application and storage independent of the wire protocol. The typed repository now uses this seam; domain RPC remains separate application work. |
| Bounded storage leases | Non-isolated stores retain one session per alias. Subject-isolated JWT stores lease independently per validated issuer/subject, while serializing physical operations. The typed wrapper opens a short session per operation; subscriptions hold no storage lease. |
| Atomic mutation batches | Gives one-store atomicity without leaving a transaction open across network round trips or client disconnects. |
| Explicit unknown mutation outcome | A missing acknowledgement can follow a successful commit. Callers reconcile durable state; blind transport retries could repeat effects. |
| Bounded current-state pages | Supports restart reads with explicit memory/response ceilings. A separate current-state delta API uses bounded durable sequence cursors; expired cursors require a fresh snapshot. |
| TLS TCP with required JWT access tokens | The selected deployment path validates configured issuers and authorizes the store alias and capabilities. Explicit mutual TLS modes remain available for compatibility or an additional certificate requirement. |

## Scope and ownership

Implemented operations are get, put, delete, get-many, atomic mutation batch, bounded enumeration,
checkpoint and ping. The host opens only configured stores, and a connection selects an alias;
client messages cannot name host filesystem paths. SegmentCore uses its durable write-through
configuration. Mutation success is returned after the underlying operation completes.

The host grants **one active storage session per alias** for non-isolated stores, and one per validated
issuer/subject for subject-isolated JWT stores. A competing session for the same lease receives `Busy`.
Every storage lease stops admitting operations at its maximum lifetime (five minutes by default),
which ping cannot extend.
Client requests are serialized within a session. One process must own a physical
store directory; different aliases must not bypass that ownership. Multiple independent aliases
can be hosted by the library, while the sample deliberately configures one store.

`EncodedEnvelopeSerializer<TContract>` reuses InterfaceDB's existing object serializer and checks
that the physical key matches the envelope key. Its caller supplies the prepared envelope and
stable authenticated encryption associated data. An unversioned host does not need the domain contract DLL merely to save or retrieve these bytes. A versioned host deploys trusted validation/migration code and its dependencies; clients never supply executable migration code.

The [typed repository](tcp-repository.md) connects this seam to `IRepository<TContract>`.
Each operation takes a short exclusive session, refreshes a bounded snapshot and preserves strict
revisions. `IsolateBySubject` partitions keys by validated issuer/subject. Disposable change hints
and current-state deltas are available. The connector has no persistent client database or offline
write queue. It does not implement distributed conflict resolution or general request deduplication.

The source update after preview.11 adds `TcpStoreRegistration.AccessMode` per alias:
`SubjectIsolated` partitions by JWT subject; `Shared` uses an explicitly authorized shared dataset.
Null retains the host's existing `IsolateBySubject` behavior. Both explicit modes require JWT.
The host never advertises subject isolation for a shared store. Typed shared clients explicitly
request `EncodedStoreCapabilities.SharedDataset`; isolated remains their default. See the
[shared-dataset setup and authorization contract](tcp-repository.md#shared-datasets-source-update-after-preview11).
Register independent stores for independent memberships. Configuration is fixed at host startup;
stop/restart the host to change registrations. The stock one-store sample has no dynamic group registry.

## Authentication

The selected deployment uses `AuthenticationMode = JwtBearer`: **every storage session requires a
valid access token**. A configured client obtains its token before opening the TCP connection;
a supplied token is also supported. Missing/invalid tokens never receive storage access. The host
has no anonymous fallback and the client cannot negotiate a weaker authentication mode.

TLS is required in every mode, including JWT. `JwtBearer` does not require a client certificate;
`MutualTlsAndJwtBearer` requires both. The original `MutualTls` API default remains for existing
callers, so new deployments must explicitly configure `JwtBearer` on both sides. Do not expose an
additional certificate-only endpoint if the deployment requires tokens for all access.

The [JWT authentication guide](jwt-authentication.md) describes trusted issuers, Keycloak settings,
client token providers, scopes, expiration and bounded signing-key refresh. Browser login and
domain-specific authorization are outside the raw storage transport.

## Run the host sample

The sample requires the .NET 10 SDK. Build from this public repository; no other checkout is needed:

```powershell
dotnet publish samples/InterfaceDB.TcpStore.Host/InterfaceDB.TcpStore.Host.csproj -c Release -o artifacts/tcp-host-publish
```

Copy `samples/InterfaceDB.TcpStore.Host/hostsettings.jwt.example.json` to a deployment-owned
`hostsettings.json`, outside source control. Supply:

1. The persistent data directory and configured store alias.
2. A server PFX containing its private key and server-authentication EKU. Its DNS names must match
   the client `Host` or `TargetHost`.
3. The environment variable holding the PFX password. The JSON file contains only that variable's
   name, never the password.
4. `authenticationMode: "JwtBearer"`, exact trusted HTTPS issuer/discovery/JWKS addresses, the
   storage audience, accepted algorithms/types and required scopes. The example uses Keycloak's
   default JWT header plus the `typ: Bearer` payload discriminator. Client certificate pins may
   be empty in this mode. See the [configuration guide](jwt-authentication.md#host-configuration).
5. The deployment's compatibility manifest, also provided to clients. Replace every sample
   identity/hash with the values established by the application build and storage configuration.

All relative paths resolve against the JSON configuration file's directory. Replace example
identity-provider addresses and manifest values with the deployment's actual settings.
The default listener is `127.0.0.1:7443`. Select a private interface address, or `0.0.0.0` when a
container's network must reach the process.

Have the process manager or secret store supply `IDB_SERVER_PFX_PASSWORD` (or the configured name),
then run:

```powershell
dotnet artifacts/tcp-host-publish/InterfaceDB.TcpStore.Host.dll /deployment/hostsettings.json
```

For an interactive development console, append `--console` and enter `stop` to perform graceful
shutdown. Standard service mode does not read stdin. The sample uses temporary Windows key
containers (`DefaultKeySet`) for Schannel compatibility, and ephemeral keys on other platforms;
the service identity needs permission to create its temporary key container. No certificate is
installed into a trust store.

### Explicit mutual TLS compatibility mode

`hostsettings.example.json` retains the original certificate-only configuration. That mode requires
at least one client certificate fingerprint: SHA-256 of the complete DER certificate, exactly
64 hexadecimal characters without separators. The placeholder pin is deliberately invalid. This
mode is available to existing integrations; it is not the selected token-required deployment path.

In either certificate-requiring mode the sample requires exact client certificate pins, current certificate validity and an explicit
client-authentication EKU. Its trust callback intentionally uses those configured pins instead of
the platform CA chain decision. Possession of the corresponding private key is proven by mutual TLS.
Every pinned client is authorized for the sample's one alias; pins do not express application users
or tenant permissions. Pin removal and certificate rotation require a host restart, which also ends
existing sessions. This pin policy does not consult CA revocation lists. Keep client private keys
in trusted service credentials and manage pin revocation operationally.

For a CA-managed host, use `TcpStoreHost` directly with the platform certificate validation default
or an explicit private-CA policy, and supply an `AuthorizeClient` function mapping the verified
certificate identity to allowed aliases. Certificate-requiring modes require both a client certificate
and that authorization decision. An unconditional certificate validation callback is unsafe.

The sample stops the listener and awaits active storage work before disposing the store on Ctrl+C,
SIGTERM or normal process exit. Shutdown closes connections, so a client with an in-flight write may
need outcome reconciliation. Configure the service manager's shutdown grace period for the longest
expected commit/recovery work. SIGKILL and machine loss bypass graceful cleanup and rely on store
recovery; the sample adds no new physical power-loss qualification.

## Client usage

The client validates the server's CA chain and hostname by default. A private CA must be trusted
by the client platform, or an explicit equally strict validation policy must be supplied. Both
endpoints require TLS 1.2 or TLS 1.3.

```csharp
using Idb.Authentication;
using Idb.Transport;

// `manifest` is the exact CompatibilityManifest used by the configured host alias.
// `encodedEnvelope` was produced by the application's existing envelope serializer.
using var tokenProvider = new ClientCredentialsAccessTokenProvider(new()
{
    Authority = "https://identity.example.com/realms/interfacedb",
    ClientId = "storage-service",
    ClientSecretEnvironmentVariable = "IDB_OIDC_CLIENT_SECRET",
    Scope = "interfacedb.storage"
});

await using var remote = await TcpEncodedEnvelopeStore.ConnectAsync(new()
{
    Host = "storage.internal.example",
    Port = 7443,
    StoreAlias = "catalogue",
    Manifest = manifest,
    AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
    TokenProvider = tokenProvider
});

await remote.PutAsync(physicalKey, encodedEnvelope);
byte[]? stored = await remote.GetAsync(physicalKey);

await remote.ApplyBatchAsync([
    new EncodedMutation("key-a", encodedEnvelopeA),
    new EncodedMutation("key-b", encodedEnvelopeB),
    new EncodedMutation("deleted-key", null)
]);

string? after = null;
do
{
    var page = await remote.EnumerateAsync(pageSize: 64, afterKey: after);
    foreach (var record in page.Records!)
        ProcessEncodedRecord(record.Key, record.Payload);
    after = page.NextAfterKey;
} while (after is not null);
```

A null mutation payload means delete. An empty byte array is a valid value. Batch mutations must
use distinct keys and commit atomically within the one registered SegmentCore store. There is no
transaction spanning aliases or external systems, and no remotely held begin/commit transaction.

## Failure and retry contract

If a mutation may have reached storage but its outcome cannot be proven, the client throws
`RemoteCommitOutcomeUnknownException`. This includes a lost acknowledgement, disconnection or
deadline/cancellation after dispatch. **A timeout does not prove rollback.** Open a fresh session
and reconcile the affected keys or the application's durable operation state before deciding
whether to retry. The transport never retries a mutation automatically and does not persist
request IDs for deduplication. A request's correlation ID only orders messages within its session.

A failed session must be replaced explicitly. Compatibility, authorization, capability, busy and
resource-limit errors use `RemoteErrorCode`. Remote error messages do not expose host exception
text or paths. `OnError` is host-local diagnostics; the sample logs exception type only for
connection errors.

JWT expiration closes the session and prevents new storage operations. Obtain a fresh token and
connect a new session explicitly; tokens are not refreshed inside an open session. An already
started mutation retains its commit/outcome rules even if the token expires while it is running.
Expired or disconnected sessions never trigger automatic mutation replay.

Cancellation uses bounded operation deadlines and connection closure. Storage may finish after
the transport deadline if it has crossed a durable commit boundary. The host keeps the alias leased
until that work completes, preventing a replacement session from overlapping the previous writer.

## Protocol and resource bounds

Every TLS stream starts with a MessagePack hello identifying the authentication mode, access token
when required, alias, compatibility manifest, required capabilities and client resource limits.
The host authenticates and authorizes the session and verifies the complete manifest before
admitting payload operations and chooses the lower client/server limits. Manifest equality checks
compatibility only; a caller's claimed schema/build hash does not prove what code it is executing.

The wire format is version 2. Version 1 frames are rejected; upgrade client and host together.
A frame has a 44-byte header followed by one MessagePack value:

| Bytes | Meaning |
| --- | --- |
| 0–3 | ASCII `IDBT` |
| 4 | Wire version, currently `2` |
| 5–7 | Reserved, must be zero |
| 8–11 | Unsigned big-endian MessagePack body length |
| 12–43 | SHA-256 of the body |

The checksum detects damaged frames; TLS provides authentication and confidentiality. The body
uses explicit fixed-length arrays for its message, manifest, limits, request and response shapes.
The root array has 12 fields: kind, correlation ID, alias, manifest, limits, capabilities, request,
response, error, timeout milliseconds, access token and authentication mode. Only the storage hello or subscription hello can
contain an access token; it is never echoed in a reply. Authentication mode is exchanged only in
hello/subscribe/hello-result. The access token ceiling is 16 KiB and also counts toward the frame limit.
There is no type-name resolver or domain-object deserialization at the host. The decoder rejects
unknown message/command values, unexpected fields, malformed/truncated frames, trailing data,
bad sequence IDs and oversized lengths before unbounded allocation. The operation and shape
definitions live in `EncodedStoreContracts.cs`, `TransportContracts.cs` and `MessagePackFrameCodec.cs`.

| Default limit | Value |
| --- | --- |
| Pre-authentication hello including header | 64 KiB |
| Authenticated complete frame including header | 8 MiB |
| One encoded payload | 4 MiB |
| UTF-8 key | 1,024 bytes |
| Batch keys/mutations | 64 |
| Requested page records | 128 |
| Host connections including handshakes | 16 |
| Active storage sessions | 1 per alias, or 1 per issuer/subject when subject isolation is enabled |
| Long-lived subscriptions | Half of MaximumConnections (8 by default); configurable with MaximumSubscriptions |
| Handshake/connect timeout | 15 seconds |
| Client operation / host maximum operation | 30 seconds |
| Storage session idle timeout | 2 minutes |
| Storage session admission lifetime | 5 minutes; configurable up to 10 minutes |
| Subscription lifetime | Until disposal, connection failure, JWT expiry or authorization failure; no storage polling |
| SegmentCore total record capacity | 100,000 by default; enforced before commit |
| SegmentCore subject capacity | 10,000 by default; cannot exceed total capacity |

Aggregate batches must also fit the frame budget; 64 individually legal payloads are not necessarily
one legal batch. Enumeration limits response bytes as well as record count. Get-many can fail when
the aggregate response exceeds its budget. A client negotiating lower limits may receive
`LimitExceeded` when existing records cannot fit its response frames.

New writes are checked against the configured payload/frame limits before storage. Network and
control-message allocations are bounded by the negotiated limits. For a pre-existing store,
SegmentCore's public read API allocates a stored value before the adapter can inspect its size;
an oversized legacy record can therefore allocate up to the engine's 256 MiB complete-record ceiling
before `LimitExceeded` is returned. Qualify existing data as trusted and within the configured limits.
The transport payload cap is not a pre-read disk-allocation ceiling.

Enumeration is ordinal keyset paging of current state. SegmentCore snapshots/sorts keys per page.
New keys are admitted only while the configured total and authenticated-subject capacities remain
available; overwrites and capacity-releasing deletes remain possible at the boundary. Subject
enumeration applies its ceiling after filtering to that authenticated partition. Pages do not form a durable
snapshot or change feed; writes between pages can affect the result. The separate `ReadDeltaAsync` API uses durable current-state cursors. Larger datasets still require additional paging/query and capacity qualification before raising these bounds. Idle storage connections should be closed
or send an explicit ping before the idle timeout; ping does not extend the session admission lifetime.
An operation is cancelled at that boundary. Its lease remains held until invoked storage settles, so
an uncertain mutation is never overlapped by a replacement writer; deployed store implementations
must honor cancellation and the built-in SegmentCore adapter does so.
Subscription connections use OS TCP keepalive
(30 seconds idle, 10-second intervals, 3 failed probes) and wait for push frames.

## Cloud deployment choices

The database service is a long-running process with a TCP listener, server certificate and durable
storage. The port can remain private between trusted services. A public domain gateway can provide
ordinary HTTPS to mobile/web applications and call this private database service after enforcing
the application's user, family, tenant and operation permissions.

| Deployment | Infrastructure consequence |
| --- | --- |
| One VM/service | Run the published host under a process manager, mount durable storage and permit the configured TCP port from trusted clients. |
| Container on a VM or orchestrator | Mount persistent data and certificate/config secrets, bind the container interface, and expose a TCP service. The writable container layer is not the database volume. |
| TCP load balancer | Pass encrypted TCP through to the owning host, preserving end-to-end TLS and any required client certificate. |
| HTTP-only hosting/ingress | This raw TCP listener cannot run behind a route that accepts only HTTP. Choose TCP-capable hosting for this connector. |

For AWS Network Load Balancer, use a **TCP** listener for end-to-end mutual TLS; a TLS listener
terminates TLS at the load balancer. [AWS listener documentation](https://docs.aws.amazon.com/elasticloadbalancing/latest/network/load-balancer-listeners.html).
Azure Container Apps has explicit TCP ingress support and external TCP networking/port constraints;
verify those against the chosen environment and persistent-storage design.
[Azure ingress documentation](https://learn.microsoft.com/azure/container-apps/ingress?tabs=bash).
Kubernetes can expose a TCP service through `LoadBalancer`, subject to the cluster's provider.
[Kubernetes load balancer documentation](https://kubernetes.io/docs/tasks/access-application-cluster/create-external-load-balancer/).

Use one active host instance per physical store. Adding replicas behind a load balancer does not
replicate SegmentCore files or make several writable owners safe. Scale distinct stores using
explicit partition/alias routing; shared-store failover needs ownership/fencing and a separately
qualified storage plan. Rolling upgrades must stop the old owner before starting a replacement on
the same data directory. Do not enable automatic scale-out for this single-store sample.

For containers, publish for the target OS/runtime and copy only the publish output into a suitable
.NET 10 runtime image. Supply configuration, PFX files and persistent data as runtime mounts, with
the PFX password injected by the secret manager. No Docker build needs access to private keys, and
the image does not need the source repositories. The [local Docker sample](../samples/InterfaceDB.TcpStore.Host/docker/README.md)
uses a Dockerfile over prepared Linux publish output, a loopback-only TCP port, one nonroot storage
container and a persistent named data volume. Read-only runtime mounts supply the server certificate
and configuration; client/CA private keys stay outside the container. The preparation and Compose
commands in that guide are the starting point for local deployment.

Use process supervision and a listener-level probe for basic liveness. A TCP connect probe proves
only that the port accepts connections; it does not prove a durable commit. A full authenticated
ping needs a session and can receive `Busy` while the owning client is connected. Storage readiness,
backup/restore, disk-space alerts, certificate expiry and commit latency need operator monitoring.
For a simple consistent offline backup, stop the host and copy the closed store, then verify restore
in a separate directory. Do not assume copying changing live database files is a consistent backup.

This implementation supplies the transport and host foundation. A cloud deployment still needs
its own volume durability/latency qualification, restart recovery checks and failure-handling tests.
