# Query Planning

InterfaceDB's expression search path separates candidate retrieval from final
predicate validation. The planner chooses indexes that can safely narrow the
physical envelope keys; the repository then loads those candidates and applies
the original expression.

## Query Vocabulary

- A **predicate** is a condition from the LINQ expression.
- An **access path** is one exact, range, prefix, suffix, or contains index lookup.
- A **residual predicate** is evaluated after candidate retrieval.
- A **plan** is a full scan, index scan, intersection, or union.
- **Cardinality** is the estimated number of candidate physical keys.
- **Cost** is a relative score combining key-set work and expected object loads.

## Declaring Indexes

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
    int Age { get; set; }

    [SearchIndex(SearchIndexMode.Exact)]
    string Role { get; set; }

    [SearchIndex(
        SearchIndexMode.Prefix
        | SearchIndexMode.Suffix
        | SearchIndexMode.Contains)]
    string Name { get; set; }
}
```

`SearchIndexMode.All` enables every mode supported by the member type. Contains
and suffix are deliberately opt-in because each maintains additional data.

## Supported Expression Shapes

```csharp
person => person.Role == "Student"
person => person.Age >= 18 && person.Age < 65
person => person.Name.StartsWith("Ann", StringComparison.Ordinal)
person => person.Name.EndsWith("son", StringComparison.Ordinal)
person => person.Name.Contains("ann")
person => person.Name.Contains("ann", StringComparison.Ordinal)
```

One-argument `Contains` is ordinal and indexable. One-argument `StartsWith` and
`EndsWith` are culture-sensitive and scan. Ignore-case and culture-aware string
comparisons also scan unless a future index provides matching semantics.

## Planning An AND

For `A && B && C`, the planner:

1. Flattens the conjunction and combines compatible lower/upper bounds on one property.
2. Builds every safe access path and asks each index for its current cardinality estimate.
3. Selects the lowest-cost initial path.
4. Estimates the candidate reduction from each remaining path.
5. Adds an intersection only when its projected saving exceeds the set-operation cost.
6. Applies unsupported and negative conditions as residual predicates.
7. Validates every candidate with the complete compiled expression.

This is more robust than always picking indexes in declaration order or always
intersecting them. The smallest set is generally the best starting point, but a
second broad set can cost more to build than the object loads it avoids.

## Cache-Aware Costing

The same index cardinalities can justify different plans depending on object
materialization cost:

| Object cache mode | Relative avoided-load value | Expected tendency |
|---|---:|---|
| `MetadataOnly` | 64 | More willing to intersect to avoid file reads/deserialization |
| `WeakObject` | 12 | Balanced |
| `StrongObject` | 2 | Prefer one selective path unless intersection saves substantially |

These are relative planning weights, not elapsed-time promises. Final benchmark
data should guide future tuning.

## OR And Negative Predicates

An `OR` plan unions candidates only if every branch can produce a safe candidate
set. If one branch is unindexed, the planner scans the whole repository because
using only the indexed branches would create false negatives.

A negative predicate such as `!person.Name.Contains("test")` is normally too
broad to materialize as an index complement. Behind a positive condition, it is
a residual filter:

```csharp
person => person.Role == "Student" && !person.Name.Contains("test")
```

Without a positive anchor, it scans.

## Inspecting A Plan

Resolve the index manager registered for the repository and call
`ExplainSearch`. It does not load query results:

```csharp
var indexManager = services.GetRequiredService<IIndexManager<IPerson>>();
var plan = indexManager.ExplainSearch(
    person => person.Role == "Student" && person.Age >= 18);

Console.WriteLine(plan.PlanText);
Console.WriteLine(plan.UsesIndexes);
Console.WriteLine(plan.EstimatedCandidateCount);
Console.WriteLine(plan.EstimatedCost);
Console.WriteLine(plan.ResidualPredicateCount);
```

The explanation is diagnostic. Estimates can change as records are inserted,
updated, removed, or rebuilt.

## Correctness And Lifecycle

Exact, range, prefix, suffix, and contains indexes are updated on insert,
replacement, and removal. Index access is synchronized for concurrent readers
and writers. Candidate validation remains the final authority, so an index is a
retrieval optimization rather than a replacement for expression semantics.

## Benchmarking

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-validate
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-quick
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-benchmark --filter "*" --job short
```

Recorded .NET 10 ShortRun results at 100,000 records:

| Operation | Mean | Allocated |
|---|---:|---:|
| Full contiguous scan, broad conjunction | 302.72 us | 0 B |
| Previous smallest-single-index plan | 2,426.34 us | 1,509,285 B |
| Always intersect all usable indexes | 5,480.59 us | 2,557,760 B |
| Cost-aware plan in strong-cache mode | 2,503.28 us | 1,510,442 B |
| Full ordinal scan, rare contains | 3,173.83 us | 0 B |
| Adaptive gram contains path | 44.36 us | 4,897 B |

The contiguous scan result is intentionally included: on already-resident
objects and broad results, a linear array walk can beat hash-set materialization.
It does not represent metadata-only file-backed retrieval. The useful planner
finding is that unconditional intersection was about 2.26 times slower than the
single path, while the cost-aware strong-cache plan rejected it. The contains
index was about 71.5 times faster than the full ordinal scan for its workload.

BenchmarkDotNet writes the complete local report under
`BenchmarkDotNet.Artifacts/results/QueryPlannerBenchmarks-report-github.md`.

## Reusing Query Work

Compiled predicates use a bounded reusable-shape cache where supported; their bound constants and captured objects remain query-specific. The planner separately weak-caches syntax for the same expression instance: flattened AND/OR terms, possible bounded-range pairs and member paths. Expressions and closures can still be collected.

This does not cache answers or final cost-based plans. Captured bounds, string-comparison modes, index counts and access-path selection remain live on every execution. Reusing an expression can avoid parsing work; constructing a fresh expression each time does not reuse this syntax cache. See the [preview.6 measurements](performance-preview6-20260905.md) for scope and correctness evidence. The historical benchmark table above does not measure these changes.

## Next Improvements

- Add richer distribution/correlation statistics beyond cardinality estimates.
- Expose a public custom index provider contract.
- Add deterministic persistent/rebuild strategies for very large stores.
- Add explicitly defined ignore-case, full-text, fuzzy, and vector indexes.
