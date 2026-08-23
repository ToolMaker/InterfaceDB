# InterfaceDB contains-index prototypes

This standalone .NET 10 project compares candidate structures for exact, ordinal,
case-sensitive substring search over InterfaceDB string properties. It deliberately
stays outside `InterfaceDB.sln` while the design is experimental.

Every implementation is checked against:

```csharp
value.Contains(needle, StringComparison.Ordinal)
```

The validation corpus includes empty strings and needles, embedded NUL characters,
surrogate pairs, combining characters, repeated substrings, punctuation, and line
breaks. Candidate-producing indexes always verify candidates with the BCL operation.

## Candidates

- .NET object-by-object and packed UTF-16 arena scans
- Allocating string/`HashSet` trigrams
- Packed 48-bit UTF-16 trigrams with sorted postings
- Packed trigrams with adaptive sorted-array/dense-bitmap postings
- Packed trigrams with adaptive Roaring postings
- 256-bit and 1,024-bit bit-sliced probabilistic signatures
- Native NLibSais suffix array and a segmented suffix-array plus mutable delta
- CaseON managed suffix array
- Full and sampled WaveletMatrix FM-indexes
- Lucene.Net exact n-gram terms
- LIFTI whole-token wildcard search
- Aho–Corasick multi-pattern batch search
- NLibSais, DeltaQ SAIS/GoSAIS, and CaseON suffix-construction backends

## Commands

Run from the repository root:

```powershell
dotnet run --project .\ContainsIndexBenchmarks\ContainsIndexBenchmarks.csproj -c Release -- --validate
dotnet run --project .\ContainsIndexBenchmarks\ContainsIndexBenchmarks.csproj -c Release -- --matrix
dotnet run --project .\ContainsIndexBenchmarks\ContainsIndexBenchmarks.csproj -c Release -- --matrix --no-million
dotnet run --project .\ContainsIndexBenchmarks\ContainsIndexBenchmarks.csproj -c Release -- --bdn
```

`--matrix` measures deterministic random, name/email, URL, log, repetitive, Unicode,
long-string, 100k-record, and one-million-record workloads. It records build time,
retained-memory deltas, cold and warm query latency, allocation, candidate accuracy,
1,000-item update/rebuild cost, read concurrency, suffix construction, and batch
multi-pattern search.

`--bdn` runs BenchmarkDotNet with memory and disassembly diagnostics for the finalists
and the packed-key microbenchmark.

## Results

- [Decision and analysis](results/ANALYSIS.md)
- [Full matrix report](results/RESULTS.md)
- [BenchmarkDotNet summary](results/BENCHMARKDOTNET.md)
- CSV files in `results/` contain every measured row.

Query timings include candidate intersection, ordinal verification, and an ID digest.
They intentionally exclude InterfaceDB physical-key lookup/materialization, which must
be measured again when the chosen index is integrated. Process-private memory deltas
are approximate; managed retained-memory deltas are the more useful comparison for
the managed candidates.
