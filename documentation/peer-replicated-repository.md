# Peer-replicated repository

Available under MIT in `InterfaceDB.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

`PeerReplicatedRepository<T>` implements the normal `IRepository<T>` contract. It stores application
writes locally first and uses a relay to reconcile the records held by connected devices. The relay
keeps durable sequence/allocation metadata, but no application record bodies. This is intended for
disposable histories with best-effort availability. A record cannot be retrieved while every holder
is offline; a record lost from every holder is unrecoverable.

## Contract and sequence

Declare exactly one readable/writable nullable `long` property with `[SequenceNumber]` on the stored
interface. Inherited interface properties are supported. The property can have any name. Add the
existing `[SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]` declaration for application
range queries. The attribute lives in `Idb.Commercial.Repositories.Peers`.

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IEntry : IStoredContract<IEntry>
{
    Guid RecordId { get; set; }
    DateTimeOffset CreatedAt { get; set; }

    [SequenceNumber]
    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
    long? Sequence { get; set; }

    string Text { get; set; }
}
```

The application supplies a stable, unique logical ID through the existing `IResilientEntityMapper<T>`.
That mapper also makes detached value copies without serialization. A new offline write has a null
sequence. The relay assigns a positive number after authenticating and authorizing the publication.
Every accepted edit or deletion advances the sequence; the record ID stays fixed. Sequence numbers
are scoped to a registered dataset. They are not dates, storage keys, or provider revision tokens.

Only current record states and deletion markers are replicated. This is not an immutable event log
of every intermediate edit. Pending edits can coalesce, but an operation already being published is
retained separately until its assignment is acknowledged. This prevents lost acknowledgments and
process restarts from losing the predecessor of a newer local edit.

## Write and startup behavior

1. A local save serializes a detached snapshot with the registered object serializer, persists its
   pending operation in the local journal, then commits the native local repository projection.
   Only successful local storage returns a successful save. An uncertain journal/projection failure
   requires reopening, which replays the journal before exposing data or connecting.
2. The client obtains a JWT and establishes an outbound TLS connection to the relay. The server sends
   inventory/read/apply requests back through that connection. Devices expose no inbound listener.
3. The relay assigns sequences idempotently and persists the assignment before acknowledgment.
   Applying that assignment preserves the local provider revision so it does not invalidate an
   outstanding application edit merely because its sequence was assigned.
4. At connection/reconnection, or after new local publications, the relay compares paged exact
   inventories against its allocation metadata. Equal start/end/count summaries alone are never
   treated as proof of equality. `PeerRangePlanner.Compact` can represent exact coverage as ranges.
5. The range planner divides required sequence reads among eligible holders. Each fetched record is
   verified against its allocated digest and forwarded to permitted clients whose selections include it.
   Reads are bounded, individually correlated transfers; failed holders are replaced with another
   eligible holder during the same round. Existing participants are repaired along with newcomers.
6. Incoming records are deserialized once and committed locally. Their original serialized bytes
   are retained for subsequent forwarding. The relay never invokes an application serializer.
7. Startup imports, sequence acknowledgments and live incoming records use the replication
   subscription's `BeginWriteScope()`. They do not wake outgoing replication. Other application
   subscribers can observe real changes. Independent user saves remain enabled during synchronization.

There is no data polling loop. Reconciliation is triggered by connection, disconnection and local
write events. Connection retries and TCP keepalive handle downtime. Missing records do not cause
an endless resend cycle. Android background suspension is treated as peer unavailability.

## Abstractions and DI

The coordinator depends on `IEnvelopeMetadataRepository<T>`, `IRepositoryNotifications<T>`,
`IResilientEntityMapper<T>`, `ISequenceAccessor<T>`, `IPeerPayloadCodec<T>`, `IPeerJournal` and
`IPeerConnectionFactory`. Date selection uses `IPeerHistoryDateAccessor<T>`. The relay depends on
`IPeerSequenceLedger`, `IPeerRegistrationLedger`, `IPeerRangePlanner` and `IPeerHistorySelector`.
File, SegmentCore and Atlas use the same composition; the coordinator contains no engine-specific
casts. Capability checks and concrete construction occur at DI registration.

`AddPeerReplicatedRepository` resolves `IObjectSerializer<T>` and `IEnvelopeLifecycle<T>` using the
exact local database DI key. Register a custom serializer before the local provider. The same
serializer is used for local storage and record communication. Transport controls use bounded JSON
headers, with opaque payload bytes appended directly; control framing does not re-encode entities.
Contextual serializers receive dataset/version/record/operation associated data. Devices sharing
encrypted payloads must share the applicable data-key policy.

Use a separate local store and journal root for each endpoint, signed-in account, dataset and
contract/serializer version. Keep the underlying local repository private and perform all writes
through the peer facade. An existing nonempty store without a matching journal is rejected;
implicit adoption would omit records from synchronization. Applications select their installed
local provider at the composition root. The relay advertises its approved manifest and limits, and
never reveals another dataset or its private backend configuration. The existing managed-setup
receipt/version-migration flow is a separate `LocalRemoteRepository<T>` facility; it is not silently
enabled for this new protocol. Peer schema changes currently require an explicit coordinated upgrade
and state migration; incompatible manifests fail before joining.

The compiled [client composition](../samples/InterfaceDB.LocalRemote.Client/PeerExampleComposition.cs)
includes a contract, mapper and File registration. Call `PeerExampleComposition.CreateClient` with
TLS/JWT `TcpStoreClientOptions`, the signed-in issuer/subject, an account-specific directory and registration options.
Its endpoint manifest must equal `PeerExampleComposition.Manifest`.

```csharp
await using var provider = PeerExampleComposition.CreateClient(
    endpoint, signedInIssuer, signedInSubject, accountDatasetDirectory,
    new PeerRegistrationOptions
    {
        RegistrationId = persistedRegistrationId, // Unique per device/cache; reuse after restarts.
        Join = PeerJoinMode.LoadAllHistory,
        Resume = PeerResumeMode.ResumeAll
    });
var repository = PeerExampleComposition.Repository(provider);
await repository.InitializeAsync();

var saved = await repository.SaveAsync(new ExamplePeerRecord
{
    RecordId = Guid.NewGuid(), Text = "Hello"
});
// saved.Sequence can be null until the relay acknowledges its allocation.
```

Resolve `IRepository<T>` or `IRepositoryNotifications<T>` through the composition's DI key for normal
application use. Resolve `PeerReplicatedRepository<T>` for `Connection`, `Synchronization`, explicit
`SynchronizeAsync` and conflict resolution. Initialization opens local state and starts the client-owned
connection worker. It does not wait indefinitely for unavailable peers. `Synchronization.IsComplete`
requires a completed reconciliation with no missing or pending records; check `Connection` as well
because completeness describes the last agreed snapshot, not unreachable devices' undisclosed data.

## Relay hosting

```csharp
await using var ledger = new FilePeerSequenceLedger(ledgerDirectory);
await using var registrations = new FilePeerRegistrationLedger(registrationDirectory);
await using var host = await TcpPeerRelayHost.StartAsync(new()
{
    ListenEndPoint = new(IPAddress.Any, 7443),
    ServerCertificate = tlsCertificate,
    TokenValidator = jwtValidator,
    AuthorizeIdentity = (identity, dataset, access) =>
        applicationPolicy.Allows(identity.Principal, dataset, access),
    HistoryPolicy = (identity, dataset) => new PeerHistoryPolicy
    {
        AllowHistory = true,
        AllowResume = true
        // Set MinimumCreatedSequence from trusted application membership rules when needed.
    },
    Datasets = [new("entries", PeerExampleComposition.Manifest, ledger)
    {
        Registrations = registrations,
        HistoryDateProperty = nameof(IExamplePeerRecord.CreatedAt)
    }]
});
```

Use the existing `JwtAccessTokenValidator` and `IAccessTokenProvider` integrations, including Keycloak.
TLS hostname/chain validation remains enabled by default. Do not use permissive certificate callbacks
in production. A dataset requires an explicitly configured read/write authorization policy. Supply a
unique ledger instance and directory for every dataset. Multiple contracts/datasets and all their
device connections share this host's single public port.

This is a dedicated peer-relay protocol. The conventional `TcpStoreHost` serves a different protocol;
the two listeners cannot both bind the same endpoint. The peer host itself carries all peer operations
on one port. A single relay process owns each file ledger. Horizontal hosting requires a shared durable
ledger implementation and shared session coordination; a load balancer alone does not provide that.

Session lifetime and JWT expiry are bounded. Authorization is checked for each incoming operation and
before peer transfers, so policy revocation also blocks an existing session. The client validates the
server-confirmed issuer/subject binding before using the connection. Peers cannot submit arbitrary
read/apply requests to other devices; the relay selects holders and destinations within the authorized
dataset. Payload hashes bind transfers to allocated mutations. These checks do not attest that a
permitted writer supplied truthful application data or make a user-controlled offline device trusted.

## Client joining and resume choices

Two ordinary enums describe independent choices. They are not flags: a registration cannot select
both joining modes or both resume modes. The client sends these preferences in the authenticated
hello; the server enforces the dataset's policy before accepting them and before every transfer.

| Setting | Behaviour |
| --- | --- |
| `PeerJoinMode.FromNow` | No initial history; establishes a durable server sequence boundary. |
| `PeerJoinMode.LoadAllHistory` | Collect all permitted current records from available holders. |
| `PeerJoinMode.LoadHistoryRange` | Collect records whose configured interface date is within `HistoryRange`. |
| `PeerResumeMode.None` | No missed-record catch-up after disconnection. |
| `PeerResumeMode.ResumeAll` | Repair all gaps within the original joining scope and subsequent activity. |
| `PeerResumeMode.ResumeRange` | Repair gaps within fixed `ResumeRange` dates or a rolling `ResumeWindow`. |

```csharp
var dates = new PeerHistoryDateAccessor<IEntry>(entry => entry.CreatedAt);
var preferences = new PeerRegistrationOptions
{
    RegistrationId = persistedRegistrationId,
    DateProperty = dates.PropertyName,
    Join = PeerJoinMode.LoadHistoryRange,
    HistoryRange = new(startDate, endDate),
    Resume = PeerResumeMode.ResumeRange,
    ResumeWindow = TimeSpan.FromDays(7)
};
var connection = new TcpPeerConnectionFactory(endpoint, issuer, subject, limits, preferences);
services.AddPeerReplicatedRepository("entries", "local", journalDirectory, endpoint.StoreAlias,
    contractVersion, mapper, connection, limits, historyDate: dates);
```

Declare a readable `DateTimeOffset CreatedAt` property on `IEntry`, and preserve it in the entity
mapper. The selector may choose another property name; it must select the property directly.
Every client of a date-enabled dataset uses the same declared property, pinned by the server's
`PeerDatasetRegistration.HistoryDateProperty` and the contract manifest. The date is normalized to
UTC, carried in allocation metadata and checked against the decoded record. It is immutable after
the first local save, including edits and deletion. Offline writes keep their original application
date, not their later relay arrival date. The relay never deserializes a body to extract dates.

Fixed ranges include the start and exclude the end. `LoadHistoryRange` requires `HistoryRange`;
other joining modes reject it. `ResumeRange` requires exactly one of `ResumeRange` and `ResumeWindow`;
other resume modes reject both. Separate inputs allow a fixed initial backfill and a moving resume
window. Windows are evaluated using the server clock when an event triggers reconciliation, without
periodic data polling. New live changes still arrive outside the backfill/resume date range. Moving
the window never deletes older local data.

`FromNow + ResumeAll` recovers records created after the original joining boundary, including after
reconnects and process restarts. Editing a pre-join record does not make it a newly created record:
the relay preserves its first allocation sequence independently of its latest mutation sequence.
For `LoadHistoryRange`, the historical scope remains that initial interval plus subsequent records;
another client's full-history request cannot widen it. The initial history phase persists until
its selected snapshot has no gaps. `Synchronization.IsComplete` reports the last selected snapshot,
not all stored records globally, and does not guarantee that currently offline peers have no data.

Two consistency exceptions apply inside the authorized joining scope: tombstones invalidate retained
local copies even outside a resume window, and an explicit synchronization after a rejected pending
edit can retrieve that record's current head for conflict resolution, including a new local insert
whose ID collides with a record inside that joining scope. Neither expands access to a
pre-join or server-restricted record. Catch-up imports use the existing scoped notification guard;
they remain visible to normal application subscribers without generating another outgoing write.

Keep the registration ID unique per device/local cache and stable across reconnects. The server's
`IPeerRegistrationLedger` binds it to the authenticated account, preferences and initial sequence
boundary; the local journal scope also includes these preferences. Reusing an ID with changed modes,
ranges or another account fails. To deliberately change the selection, create a new registration and
dedicated local cache. Do not reuse an ID concurrently on two devices. Back up registration state with
the sequence ledger. The file registration ledger defaults to 10,000 registrations and fails at its
configured capacity; it does not silently forget old joining boundaries.

Server `HistoryPolicy` can deny history, deny resume, deny the registration entirely by returning
null, or enforce `MinimumCreatedSequence` from trusted membership rules. Client dates and ranges are
selection preferences, not proof of permission or authenticity. A permitted writer can choose a
false application date; business validation and stronger tamper resistance remain application policy.
Revocation prevents future transfers; it cannot erase bytes already held by a device.

The optional registration-less API preserves the earlier full-replica behaviour only on datasets
without a history date property and whose policy allows history and resume. Explicit registrations
require a server registration ledger. Connection factory decorators must forward `HistoryDateProperty`
as well as `Scope`. Explicit registration against older allocation metadata without original creation
sequences, or adding dates to an existing populated undated ledger, is rejected:
perform an explicit coordinated migration with a changed manifest. The updated sample advertises
`idb-peer-v2`; old binaries cannot read the new control fields and must not share that manifest.

## Conflicts, deletion and subscriptions

A mutation names the prior committed sequence it edited. If another writer has advanced that record,
the relay rejects the stale mutation. The local edit stays durable, other records continue syncing,
and `Connection.LastFailure` reports `PeerError.Conflict`. Incoming committed state is retained alongside
the pending edit. After it arrives, explicitly call `ResolveConflictAsync(recordId, UseRemote)` to
discard the local edit or `KeepLocal` to submit it against the current committed sequence.

Deletes retain a sequenced tombstone and an empty payload. They do not redistribute the deleted
message body. Tombstones and allocation metadata are not automatically pruned: removing them without
a coordinated retention boundary can allow stale devices to resurrect data. There is no automatic
expiry/retention policy in this first implementation.

Subscriptions remain disposable. A notification callback on the peer facade may read, but attempting
to save/delete/resolve on that same facade throws `RepositoryFeedbackLoopException`. Issue mutations
from application commands. The scoped replication guard preserves unrelated concurrent user writes;
there is no process-wide notifications-off flag.

## Storage, limits and qualification

The local journal retains the committed wire snapshot, a publishing snapshot when an acknowledgment is
outstanding, and a newer pending snapshot when necessary. Native storage provides the query/index
projection. This intentionally uses additional device storage to permit durable retries and forwarding
without another serialization round trip. Local records are bounded by both count and aggregate
payload bytes; the file implementation retains those bounded snapshots in memory while open.

Defaults: 4 MiB per payload, 10,000 current records per replica, 64 MiB aggregate replica payload bytes,
128 inventory entries per page, 32 relay connections and a 30-second request timeout. A reconciliation
round has its own five-minute `ReconciliationTimeout`, including time waiting for another round;
each inventory/read/apply/status request still has the shorter `RequestTimeout`. An unresponsive
peer can therefore time out without consuming the entire round's deadline. Caller cancellation,
session expiry and shutdown still cancel work. The round timeout must be at least the request
timeout and no more than 30 minutes. Both endpoints must be updated to use the separate round
budget; older clients retain their existing shorter wait. The file ledger
defaults to 100,000 retained allocations and atomically rewrites its metadata file on allocation. This
is an initial small-history implementation, not a measured high-volume server. Capacities and request
timeouts must match the deployment; exhaustion fails explicitly without deleting pending data.

Back up the relay ledger: it includes deduplication and current-record assignments, not just a counter.
Without that metadata a new counter cannot safely continue an old dataset. Device snapshots are not
a substitute for a central backup when durable history is a requirement. The relay still incurs transfer
bandwidth even though it stores no record bodies.

Verification covers real TLS/JWT connections, File/SegmentCore/Atlas, the default and alternate object
serializers, overlapping inventories, source load sharing/failover, offline writes, restart recovery,
lost acknowledgments, interrupted local projection, concurrent pending edits, tombstones, conflict
resolution, notification loops, dataset/account isolation, access revocation and malformed frames.
See [release validation](preview17-validation.md) for this build. Android hardware/background behavior and cloud soak/load qualification remain deployment work.
