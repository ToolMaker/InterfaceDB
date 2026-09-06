# Retained editing and custom conflict strategies

Retention is an explicit, free provider capability. Ordinary `Get`/query and `SaveAsync` still use strict expected-revision matching and do not acquire historical leases. Enabling retention does not select a different conflict strategy.

```csharp
var policy = StoragePolicy.Default with
{
    RetainedVersions = new RetainedVersionOptions
    {
        LeaseDuration = TimeSpan.FromMinutes(5),
        MaximumLifetime = TimeSpan.FromMinutes(30),
        MaximumSessions = 256,
        MaximumRecords = 256,
        MaximumBytes = 64L * 1024 * 1024,
        CleanupInterval = TimeSpan.FromSeconds(15)
    }
};
// Put policy on the contract configuration passed to AddFileRepository
// or AddSegmentCoreRepository before the repository is opened.
```

An existing entity supplies the physical key; callers do not supply a retained disk address. Given an `IRepository<IContact>` and an existing `contact`:

```csharp
await using var edit = await repository.BeginEditAsync(contact.Envelope!.EnvelopeCacheKey);
edit.Working.Name = "Updated";
try
{
    var committed = await edit.SaveAsync();
    // committed is the actual stored result, with its accepted revision.
}
catch (WriteConflictException<IContact> conflict)
{
    var current = await conflict.ReadCurrentAsync(); // fresh, isolated; may be null
    var original = await edit.ReadOriginalAsync();  // original committed bytes
    // Decide explicitly whether to abandon this edit and begin a new one.
}
```

`Working`, each original read, and committed-current reads are isolated through the configured serializer/lifecycle. A mutable cache entry is never the historical baseline. The session holds an opaque reference, not a previous object or envelope chain. Do not mutate its key, revision or locator, save `Working` directly, or modify it concurrently with `SaveAsync`.

A successful session is single-use and releases its baseline. A rejection, validation exception or pre-commit cancellation leaves the session available until disposal/expiry. Disposal is idempotent. An already-admitted save/read keeps its own hold until it finishes; expiry/disposal prevents new uses. `Renew(duration)` is explicit, cannot resurrect an expired scope, and cannot exceed either the configured lease duration or total maximum lifetime. Expired handles raise `RetainedVersionUnavailableException`.

Limits apply to each opened retention registry. Multiple File processes have independent admission budgets, so deployments must budget their aggregate disk use. Shared physical SegmentCore records count once toward its retained-record/byte limit, with one hold per editor; File captures one encoded copy per acquisition. Pending failed deletions continue to count against archive space/admission. Statistics distinguish active sessions, protected record bytes, physically present archive bytes, successfully published copied bytes, historical read bytes, and failed cleanup attempts. Encoded payload/frame accounting is not an exact filesystem-allocation measurement.

## Capability and provider behavior

```csharp
if (repository is not IRevisionEditingRepository<IContact> capability ||
    !capability.RetainedVersions.Supported)
    throw new NotSupportedException("This repository cannot provide retained editing.");
```

File requires retention options, sidecar write locks, the supplied conditional coordinator, and an identity factory able to resolve an existing physical key without a mutable object. An incompatible custom identity factory fails acquisition explicitly. The `.edit-incarnation` payload extension is reserved and reports editing as unsupported. History is excluded from hydration, watchers, recovery scans, encryption rotation and ordinary writes even when the configured payload extension is `.version`. Both Durable and Performance modes retain their existing commit guarantees. Every acquisition copies and flushes the exact encoded committed payload into `.idb-history` under the ordinary payload lock. This is additional disk work; retention does not make Performance mode crash-durable.

File uses a small `.edit-incarnation` marker for keys that have been edited. Saves in an enabled session validate it under the same cross-process guard. Deletes remove the marker before deleting the payload, including deletes from an instance with retention disabled; consequently File deletes perform one marker-existence probe even with retention disabled. A failed delete can conservatively invalidate an editor. Markers for live keys occupy 32 bytes each, in addition to existing lock metadata. Overwrite outside the coordinated API and live restoration over an open store are unsupported.

SegmentCore registers the selected committed physical address under index/mutation coordination. During compaction it copies only held encoded records from retiring segments to the history namespace, without materializing properties or running application strategies. It publishes each completed archive mapping before retiring readers/old segments. New acquisitions join that protection protocol. Latest-key indexes, checkpoint indexes, normal queries and notifications never include historical records. Releasing one editor does not release another's hold. Failed relocation leaves old addresses usable. Additional copy space is included in compaction admission, and the final retirement pause includes copying the retained records.

Registry cleanup uses finite monotonic leases and an explicit owner lock. Enabled-store reopen reclaims abandoned history while preserving another live File process's owner directory. Failed deletion is observable and retried. The cleanup guard waits at most about one second before reporting contention/I/O failure; orphan scanning examines up to 1,024 owner directories per open. History paths reject reparse points in their managed namespace. Store paths and filesystem access remain trusted administrative configuration.

Leases are process/session scoped. Repository close or restart invalidates handles; serialized references are not resumable offline drafts. SegmentCore backups include current/recovery data, not the separate history namespace. For external File copies, exclude session history or reopen with retention enabled to clean abandoned artifacts. Close stores before restoring. Restored stores issue new owner/session identities. These capabilities provide no cross-store or distributed merge promise; wrappers and third-party adapters that do not forward the capability report unsupported.

## A free custom strategy

The interfaces do not require any additional assembly. This example preserves strict behavior and supplies a structured diagnostic:

```csharp
public sealed class ExplainStale<T> : IWriteConflictStrategy<T>
    where T : class, IStoredContract<T>
{
    private readonly StrictTimestampMatchStrategy<T> strict = new();
    public async ValueTask<EnvelopeWriteDecision<T>> DecideAsync(
        EnvelopeWriteConflict<T> context, CancellationToken token = default)
    {
        var result = await strict.DecideAsync(context, token);
        return result.Accepted ? result : new EnvelopeWriteRejected<T>("stale-write");
    }
}
// Register IWriteConflictStrategy<IContact> before registering the repository.
```

An independently supplied resolver can declare `HistoryRequirement = OriginalCommittedVersion`, inspect `OriginalVersion`, read isolated original/current/incoming values lazily, and use `PrepareResolvedAsync` to stage an independent replacement. The provider authenticates the edit association and performs its decision under the authoritative write guard. Declaring a history requirement does not automatically retain ordinary reads: the application must use an explicit edit session.

Decisions can commit, reject with property names/reason, or return `EnvelopeWriteNoChange<T>`. A no-change decision returns actual committed current state, skips physical writes/index updates/events and does not advance its revision. A rejected incoming value is never returned as a successful save. Replacement candidates preserve the current synchronization metadata through the staging helper. They cannot change physical identity or resurrect a deleted entity incarnation. Resolvers and validators must honor cancellation and bounded work; nested storage writes, edit acquisitions and maintenance from them are rejected before entering provider guards. Notification callbacks retain their normal post-commit behavior outside those guards.

The strict strategy remains usable with retention disabled, including rejecting both older and newer mismatching tokens. No baseline objects, registry, background history timer, archive files or lease enumeration are created for ordinary disabled saves. The implementation keeps strict async commit state separate from optional outcome callbacks.

Runnable examples and checks are in `InterfaceDb.Tests/RetainedEditingTests.cs`, `RetainedRegistryTests.cs`, `RevisionWriteTests.cs`, and `InterfaceDB.Storage.SegmentCore.Tests/RetainedSegmentTests.cs`. The new retention behavior has been exercised on Windows/.NET 10. This does not claim physical qualification on mobile, macOS or Linux targets.
