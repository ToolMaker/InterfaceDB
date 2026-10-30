# Data access policies (preview.13)

The server now selects data scope through `IDataAccessPolicy`. This replaces
`IsolateBySubject`, `TcpStoreAccessMode` and the `SubjectIsolation`/`SharedDataset`
capability flags. Client and host must be upgraded together: transport frame version 3
rejects older frames. See the [release status and verification](preview13-validation.md) before selecting package versions.

Authentication remains independent. The unreleased [native-account provider](native-account-authentication.md)
also supplies server-validated account identities to these policies. JWT modes still validate tokens and require the
host's `AuthorizeIdentity` callback for alias/operation admission. Certificate modes still
validate and authorize client certificates. No data policy never means no transport authentication.
Local File and SegmentCore repositories require no tokens or changes to their APIs.

## Authentication, permissions and tenant isolation

These are separate decisions, applied on the server in this order:

| Layer | Responsibility | Configuration |
|---|---|---|
| Transport authentication | Establish who connected; validate TLS and the configured credential. JWT modes validate signature, issuer, audience and lifetime. | `TokenValidator` or the unreleased `IAuthenticationProvider`, authentication mode and certificate trust. |
| Store/operation authorization | Decide whether that identity may use this alias and the requested operations. Roles or application permissions belong here. | `AuthorizeIdentity` checks alias and operations in JWT/Provider modes; `AuthorizeClient` admits a certificate to an alias in certificate modes. Registered store capabilities also apply. |
| Data authorization and selection | Check tenant/account/group membership and resolve one permitted data partition. | `IDataAccessPolicy` on the server registration; built-in identity policies require trusted identity claims. |
| Ownership on writes | Require declared record fields to match the authorized claim values before committing. | `ContractDataAccessPolicy<T>` and `[StorageAccessClaim]`. |
| Encryption at rest | Protect persisted payload contents when configured. | The storage serializer/encryptor and key provider; independent of the access policy. |

`SubjectDataAccessPolicy`, `ClaimDataAccessPolicy` and `ContractDataAccessPolicy<T>` require a
validated identity claims. Use `JwtBearer`, `MutualTlsAndJwtBearer`, or the unreleased `Provider`
mode supplying an authenticated issuer/subject and any required membership claims. In
certificate-only `MutualTls` mode, `DataAccessContext.Identity` is null and the context does not
contain the validated client certificate; these identity policies therefore deny access.
Certificate trust and `AuthorizeClient` still protect alias admission. Unrestricted access and
custom policies based on trusted server configuration rather than caller identity remain usable,
but a custom data policy cannot distinguish client certificates through the current context.
Do not infer certificate ownership from the client-supplied scope selector.

A valid token alone does not grant access to every tenant. A client-supplied `TenantId`, scope
selector, key or query predicate is not authorization. The server resolves the partition from
validated identity claims and applies it to keys, pages, batches, changes and subscriptions.
This is enforced selection, not an optional filter the client can remove. It is also not an
arbitrary per-row authorization predicate: once admitted, the caller can access records within
that partition subject to its allowed operations.

For example, Alice and Bob with `tenant_id = "tenant-a"` can share that tenant's data. Carol
with `tenant_id = "tenant-b"` uses a different partition, even when she requests the same logical
record key. A token with both memberships must select one unless the registration pins a value.
To keep each login private instead, choose `SubjectDataAccessPolicy`. Roles determine permitted
actions; membership claims determine the data boundary. A broad role must not implicitly bypass
the selected tenant policy.

The selector (`RequestedScope`) and opaque server approval (`ScopeIdentity`) are identity
metadata, not secrets or access tokens. Knowing them does not grant access. Partitioning also
does not encrypt files or prevent someone with direct database-file/process access from reading
them. See [security boundaries](security.md#authentication-authorization-and-data-boundaries).

## Registration and DI

Register policy implementations at the composition root and inject the abstraction:

```csharp
services.AddKeyedSingleton<IDataAccessPolicy>("messages", new ClaimDataAccessPolicy("groups"));
services.AddKeyedSingleton<IDataAccessPolicy>("profiles", new SubjectDataAccessPolicy());

var messages = new TcpStoreRegistration("messages", messageManifest, messageStore)
{
    AccessPolicy = provider.GetRequiredKeyedService<IDataAccessPolicy>("messages")
};
```

Each registration has its own policy, including multiple registrations of the same contract.
A null registration policy inherits `TcpStoreHostOptions.DefaultAccessPolicy`. If both are
null, callers share the unpartitioned dataset within the authorized alias. Set
`UnrestrictedDataAccessPolicy.Instance` to explicitly override a default policy.
Do not remove a policy from an existing protected dataset without a deliberate data migration.

| Implementation | Data boundary |
| --- | --- |
| `SubjectDataAccessPolicy` | Validated issuer plus subject; preserves the former subject key format. |
| `ClaimDataAccessPolicy("groups")` | One authorized claim value, namespaced by issuer and claim type. |
| `ClaimDataAccessPolicy("groups", "family-a")` | Registration pinned to that membership. |
| `UnrestrictedDataAccessPolicy` / no policy | The alias's unpartitioned dataset, subject to transport admission; no implicit access to protected partitions. |
| `ContractDataAccessPolicy<T>` | AND of interface-declared claim criteria; validates ownership fields before writes. |
| Custom `IDataAccessPolicy` or `DelegateDataAccessPolicy` | Application-defined scope and operation permissions. |

Configure each physical store consistently as an unpartitioned shared dataset or a partitioned
dataset, including when multiple aliases expose the same store. `Unrestricted` is the ordinary
unpartitioned scope, not an administrator grant across partitions. In a store that has never used
partitioning, this retains the existing shared-dataset behavior. Switching a protected store to
unrestricted does not merge or transfer its data.

For administration, authorize an explicit tenant selection through the same policy and connect
within that approved scope. An administrator can work with each permitted tenant this way;
cross-partition enumeration is not a built-in capability. Never fall back to unrestricted access
when required claims or membership checks fail.

Policies return a `DataAccessScope` or null to deny. Exceptions deny access. They are called
before operations and notification delivery, and must be thread-safe. A session cannot switch
to a different resolved scope. Configure `AllowedCapabilities` for read-only policies, or compose
role checks in a custom policy. Include `DurableCommits` and the operations required by the client.
An application role must not grant access to unrelated groups implicitly.
When adding a role check, prefer the host's admission callback so the registered contract policy
retains its write checks. A custom decorator around a write policy must also implement and forward
`IDataAccessWritePolicy.ValidateWrite`; wrapping only `Authorize` in `DelegateDataAccessPolicy`
would omit ownership-field validation.

## Choosing a policy for performance

Choose the required data boundary and write guarantees first, then use the simplest policy that
implements all of them. Removing ownership validation or widening a partition is a behavior change,
not a safe performance optimization. A tenant is only one possible boundary: user, family, account
and group partitions use the same key-selection mechanism. Renaming a claim to `tenant_id` does
not unlock a special fast path.

| Required behavior | Starting choice | Work performed and performance implication |
|---|---|---|
| Private data per login, no stored ownership-field rule | `SubjectDataAccessPolicy` | Resolves issuer/subject and a partition; does not decode record bodies for ownership. No extra tenant property is needed. |
| Shared family/account/group data, partition membership is sufficient | `ClaimDataAccessPolicy("family_id")`, `"account_id"`, `"groups"` or the appropriate trusted claim | Checks exact membership and selects one partition; no record-body ownership decoding. Multiple memberships require an authorized selection or a pinned registration. |
| Stored ownership fields must match validated claims | `ContractDataAccessPolicy<T>` | Adds envelope decoding and ownership checks on upserts before commit. Reads still select partition keys without domain decoding for this policy. Preserve this check when it is part of the business contract. |
| Everyone admitted to the alias may share its unpartitioned dataset | `UnrestrictedDataAccessPolicy.Instance` | Omits membership partitioning and ownership checks. It retains ordinary enumeration rather than the partition index, so it is not automatically the fastest read choice. |
| Permissions require additional trusted relationships or rules | Custom `IDataAccessPolicy` and, where needed, `IDataAccessWritePolicy` | Keep checks bounded and thread-safe. A prebuilt immutable lookup can represent application-owned configuration; it must not become a stale cache of user authorization decisions. Profile the actual implementation. |

The descriptions above follow the implementation; they are **not a measured speed ranking**.
We have not run a head-to-head subject-versus-group-versus-custom policy benchmark. The production
measurements compare unrestricted access with the contract policy, and compare indexing modes
under the same contract policy. Do not assume a simpler policy preserves a field-validation rule
just because both policies select the same tenant today.

### Recommendations for read-heavy workloads

Start with bounded `Lazy` partition indexing for repeated reads of authorized partitions. It
benefits subject and claim partitions as well as interface-defined ownership partitions. At
100,000 records across 100 tenants, the measured contract-policy full-tenant read (1,000 records)
fell from **81.505 ms to 6.653 ms** with indexing, with identical permissions and returned data.
This measures index selection, not an advantage of tenant IDs over user IDs or groups.

Size the index for the active working set: retained keys across active partitions, number of
active partitions, and estimated bytes. All three budgets apply; increasing only one may still
leave frequent eviction. Defaults retain up to 10,000 keys, 16 partitions and 8 MiB of estimated
key metadata per store. Inspect hits, misses, evictions and fallbacks. Repeated fallback can mean
one partition exceeds the key/byte budget; repeated misses can indicate too many active partitions
or restart/recovery. The temporary scan allocations are not bounded by the retained-byte estimate.

First access still scans: in that same run it was **10.677 ms with indexing disabled versus
11.625 ms with lazy indexing**. Consider `Eager` only when startup cost and memory are acceptable
and the intended partition set fits the budgets; measure startup and first-request latency before
selecting it. For infrequent partition reads or severe memory constraints, compare `Disabled`
against `Lazy` with the same policy. Neither alternative changes authorization or durability.

Choose partition size according to who must share data. A larger shared partition means more
records to retrieve and more clients competing for its session lease. Artificially splitting it
changes sharing and reconciliation requirements. Independent scope leases do not establish
parallel engine throughput: the current adapter still serializes physical operations, and the
published production benchmark used one active benchmark client at a time.

### Recommendations for write-heavy workloads

When partition membership is the entire requirement, subject/claim policies avoid the typed
policy's extra body-validation work. When ownership fields are a required invariant, keep the
contract policy and budget for that cost. In the equal-size 10,000-row single-dataset comparison,
an individual durable update was **3.966 ms unrestricted versus 5.382 ms with the contract
policy**; 128-update batches were **4.729 ms versus 6.310 ms**. Both used TLS/JWT. These are total
operation costs including partition keys/bookkeeping, not isolated timings of the authorization
method. Encrypted envelopes and custom additional validators need their own workload measurements.

Use normal bounded batch APIs when the application already wants those edits committed together.
They can amortize request/commit costs; every upsert is still checked before the batch is written.
Respect negotiated item/payload limits and the typed repository's reserved clock item. Do not
silently split a transaction, drop validation, change conflict handling, or relax durability to
obtain a better number. The key index did not establish a write-speed improvement: large-store
capacity scans remain on the write path.

Keep only criteria the required access rule actually needs; extra criteria add claim processing
and write checks, and change the namespace. A custom policy runs on admission, operations and
notification delivery. Avoid unbounded work or synchronous network calls in that path. Any cached
application configuration needs an explicit update strategy; token expiry, membership validation
and policy checks must continue to apply. Preserve `IDataAccessWritePolicy` when decorating a
contract policy, as described above.

### Validate a choice without changing requirements

Use equal data, result counts, permissions, serializer, encryption and durability when comparing
index or implementation choices. Keep the unrestricted database/SQL baseline separate from the
policy-enabled workload. Record warm and first-after-reopen reads, writes/batches, allocations,
index counters and the application's concurrency; verify cross-scope rejection and reopen data
alongside timing. Claim cardinality, encrypted writes, custom policies, WAN latency and concurrent
traffic were not covered by the current policy comparison.

The [production runner and method](../StorageBenchmarks.AccessPolicy/README.md) measure encoded
operations on a loopback TCP/TLS connection. `TcpRepository<T>` also opens short sessions and
refreshes bounded snapshots, so its application latency must be measured separately; the roughly
12x enumeration gain is not a promise of 12x faster application calls. See the
[raw results and limitations](access-policy-production-20261028.md) before selecting budgets or
making a performance claim. No new timing run was performed for these recommendations.

## Selecting a group

```csharp
var options = repositoryOptions with
{
    Connection = repositoryOptions.Connection with { RequestedScope = "family-a" }
};
await using var repository = new TcpRepository<IMessage>(options);
```

The server checks exact membership; the selector is not proof of access. If no scope is selected,
the claim policy requires exactly one membership, unless the registration pins a scope. Group
hierarchy and prefix matching never grant membership implicitly. Configure Keycloak to include
the appropriate claim in the **access token**. String claims and JSON arrays of strings are supported.
Use stable group/account IDs when rename-independent ownership is needed; changing a group path
used as identity changes the data scope and requires deliberate migration.

The server returns an opaque `ScopeIdentity` during discovery and connection. A client can pin
`ExpectedScopeIdentity` to a previous server approval; a mismatch is denied. The typed repository
pins its first approval automatically. It includes actor and resolved scope in its cache identity,
and verifies the scope on every subsequent connection and subscription. Recreate the repository
and select an appropriate cache when changing groups. The policy implementation is never sent
to the client.

Keys, enumeration, batches, change cursors and notifications share the resolved partition.
Operation leases serialize clients within a partition; different group partitions can acquire
separate leases. Unrestricted datasets use one alias-wide lease. Authorization is per scope and
operation. Simple policies do not inspect fields. The optional contract policy below also validates
ownership on writes. Neither path reserializes the domain payload on the server.

## Declaring ownership on the stored interface

For tenant isolation without inspecting a record field, register
`new ClaimDataAccessPolicy("tenant_id")`. Use the typed contract policy below when the stored
interface also declares `TenantId` and the server must reject a mismatching value in a save.

```csharp
public interface IMessage : IStoredContract<IMessage>
{
    [StorageAccessClaim("tenant_id")]
    string TenantId { get; set; }
    string Text { get; set; }
}

// contract is the deployed IDbContractConfigDto<IMessage>.
// serializer is the same configured IObjectSerializer<IMessage> used for storage.
services.AddKeyedSingleton<IObjectSerializer<IMessage>>(contract.EnvelopeDICacheKey, serializer);
services.AddContractDataAccessPolicy(contract);
using var provider = services.BuildServiceProvider();
await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(dataPath,
    keyIndexFactory: provider.GetRequiredService<IPartitionKeyIndexFactory>());
var registration = new TcpStoreRegistration("messages", manifest, store)
{
    AccessPolicy = provider.GetRequiredKeyedService<IDataAccessPolicy>(contract.EnvelopeDICacheKey)
};
// Include registration in the authenticated host's Stores collection.
```

The snippet uses `Idb.Transport`, `Idb.Libraries.Abstractions.Attributes`,
`Idb.Libraries.Abstractions.Dtos`, `Idb.Libraries.Abstractions.Services.Serialization`
and `Microsoft.Extensions.DependencyInjection`. The contract configuration, manifest and
concrete implementation remain application-owned, as with other repository registrations.

The attribute adds an exact query index automatically in local repositories. **It does not
enable authorization by itself.** The host must register the contract policy. Local applications
using no policy continue to work without a token, including interfaces carrying this attribute.
The server's partition key index is separate from local query indexes: it selects committed keys
for the authorized boundary without deserializing every row.

Declare one to eight readable string properties. Inherited criteria work; shadowed/duplicate
property names, numeric criteria, `NotSearchIndexed` and `EncryptedStorageField` on a criterion
are rejected. Exact matching is ordinal and case-sensitive. Keycloak should issue a stable
string ID, even if the application's tenant identifier is otherwise numeric. A claim may contain
one string, repeated string claims or an array of strings. With several criteria the server ANDs
their membership checks. This represents independent memberships; it does **not** infer allowed
tenant/department pairs from separate claim arrays. Use a trusted composite claim or a custom
policy for relationships between those values.

With one criterion, `RequestedScope = "tenant-a"` selects an authorized membership. With several,
use a JSON object keyed by interface property name, for example
`RequestedScope = "{\"TenantId\":\"tenant-a\",\"DepartmentId\":\"sales\"}"`.
Omitted values require a single membership or a server-pinned value. Unknown/duplicate JSON
properties, ambiguous membership and unauthorized values are denied. Server registration can
pin values using `selectedValues`; client selection cannot override them. `serviceKey` and
`serializerKey` allow independent policies for several registrations of the same interface.
Each selected criterion value is limited to 256 UTF-8 bytes; the complete transport selector
is limited to 1,024 UTF-8 bytes. Claim names are literal: a name such as `tenant_id` is not a JSON
path into nested role or group objects. Supply a flat string/string-array claim or use a custom
policy to interpret a different trusted claim structure.

Before `Put` or `ApplyBatch` reaches storage, the policy decodes each non-null record once with
the configured serializer, verifies envelope identity/version and checks every ownership field
against the validated token. The entire batch is checked before any record or repository clock
is written. A mismatch denies the write. The original encoded bytes are committed unchanged;
there is no server reserialization or silent ownership stamping. Application edits stay local
until a normal authorized save succeeds. Deletes, reads and notifications use the same partition.
Reads perform no domain decoding on the server for this policy.

`IAccessPolicyEnvelopeReader<T>` is the DI seam for application envelope validation. The default
reader supports the configured whole-envelope encryption wrapper and its authenticated context;
the server must have the corresponding key provider. Unencrypted serializers avoid unused
encryption-context hashing. Other independently configured validators, such as a custom
`TcpStoreVersion.Validate` callback, still run and may decode again; they are not bypassed to
claim a faster result. The benchmark uses the normal encoded store with the contract reader,
without a second custom domain validator.

Ownership criteria, property names, contract identity and issuer form a namespace. Changing this
mapping or switching from a simple claim/subject policy requires explicit trusted migration and
validation of existing records. Existing unpartitioned data is never silently assigned to whoever
connects first. Changing an ownership field does not move a record between tenants. Rebuild
disposable client caches or explicitly migrate pending writes when the approved identity changes.

## Bounded partition selection

```csharp
services.AddPartitionKeyIndex(new PartitionIndexOptions
{
    Mode = PartitionIndexMode.Lazy,
    MaximumKeys = 10_000,
    MaximumPartitions = 16,
    MaximumEstimatedBytes = 8 * 1024 * 1024
});
```

Register customized options before `AddContractDataAccessPolicy`, which adds defaults only if
no factory exists. A singleton `IPartitionKeyIndexFactory` creates independent mutable indexes
for each opened store. Pass it to `SegmentCoreEncodedEnvelopeStore.OpenAsync` or
`VersionedTcpStore.OpenAsync`. Custom implementations depend on `IPartitionKeyIndex`, not engine
classes. Multiple keyed factories can supply different budgets to different stores.

`Lazy` is the default. First access scans committed keys; later pages use an ordered set for
that partition. Least-recently-used partitions are evicted within all three budgets. Oversized
partitions fall back to a temporary scan/sort and are not retained. The byte budget is an estimate
of retained index metadata, not a process-memory ceiling; cold scans still allocate snapshots.
`Disabled` preserves the previous scan path. `Eager` warms partitions at open within the same
retained budgets but requires a temporary whole-store grouping and more startup work.
Unrestricted access scans shared keys in all modes. The host assigns an optional
`EncodedStoreRequest.UnpartitionedOnly` enumeration hint; SegmentCore uses it to skip tenant
keys before sorting or reading payloads. The global physical-key ceiling is unchanged. The hint
is not on the wire and is not an authorization decision: the host still validates and filters
every response. Custom providers can ignore the hint and retain the correct, slower fallback.
Custom request-copying adapters should preserve it when forwarding server requests. Direct raw
enumeration defaults to the complete physical dataset for trusted migrations and validation.

The original unrestricted baseline rebuilds and sorts its key list for each page; it does not use the
partition index. Its slower multi-page result therefore compares two selection algorithms,
not the cost of authorization alone. Writes also scan committed keys to enforce global,
partition and change-journal capacity. Replacing those scans with counters or extending indexed
selection to unpartitioned data is separate optimization work, requiring paired measurements and
tests for atomic batches, reopen, net-neutral writes at capacity and bounded metadata memory.

Derived keys are updated only after the durable transaction commits, under the store's commit
gate. A commit or index-state failure discards derived state; a later read reconstructs it from
committed storage. Proven pre-commit rejections preserve the index because nothing was changed.
Eviction and reopen never discard data. Quotas, frame limits, ordering and continuation cursors
remain enforced. The adapter also checks an injected index's returned keys for scope and order.
`IPartitionIndexDiagnostics.PartitionIndexStatistics` exposes hits, misses, evictions, fallbacks
and retained estimates without requiring telemetry or exposing tenant identifiers.
The index contains keys, never cached permission grants: admission and every operation and
notification still evaluate policy. See the [production validation and measurements](access-policy-production-20261028.md).

## Offline composition and compatibility

The online TCP repository has no local database. For a composing repository that needs offline
restart, supply `ExpectedScopeIdentity` from trusted server configuration or inject
`TcpRepositoryOptions.ScopeApprovalStore` using `IRepositoryCursorStore`. The latter persists only
the last server-approved scope, under an actor/endpoint/contract/selector-specific key. First use
still requires server approval. Cached scope metadata is an identity guard, not permission to
bypass authentication; reconnect always reauthorizes it. Applications retain responsibility for
offline access to already downloaded files.

Old cache identities are intentionally incompatible. Rebuild disposable caches, or explicitly
migrate records and pending writes. Subject-partition server keys remain compatible with the old
subject mode; unpartitioned keys remain unchanged. Moving records between subject, group or
unrestricted policies requires an explicit mapping. There is no automatic ownership reassignment.

In the stock host sample, replace `isolateBySubject` with `dataAccessPolicy: "subject"`.
For groups use `dataAccessPolicy: "claim"`, `dataScopeClaim: "groups"`, and optionally `dataScope`.
Use `"none"` for unrestricted data. Unknown configuration properties are rejected, so an obsolete
scoping setting cannot be silently ignored.

Token lifetimes, refresh and membership-revocation policy remain with the identity provider and
authentication integration. This feature does not introduce token introspection or group polling.
Storage durability, transaction/revision semantics and committed-read copies are unchanged.

## Upgrade and troubleshooting

| Previous configuration | Current source |
|---|---|
| `IsolateBySubject = true` or subject access mode | Register `SubjectDataAccessPolicy`. |
| Shared/unrestricted access mode | Register `UnrestrictedDataAccessPolicy.Instance` explicitly. |
| Subject/shared capability flags | Remove them; select data through the server policy. Keep operation capabilities. |
| Client-owned group/tenant filter | Use a validated membership policy and, when fields must be enforced, the contract policy. |
| Old client cache or approval | Rebuild disposable caches or explicitly migrate data/pending writes; obtain new approval online. |

Upgrade the client and server together for wire version 3. Preserve a backup and the previous
runtime before moving existing data. Subject policy retains the old subject key namespace;
other policy/criterion changes can require explicit ownership mapping and revalidation. The
database does not infer that mapping. In commercial managed deployments, renew signed setup
receipts using the matching commercial source and its private integration guide.

| Symptom | Check |
|---|---|
| Valid JWT but `Unauthorized` | Alias/operation permissions, exact claim name, issuer, membership and requested/pinned selector. Authentication and authorization are separate. |
| `Unauthorized` on save/batch | Every ownership property must equal the selected claim value; no record in a rejected batch is committed. |
| `InvalidRequest` on a typed write | Envelope identity/version, registered serializer/runtime allowlist, encryption context and payload shape. |
| `InvalidRequest` for a reserved physical key | Use logical keys in the approved scope. Tenant and change-journal namespaces cannot be accessed through an unrestricted TCP session. |
| `LimitExceeded` before storage | Reduce the batch/frame size or respect the store/partition key capacity. A proven pre-commit rejection leaves the session usable and commits no partial batch. |
| Lookup is empty | Confirm the approved tenant. The same logical key in a different partition is not visible; do not retry with unrestricted access. |
| `Busy` | Another storage session holds the same alias/scope lease; dispose short-lived operations and apply the normal bounded retry policy. |
| `OutcomeUnknown` after a write | Reconcile committed state before retrying; a transport/storage failure is different from a preflight policy denial. |
| First tenant read is slower | Lazy indexing builds from committed keys on first use; inspect index diagnostics for misses, eviction or fallback. |

For a minimal integration, test two members of one tenant, a member of another tenant, a forged
ownership field and a reconnect under a changed selection. Existing regression coverage and
measured costs are recorded in [production validation](access-policy-production-20261028.md).

Provider authors may use `EncodedStoreRequestRejectedException` only when validation proves
that no mutation could commit. It supports `InvalidRequest` and `LimitExceeded`; the host does
not publish a change hint for this result. Ordinary exceptions, timeouts and failures after a
commit attempt still require reconciliation. A generic `RemoteStoreException` carrying either
code is not proof that nothing committed.

The scope view is applied at the TCP host boundary, independently of the physical provider.
Direct in-process access to a raw provider is trusted storage access, not an authenticated data
policy endpoint. Its physical enumeration remains available to server-side version migration
and validation so upgrades retain every tenant's records and revision clocks.
