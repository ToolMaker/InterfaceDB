# Preview.12 baseline validation — 21 October 2026

Status: **local candidate validation passed; not published**.

## Frozen runtime

Public commit `4e63ddb786d7d89c4151e62535e623090b33cba3` is the committed
correctness/A1 baseline. Package version and release documentation changes do
not include subsequent performance prototypes.

The preceding verification passed 791 public tests with 12 platform skips:
core 391, SegmentCore 216 and public transport 184. Its 18-process Windows
comparison passed 117 engine/cache configurations and 1,413 measurement rows.
The source fingerprints still matched the tested runtime when it was committed.

## Candidate gates

The four candidate packages were built from version/documentation commit
`aa7c116c3ddae8a2058d8e83ba6d74482ab1b323`. It changes no runtime code after
`4e63ddb`. Package repository metadata and matching preview.12 dependencies were
checked. Subsequent validation documentation does not change these artifacts.

Fresh Windows Release validation passed:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core | 391 | 10 |
| Analyzers | 31 | 0 |
| SegmentCore | 216 | 2 |
| Authentication | 61 | 0 |
| Public transport | 184 | 0 |
| **Total** | **883** | **12** |

The skips cover POSIX enter-only directories, case-sensitive paths and symlink
alias checks unavailable in this Windows setup. They are not counted as passes.
This run did not repeat Linux, Android/device, live Keycloak or power-cut tests.

Additional gates passed:

- Full solution restore and Release build: zero errors. Eight existing
  `MSTEST0032` warnings remain, plus four dependency-advisory warnings described below.
- Integration harness, including encrypted historical-key reads, Windows
  Credential Manager and eager encrypted migration.
- Materialization contract self-test for InterfaceDB and both SQLite consumers.
- Repository smoke with 200 complete objects in all three cache modes, and the
  32-record review smoke in all three cache modes.
- Source boundary check across 28 public projects; package boundary check across
  exactly four MIT packages, each with its one permitted assembly.
- Clean .NET 10 package consumer using a fresh isolated package cache and no
  project references. All four resolved package versions are preview.12.
- Package-consumer JWT/TLS save/read/update, disposable pub/sub readiness and
  server reopen. Embedded File and SegmentCore each passed MetadataOnly,
  StrongObject and WeakObject save/query/notification/reopen checks, including
  unsaved-edit isolation and independent returned copies.

### Dependency audit

NuGet's vulnerability audit reports no known vulnerable dependencies in the four
public package projects or the isolated consumer graph at the time of this run.
This is a dependency-advisory check, not a new comprehensive security audit.

The solution still reports `System.Security.Cryptography.Pkcs` 9.0.18 in two
benchmark projects, with one High and one Moderate advisory
(`GHSA-846f-f764-76mx`, `GHSA-2fmv-rv2c-59x7`). That dependency is absent from the
four public package graphs. It remains separate benchmark-tooling maintenance;
no dependency change was mixed into the frozen performance comparison.

### Candidate artifact checksums

SHA-256 of the locally validated `.nupkg` files:

| Package, all version 1.0.0-preview.12 | SHA-256 |
| --- | --- |
| InterfaceDB | `c8bc019b8a43a982925dfcf33c94add53cdd51766cd98b37e1cb5718fe7759d1` |
| InterfaceDB.Authentication | `a95207d467dc1d27c671e82db8148cdad42737cd7c5f6a1fbf7e4b2fe2a6e175` |
| InterfaceDB.Storage.SegmentCore | `f6d5e710049fbe796918a529334ffcf8a4965e3f4e8a766ff55264c951430c9e` |
| InterfaceDB.Transport | `671a0ef2b958e109eb8dbf9c416824db78d3266a97cd5c2330d3735792432455` |

Local evidence is retained in `.codex-preview12-baseline-20261021`: TRX results,
build/pack/integration logs, raw audits, consumer source and assets, and
`candidate-manifest.json`. These local artifacts are not included in the public
packages. Candidate packages are not evidence of NuGet.org publication.

The NuGet feed checked before preparation advertised preview.11 for all four
public packages and no preview.12.

### First release attempt and benchmark correction

The first tagged [release run](https://github.com/ToolMaker/InterfaceDB/actions/runs/39628090759)
passed all five Linux test suites (893 passed, two platform skips), integration,
materialization and complete-repository smoke. It stopped before packing or
publication when the storage-review harness required exactly one deserialize
for a collected weak-cache result.

That assertion predated committed-copy isolation. A collected entry legitimately
materializes the caller object and an independent private cache copy. A surviving
weak entry only needs a caller copy; temporary-root lifetimes vary by runtime.
The harness now requires one deserialize per result plus at most one private
copy, while preserving full-object count/checksum and durable-reopen checks.
The two new File/SegmentCore regressions prove a genuinely collected miss creates
two independent objects, unsaved edits remain private, and reads do not serialize
again. All 14 cache-isolation test cases and three six-configuration review smoke
runs passed locally. Only benchmark, test and documentation files changed;
published library source and the measured performance baseline are unchanged.

Nothing was published by the failed attempt. The unpublished preview.12 tag is
being advanced to include this corrected release check before retrying.

The [release procedure](releasing.md) describes publication and subsequent
public-feed verification. Real-device and physical power-loss qualification
are outside these local checks.

The [cache-read profile](cache-read-profile-20261021.md) records the next
performance investigation. No further runtime optimization was adopted from
those diagnostic traces.
