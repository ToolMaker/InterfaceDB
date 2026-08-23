# Search Index Strategies

InterfaceDB keeps in-memory secondary indexes per eligible property. Indexes
produce physical envelope keys; the repository materializes those envelopes and
evaluates the complete compiled predicate before returning a result. Candidate
indexes therefore improve retrieval without weakening query correctness.

## Exact Equality

`==` uses dictionary-to-key-set semantics. This remains the fastest and
simplest property equality path; RadixForge is not used as a dictionary
replacement. Equality on the repository's logical storage identity remains the
envelope catalog's responsibility.

## Ordinal Prefixes And Suffixes

`string.StartsWith(prefix, StringComparison.Ordinal)` uses
`RadixIndex<string, string>` with `RadixKeyCodecs.Utf16Ordinal` in direct
mutation mode. Prefix estimates use `CountByPrefix`, and retrieval visits the
matching subtree into a pre-sized ordinal key set.

`string.EndsWith(suffix, StringComparison.Ordinal)` uses a second radix index
with a reverse UTF-16 codec. Reversing both stored and query code units turns an
ordinal suffix relationship into a prefix lookup without allocating reversed
strings.

The one-argument `StartsWith` and `EndsWith` overloads are culture-sensitive,
so they cannot be answered safely by ordinal indexes. They fall back to the
normal scan. Ordinal-ignore-case and culture-aware overloads also scan.

## Literal Ordinal Contains

A property configured with `SearchIndexMode.Contains` owns a RadixForge
`AdaptiveGramIndex<string>`. It maps n-gram postings to physical keys, intersects
selective postings, and verifies the actual substring to eliminate gram false
positives. Both of these shapes are indexable:

```csharp
item => item.Name.Contains("ann")
item => item.Name.Contains("ann", StringComparison.Ordinal)
```

The one-argument `Contains` overload is ordinal in .NET. Ignore-case and
culture-aware overloads are not indexable by this structure.

`!item.Name.Contains(term)` does not create a second physical index. A negative
predicate is normally broad, so it remains a residual filter behind another
positive index path. If it is the only condition, InterfaceDB scans rather than
allocating the complement of a potentially small posting set.

Contains is explicit rather than part of the string default because posting
lists consume additional memory and add write work.

## Ordered Ranges

`>`, `>=`, `<`, and `<=` use a typed adaptive range manager. Two compatible
bounds on the same property, such as `Age >= 18 && Age < 65`, become one bounded
index query.

The mutable strategy is selected by distinct-value cardinality:

| Distinct values | Strategy |
|---:|---|
| 0–1,024 | `SortedList<TValue, HashSet<string>>` with binary bounds |
| 1,025–4,095 | Keep the current strategy as a hysteresis band |
| 4,096+ | RadixForge direct mode when an order-preserving codec exists |

When a radix-backed index falls to 1,024 distinct values, it migrates back to
sorted buckets at that mutation boundary. The thresholds select a structure;
they do not limit how many values or records can be stored.

InterfaceDB uses RadixForge codecs for strings and fixed-width integers.
Internal order-preserving codecs cover `float`, `double`, `decimal`, `DateTime`,
`DateTimeOffset`, and `TimeSpan`. Unsupported explicit range types stay on the
correct sorted-bucket path.

Using `d` for distinct values, `r` for matching buckets, and `k` for returned
physical keys:

| Strategy | Estimate | Materialize | Distinct-key mutation |
|---|---|---|---|
| Sorted buckets | `O(log d + r)` | `O(log d + r + k)` | `O(d)` insertion/removal |
| Fixed-width radix | bounded key-depth navigation | navigation + `O(k)` | bounded key depth plus terminal maintenance |

No index can avoid `O(k)` when it must materialize `k` keys. The gain is avoiding
an `O(d)` scan of every distinct value before output begins.

## Planner Strategy

The planner obtains cardinality estimates from the current index data. For an
`AND`, it starts with the lowest-cost path and considers additional
intersections. An intersection is accepted only when the estimated reduction
exceeds its key-set cost. Avoided object loads are valued most in
`MetadataOnly`, less in `WeakObject`, and least in `StrongObject` cache mode.

Compatible range comparisons are consolidated before costing. Unsupported or
negative terms remain residual filters if another positive path safely bounds
the query. An `OR` unions branch candidates only when every branch is plannable;
one unbounded branch forces a full scan so no valid result is omitted.

The final compiled expression validates all candidates. Call
`IIndexManager<TContract>.ExplainSearch` to inspect the selected plan without
executing the query. See [Query Planning](query-planning.md).

## Attribute Defaults

`[SearchIndex]` defaults are:

- `string`: exact + ordinal prefix;
- `bool`, enums, and `Guid`: exact;
- other supported comparable scalar values: exact + range.

Use an explicit mode combination for suffix or contains workloads:

```csharp
[SearchIndex(
    SearchIndexMode.Exact
    | SearchIndexMode.Prefix
    | SearchIndexMode.Suffix
    | SearchIndexMode.Contains)]
string Email { get; set; }
```

`[NotSearchIndexed]` excludes a member inherited from a type-level declaration.

## Measured Direction

The 100,000-item .NET 10 ShortRun showed adaptive-gram contains at 44.36
microseconds versus 3,173.83 microseconds for a full ordinal scan, about 71.5
times faster for the rare-term workload. It also showed why intersection must
be conditional: always intersecting a broad conjunction took 5,480.59
microseconds, while the best single index plus residual filtering took 2,426.34
microseconds. The strong-cache cost-aware plan correctly chose the latter shape
and measured 2,503.28 microseconds.

These are isolated in-memory index benchmarks. File-backed materialization can
make reducing candidates far more valuable, which the cache-aware cost model is
designed to represent.

## Remaining String Work

Case-insensitive substring search, culture-aware search, token/full-text search,
fuzzy matching, and semantic vector search need separate semantics and index
structures. A vector index is not a replacement for literal substring search.
