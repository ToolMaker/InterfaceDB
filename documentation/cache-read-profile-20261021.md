# Committed-cache read profile — 21 October 2026

## Decision

Keep runtime baseline `4e63ddb`, including committed-value copies and A1.
The new traces identify deserialization/materialization and allocation as the
main follow-up targets. They do not establish a further safe optimization or a
measured gain, so no new runtime change was made during preview.12 preparation.

The [three-build comparison](performance-proposal-review-20261020.md) remains the
performance baseline. In particular, cached scans and large cached result sets
are slower than the old shared-object behavior. The profile does not remove or
reclassify those regressions.

## Method

Two separate Windows .NET 10.0.12 diagnostic processes used the same frozen
Cache + A1 binaries as the completed comparison. Each used 10,000 records,
StrongObject with retained caller copies, ExplicitFlush durable seeding and 800
query iterations. The predicates were the existing culture-sensitive prefix
scan and the 3,251-result broad conjunction. Both reports passed their
full-object correctness checks and recorded no failures.

Collection used `dotnet-trace` with `dotnet-sampled-thread-time,gc-verbose`,
converted to Speedscope. These are sampled thread-time stacks, **not kernel CPU
percentages**. Collection includes seeding, warmup and validation; analysis below
selects intervals whose stack contains `CommittedEnvelopeSnapshot.Materialize`.
It does not perfectly isolate timed query bodies or attribute inlined work.
Do not use traced elapsed times as a new benchmark comparison.

## Observations

Exclusive leaf shares within the selected materialization stacks:

| Reported leaf | Prefix scan | Broad conjunction |
| --- | ---: | ---: |
| Generated contract MessagePack deserializer | 34.4% | 33.3% |
| `CommittedEnvelopeSnapshot.Materialize` | 29.8% | 29.9% |
| `BinaryObjectSerializer.DeserializeCore` | 20.9% | 21.5% |
| MessagePack dictionary deserializer | 8.2% | 7.7% |
| GC polling worker | 6.5% | 7.3% |

Selected sampled thread time totals were about 12.81 seconds and 7.88 seconds,
respectively. Small remaining leaves and rounding account for the rest.
`Materialize` includes inlined operations, so its whole share must not be called
the byte-copy cost. GC polling is also not a measurement of total GC pause time.

This is consistent with the allocation increase already measured by the clean
runner. Full scans materialize independent objects before evaluating caller
predicates, including records the predicate later rejects. The two profiles
do not identify another large redundant lock operation comparable to the
previously removed cache bookkeeping overhead.

## Constraints on any next experiment

- Keep the private committed snapshot and every caller's mutable graph isolated.
  Caller predicates must not be given private cached objects: a predicate can
  mutate or retain its argument.
- Keep ownership of input bytes. Arbitrary configured formatters can retain
  slices, so removing the owned payload copy is not a general safe shortcut.
- Preserve configured serializers, encryption context, key rotation, runtime
  allowlists, metadata and weak-cache lifetime.
- A serializer-owned materialization capability or a narrowly validated header
  fast path could be prototyped separately. These are hypotheses, not approved
  speedups. Require nested-mutation/retained-slice correctness tests and repeated
  clean before/after timings, allocations and tails before adopting either.

No default cache, durability, query semantics or publication behavior changed.
File, WeakObject, encrypted workloads, mobile devices and Atlas were not profiled
in these two diagnostic runs.

Local evidence is retained in `.codex-cache-read-profile-20261021`: the two
`committed-cache-*.nettrace` and Speedscope files, benchmark JSON reports, trace
logs, `analyze-stacks.py` and its materialization summaries. The initially
rejected Windows `cpu-sampling` request is also retained; the successful runs
use the supported `dotnet-sampled-thread-time` profile listed above.
