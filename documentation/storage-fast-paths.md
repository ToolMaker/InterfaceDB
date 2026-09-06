# Embedded storage fast paths

## Single-object Segment Core writes

`IRepository<T>.SaveAsync` and a single delete use the owning stripe's durable atomic record append. The repository still stages the envelope, holds the same per-key revision guard through validation and committed cache publication, and updates indexes and notifications through the existing repository pipeline. A successful save still waits for the selected durability policy. This is not fire-and-forget persistence.

`SaveManyAsync` and explicit storage transactions retain coordinated transaction commit, rollback and atomic index publication. This includes explicitly submitting a batch of one. Transaction statistics count these coordinated transactions; ordinary single saves/deletes no longer create an internal transaction or increment its lifecycle counters. They remain atomic writes. Raw-store callers must continue using explicit transactions for multi-record atomicity.

Revision conflict rules, retained edit leases, envelope format and public durability settings are unchanged. Normal repository initialization remains required before comparing ready-to-use operation latency. No additional subscriber work is introduced when repository notifications are disabled.

## Recovery reuse

For an existing store without a usable checkpoint, recovery validates and repairs each stripe once. The store privately supplies those scan results to writer activation while continuously holding its exclusive store ownership lease. Standalone public object-log opens still validate their own files; no public caller can opt out of recovery validation. Both participant-commit and mutation-WAL store recovery retain their existing transaction reconciliation checks. Reused tail repairs are counted once, and active-file lengths are checked before activation.

## Allocation reductions

Single-key revision admission selects the same lock as ordered multi-key admission without constructing a sorted/deduplicated identifier collection. Disposing a guard is idempotent. Bulk physical reads sort one locator array and process contiguous segment ranges instead of producing LINQ grouping and per-group copy arrays. Frame checksums, complete object materialization, missing-key behavior and provider-level requested ordering/duplicate handling are preserved.
