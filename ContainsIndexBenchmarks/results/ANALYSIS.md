# Contains-index decision

## Verdict

The recommended InterfaceDB foundation is a custom, managed, segmented adaptive
trigram index—not Lucene, LIFTI, an FM-index, Roaring as the default representation,
or a fixed-size probabilistic signature.

Use packed 48-bit UTF-16 trigram keys, dense integer document IDs, sorted postings for
sparse grams, managed `ulong[]` bitmaps for dense grams, rarest-first intersection,
and final ordinal BCL verification. Put immutable index segments behind a small
mutable delta and tombstones, then compact in the background. Retain direct BCL scan
as part of the query planner for needles shorter than three code units and for very
dense candidate sets.

This is not a claim that trigrams, bitmaps, or LSM-style segments are new. The useful
differentiation is a narrowly tailored combination for InterfaceDB's property index,
exact .NET ordinal semantics, update model, and physical-key pipeline.

## Why this won

At 100,000 records averaging 64 UTF-16 code units, the controlled BenchmarkDotNet
run measured the managed adaptive index at 1.445 µs for a selective natural trigram
and 1.713 µs for a rare 8-character needle. The corresponding .NET scans took
1,396.913 µs and 1,355.368 µs. The custom index was allocation-free during queries.

At one million records, the representative matrix measured:

| Query | Hits | Adaptive trigram | Packed sorted trigram | 1,024-bit signature | .NET scan |
|---|---:|---:|---:|---:|---:|
| Rare 8-char | 1,000 | 18.19 µs | 22.59 µs | 212.42 µs | 14,408.7 µs |
| Natural 3-gram | 982 | 22.99 µs | 29.56 µs | 348.30 µs | 14,873.5 µs |
| 1% / 16-char | 10,000 | 301.81 µs | 324.21 µs | 707.50 µs | 14,556.0 µs |
| 10% / 8-char | 100,000 | 2,390.03 µs | 2,919.03 µs | 2,480.93 µs | 15,355.9 µs |

The one-million-record build/retained-memory tradeoff was:

| Structure | Build | Managed retained Δ | Approx. bytes/input char |
|---|---:|---:|---:|
| Adaptive managed trigram | 16.03 s | 252.4 MiB | 4.07 |
| Packed sorted trigram | 15.64 s | 251.3 MiB | 4.06 |
| 1,024-bit signature | 2.37 s | 129.9 MiB | 2.10 |
| 256-bit signature | 1.87 s | 38.3 MiB | 0.62 |
| Packed arena scan | 73.2 ms | 131.5 MiB | 2.12 |

The adaptive representation matters most on low-entropy data. With 100,000
repetitive 128-character records, managed retained memory was 1.7 MiB versus 13.6 MiB
for always-sorted postings because common grams collapsed into dense bitmaps.

## What the alternatives taught us

### Native and segmented suffix arrays

NLibSais is the only serious alternative tested. At 100,000 records it built a full
suffix index in 296 ms versus 1.52 s for adaptive trigrams, and rare/selective queries
were close (roughly 2.1–2.3 µs). It also handles two-character needles efficiently:
about 201 µs versus roughly 1.4–1.6 ms for a scan.

Its drawbacks are material for InterfaceDB: the prototype retained about 64.8 MiB
at 100,000 records versus 30.8 MiB for adaptive trigrams, high-selectivity results
were slower (845 µs versus 194 µs at 10%), and an immutable array needs segmentation
for updates. The segmented prototype made 1,000 upserts in 0.174 ms and compacted
100,000 records in 241 ms, but its query path was 2–3× slower for rare matches and is
not yet concurrency-safe because it reuses a deduplication stamp array.

Recommendation: keep NLibSais as an optional follow-up for static fields or workloads
that require fast two-character contains. Do not make a native dependency and dense
position-to-document map the default InterfaceDB architecture yet.

### Bit-sliced signatures

Signatures have excellent bounded memory and cheap updates. One thousand updates took
6.0 ms for 256 bits and 11.8 ms for 1,024 bits. They remained exact because every
candidate was verified, but their false-positive rate depends heavily on field length.
For 2,048-character values, even the 1,024-bit signature admitted 7,612–9,763 of
10,000 records for representative needles and was effectively a scan. A signature
would need to scale or partition with text length, eroding its fixed-memory advantage.

Recommendation: not the primary general-purpose index. It may be useful as an opt-in
memory-saving filter for short, bounded fields.

### Roaring

Adaptive Roaring was competitive around 1% selectivity but slower for rare and 10%
queries, allocated a small native result object on dense intersections, and introduces
a native runtime dependency. Managed dense bitmaps were simpler and faster overall.

Recommendation: do not take a Roaring dependency for the default implementation.

### FM-index and managed suffix array

The full WaveletMatrix FM-index built slower and used more memory than the native
suffix array. Sampling reduced memory but made locating results very expensive. At
10,000 records, a sampled FM-index took about 24 µs for 10 rare matches, 333 µs for
100 matches, and 3.60 ms for 1,000 matches. CaseON's managed prefix-doubling builder
took 774 ms for 1.32 million UTF-16 units versus 51 ms for NLibSais.

Recommendation: neither is the InterfaceDB default.

### Lucene.Net and LIFTI

Both adapters returned correct results, but their general-purpose machinery is a poor
fit for literal ordinal property substring lookup. At 100,000 records, a rare query
took about 2.29 ms and allocated 4.2 MiB in Lucene; LIFTI took about 311 ms and
allocated roughly 394 MiB. The adaptive index took 1.85 µs with zero allocation.

Recommendation: do not add either package for this feature.

### Aho–Corasick

Aho–Corasick is not a replacement for a dynamic single-needle index. It becomes useful
when one request supplies hundreds of patterns:

| Patterns | Repeated .NET scans | Aho build + search |
|---:|---:|---:|
| 8 | 37.9 ms | 192.7 ms |
| 64 | 112.7 ms | 142.7 ms |
| 512 | 782.9 ms | 192.9 ms |
| 1,024 | 1,567.9 ms | 218.6 ms |

Recommendation: a separate future query plan for `ContainsAny` batches, not storage
for ordinary `Contains`.

## Proposed production query plan

1. Map InterfaceDB physical keys to stable dense `uint` document IDs per indexed
   property; keep the reverse mapping separately.
2. Pack each UTF-16 trigram into the low 48 bits of a `ulong`. BenchmarkDotNet measured
   47.25 ns and zero allocation for the packing loop versus 1,377.54 ns and 5,400 B
   for allocating string trigrams.
3. Build immutable postings in ascending document-ID order. Store a gram as sorted
   IDs while `4 * cardinality < 8 * ceil(documentCount / 64)`; otherwise store a
   managed dense bitmap. Treat that byte-equality point as a starting threshold to
   tune with real InterfaceDB data.
4. At query time, deduplicate query grams, choose the lowest-cardinality posting,
   intersect sorted arrays in place or bitmap words with bitwise AND, then verify each
   candidate using `Contains(..., StringComparison.Ordinal)`.
5. Return to a direct scan for zero-, one-, and two-code-unit needles. Optionally add a
   suffix or bigram index only for fields whose measured workload justifies it.
6. If the intersected candidate set is very dense, scan instead. The 93%-selective
   repetitive case favored the packed scan (2.93 ms) over the adaptive index (3.97 ms),
   while the index still won decisively at 10%. Start testing a 60–70% fallback
   threshold rather than hard-coding it without production data.
7. Store changes in a small mutable delta plus tombstones and periodically compact
   into new immutable gram segments. Use pooled or thread-local query scratch so
   concurrent readers remain allocation-free.

## Work still required before InterfaceDB integration

- Replay real InterfaceDB values and actual query distributions; synthetic data is
  useful for structure selection but cannot choose the final density thresholds.
- Add 30%, 50%, 70%, and 90% selectivity cases to tune scan fallback.
- Design persistent segment serialization, checksums, crash-safe replacement, and
  versioning rather than rebuilding every index at process startup.
- Define document-ID reuse, deletes, tombstone cleanup, and physical-key remapping.
- Validate reader/writer concurrency and remove shared mutable query state.
- Benchmark end-to-end physical-key materialization and object loading, not only ID
  discovery and verification.
- Test Server GC, Linux, and the deployment CPU architecture; this machine used AVX2
  and did not expose Vector512 acceleration.
- Add cancellation and result limits so broad contains queries cannot monopolize a
  request after candidate discovery.

The next implementation milestone should therefore be an InterfaceDB-independent,
production-quality `AdaptiveGramIndex` with immutable segments and a mutable delta,
followed by end-to-end integration behind the existing index manager. The native
suffix-array path should remain an experiment until real short-needle demand proves
that its extra memory and native dependency are worthwhile.
