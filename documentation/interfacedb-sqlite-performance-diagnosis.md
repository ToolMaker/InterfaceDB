# InterfaceDB And SQLite Performance Diagnosis

## Finding

InterfaceDB's indexes and serialization were not the dominant cause of the Durable write gap. The file-per-object commit protocol was. The original path created and durably touched a writer lease, a payload sidecar, and a proposal file for every object, then published the proposal with two renames.

A low-level filesystem probe reproduced the original 15 ms median without executing any InterfaceDB serialization or index code. Removing all search indexes changed Durable write time by less than normal storage variance.

## Implemented Optimization

The file provider now:

- uses empty, exclusive lock files instead of writing and flushing unused owner metadata;
- creates migration writer leases as synchronization handles without a redundant durability request;
- relies on the proposal handle's `WriteThrough` behavior instead of following it with a second explicit `FlushToDisk` barrier;
- retires per-identity in-process semaphores after the final waiter completes;
- runs asynchronous bulk workers directly instead of wrapping them in `Task.Run`.

The protocol still retains its tested writer lease, per-payload sidecar lock, `.writing -> .ready -> canonical` publication, last-operation-wins ordering, and abandoned-ready recovery.

## Result

On the same Windows/NVMe development machine and 1,000-object quick workload:

| Configuration | p50 | p95 | Operations/s |
|---|---:|---:|---:|
| InterfaceDB Durable, earlier protocol | 15.299 ms | 16.291 ms | 64 |
| InterfaceDB Durable, current protocol | 5.626 ms | 6.332 ms | 174 |
| SQLite FULL, direct provider | 3.019 ms | 3.211 ms | 323 |
| SQLite FULL, Dapper | 3.094 ms | 3.274 ms | 316 |

That is a 2.72x improvement to InterfaceDB's median Durable object write. The full integration harness, cross-process ordering tests, migration tests, encryption rotation tests, and a forced-termination production-gate cycle still pass.

## Why SQLite Is Still Faster On Durable Writes

The remaining difference is structural rather than a surprising managed-code hotspot:

- InterfaceDB publishes one independently recoverable canonical file per object.
- Each Durable object still needs a root writer marker, a per-payload lock, one proposal file, and two publication renames.
- SQLite reuses database/WAL files and commits through its page/WAL machinery.

The standalone probe measured a reused append handle in the same physical cost class as SQLite FULL. That supports an append-only InterfaceDB provider, but it does not justify weakening the existing file provider's per-object file and recovery semantics.

## Remaining Safe Work

Query-side fixed costs still deserve measured optimization:

- cache reusable normalized plans and compiled predicate shapes;
- specialize tiny exact candidate sets;
- avoid residual execution only when a plan proves complete semantic coverage;
- reduce per-query candidate-set allocation;
- rebuild or persist index metadata more efficiently for large reopen workloads.

Further Durable write improvements inside the current protocol require a replacement for one of its synchronization/publication mechanisms, not simply deleting it. That is why the next storage experiment belongs behind `IEnvelopeRepository<TContract>` as a journal provider.

See [Current Performance Results](performance-results.md) for the current numbers and [Production Benchmark And Stress Gate](production-benchmark-gate.md) for the comparison contract.

## References

- [SQLite write-ahead logging](https://www.sqlite.org/wal.html)
- [.NET `RandomAccess.FlushToDisk`](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.flushtodisk?view=net-10.0)
- [.NET `FileOptions.WriteThrough`](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions?view=net-10.0)
- [Windows `FILE_FLAG_WRITE_THROUGH`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea)
