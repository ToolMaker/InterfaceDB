# Preview.12 baseline validation — 21 October 2026

Status: **published on NuGet.org and verified through a fresh public-feed consumer on 21 October 2026**.

## Release workflow

Tag `v1.0.0-preview.12` resolves to
`b9cbde5d590ee56eb8098e0bba24a8eefd121da1`. The
[release workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/39629190648)
passed all gates and NuGet accepted all four uploads on 21 October 2026 at
15:53 UTC. The separate
[Windows/Linux CI run](https://github.com/ToolMaker/InterfaceDB/actions/runs/39629191050)
also succeeded, including the Windows production pilot and integration harness.

| Release source verification | Core | Analyzers | SegmentCore | TCP | Authentication | Total passed | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Linux release workflow | 401 | 31 | 218 | 184 | 61 | **895** | **2** |
| Windows CI | 393 | 31 | 218 | 184 | 61 | **887** | **10** |

These totals include the two weak-cache regression cases described below.
The GitHub Windows runner could execute the symlink cases skipped locally.
No packaged runtime source changed between the prepared candidate and release;
the final addition corrects the benchmark assertion and adds regression tests.

## Published packages and consumer verification

All four public feeds advertise `1.0.0-preview.12`. The packages were downloaded
from NuGet.org, checked for their MIT license, allowed assembly/dependency
boundaries, release commit metadata and matching InterfaceDB dependency versions.

A separate .NET 10 consumer restored from NuGet.org into a fresh isolated package
cache, with no project references or local candidate feed. Its package-origin
records identify NuGet.org for all four packages, and the cached package hashes
match the independently downloaded files. The SDK additionally lists its built-in
`library-packs` directory; no InterfaceDB package was sourced from it.

The public-feed consumer passed JWT/TLS save, query, update, pub/sub readiness and
server reopen. File and SegmentCore each passed MetadataOnly, WeakObject and
StrongObject save/query/reopen checks, notification readiness, independent read
copies, and indexed searches before and after saving an edit. No private assembly
was loaded. The published consumer's transitive dependency audit reports no known
vulnerable packages at the time of this check.

SHA-256 of the published packages:

| NuGet package | SHA-256 |
| --- | --- |
| [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.12) | `f865b03c438e8c9f50e5d1fd0d531a06607819dbe283605590fcb1bb243ad3cf` |
| [InterfaceDB.Authentication](https://www.nuget.org/packages/InterfaceDB.Authentication/1.0.0-preview.12) | `3bf7fda033548781e9aaa159aba07b7a81bbf574944e36cbbd48559c9ffc9492` |
| [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.12) | `eb3bc3eee8434306aef661bc9b4aac8e27ff3aef894ddd0afdaa72666f9805f7` |
| [InterfaceDB.Transport](https://www.nuget.org/packages/InterfaceDB.Transport/1.0.0-preview.12) | `857e303b27b8742d839b30bd93a8cef395c327c1442a4609aa23b1a66a0fe4fc` |

Local evidence includes `published-manifest.json`, the final feed snapshots,
published packages, clean consumer source/restore/run/audit logs, and both release
attempt logs under `.codex-preview12-baseline-20261021`. The candidate checksums
below describe the earlier local Windows preparation; published hashes also
reflect the CI build, final release metadata and NuGet processing.

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

Nothing was published by the failed attempt. The unpublished preview.12 tag was
advanced with an explicit expected-old-tag lease to include this corrected
release check, and the retry above succeeded. The published tag is now fixed.

The [release procedure](releasing.md) describes publication and subsequent
public-feed verification. Real-device and physical power-loss qualification
are outside these local checks.

The [cache-read profile](cache-read-profile-20261021.md) records the next
performance investigation. No further runtime optimization was adopted from
those diagnostic traces.
