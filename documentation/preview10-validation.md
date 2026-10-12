# Preview.10 local release validation

12 October 2026, Windows x64, .NET SDK 10.0.401, runtime 10.0.12, Release configuration.
The local results describe the prepared `1.0.0-preview.10` working-tree candidate. The subsequent
publication and verification are recorded separately below.

## Publication verified

On 12 October 2026, release commit `c5f5a97427ef9589fc2642fb1e2877ad989bd0fd` and tag
`v1.0.0-preview.10` were pushed to the public repository.

- [Release workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/38282743795): succeeded;
  525 test passes and two skips on Linux, integration/materialization/smoke checks passed, both
  package boundaries passed, and NuGet accepted both uploads.
- [Windows and Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/38282743854): succeeded.
- Both [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.10) and
  [SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.10)
  became available in NuGet's download feed and were restored into a new isolated cache.
- The published-package consumer passed all 12 File/SegmentCore checks. Its configuration used
  NuGet.org only; `.nupkg.metadata` confirmed that source for both database packages.

| Published package | SHA-256 of downloaded nupkg |
| --- | --- |
| InterfaceDB.1.0.0-preview.10.nupkg | `746C8F378FF77152F861D6DD7B79D95FE61006DAF5659C83C8B4680313FDED9F` |
| InterfaceDB.Storage.SegmentCore.1.0.0-preview.10.nupkg | `DE2864694A1F65C9F6D98497DA5227B13B3A02785739124CD6213542E955D8B9` |

Published-consumer evidence is retained under `artifacts/preview10/published-consumer*` and
`artifacts/preview10/published-cache`. These are separate from the initial local package artifacts.

## Local preparation results

| Check | Result |
| --- | --- |
| Restore and full solution build | Passed, zero errors; `CS9057` treated as an error and analyzers enabled |
| Core tests | 320 passed, 2 platform skips |
| SegmentCore tests | 198 passed, 2 platform skips |
| Analyzer tests | 5 passed |
| Standalone integration harness | 50 scenarios passed |
| Materialization self-test | Passed for InterfaceDB MetadataOnly, Microsoft.Data.Sqlite and Dapper |
| Repository smoke | File and SegmentCore, each with MetadataOnly/WeakObject/StrongObject; 200 complete objects per case |
| Quick SegmentCore production pilot | Passed mutations, indexes, checkpoint, compaction, verified backup, reopen and restore; final 2,100 records |
| Public package boundary | Exactly two MIT packages; one expected runtime assembly and one public README each; allowed dependencies only |
| Clean package consumer | 12 checks passed across File and SegmentCore: running version, subscription readiness, revision, changed callback, polymorphic query and persisted reopen |
| NuGet vulnerability report | No vulnerable direct/transitive packages reported across 13 public solution projects |

The **523 public test passes** exclude four skipped cases requiring a case-sensitive filesystem
or symbolic-link privileges unavailable on this Windows setup. Eight existing `MSTEST0032`
warnings concern constant assertions in `SegmentCoreCompatibilityTests`; contract analyzers
remain enabled. No failing test was accepted.

The clean consumer uses package references only, an explicit NuGet configuration mapping both
database IDs to the candidate feed, and an isolated package cache with fallback folders cleared.
NuGet metadata confirms both database packages came from that local feed. The final restore,
build and execution passed.

## Evidence and commands

See [release preparation](releasing.md) for build, test, pack and boundary commands. Additional gates:

```powershell
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Transactions -- --segment-core-production-pilot --quick --output artifacts/preview10/production-pilot.json
dotnet list InterfaceDB.sln package --vulnerable --include-transitive --format json
```

Local, Git-excluded evidence is under `artifacts/preview10/`: build/restore logs, test TRX files,
integration/materialization logs, smoke/pilot JSON, dependency audit JSON, package-boundary log,
consumer source/configuration, isolated restore cache and consumer log. Candidate packages are
under `artifacts/preview10/packages/`.

| Candidate package | SHA-256 |
| --- | --- |
| InterfaceDB.1.0.0-preview.10.nupkg | `36B77A2DC11D02EE28DF73CAB1308312C770A937A8B419ADFBD5B9C852E0A851` |
| InterfaceDB.Storage.SegmentCore.1.0.0-preview.10.nupkg | `A4FFF40040C923B7BD328F8065364FC88EA73E8E3E921003E0ACEC4C75A0420A` |

These hashes identify local preparation artifacts. Committing and rebuilding through the release
workflow can change package metadata and hashes. Verify eventual published files separately.
These local measurements preceded the confirmed publication above. No application deployment
was performed by this library release.

## Remaining qualification work

Follow [preview.10 upgrade guidance](preview10-release-notes.md), especially for encrypted stores
upgrading from the previous published version, preview.8.

Physical power-cut evidence, representative application/device soak, additional hardware and the
[documented security/platform qualification](roadmaps.md) remain separate stable-release work.
