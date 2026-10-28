# Preview.13 release validation — 28 October 2026

Status: **all four `1.0.0-preview.13` packages were published to NuGet.org on 28 October 2026**.
Windows/Linux CI and release automation passed at commit
`f390bba5f0fea097ed398eac1827565c1e18f587`, tagged `v1.0.0-preview.13`.
The published packages passed 298 isolated consumer tests. The reviewed corrections follow
candidate `38a074c`; earlier candidate results and hold statements below describe review history.

The feature snapshot passed 874 public test cases with 12 platform-specific skips on Windows.
The 24 production benchmark rounds passed exact payload, partition, ordering and reopen checks.
See [feature validation](access-policy-production-20261028.md). Those results do not replace the
release workflow checks below.

The release verified package versions, source and package boundaries, full Windows/Linux CI,
analyzers, repository/integration smoke checks, and isolated package consumers. The tag workflow
used NuGet trusted publishing with a temporary credential. Public packages contain only
their intended MIT assemblies and allowed dependencies. Commercial verification remains private.

Release commit, workflow outcomes, package hashes and public-feed consumption are recorded
below. Live Keycloak, real mobile devices and physical power-loss qualification are not claimed
by these package checks.

## Published release verification

- [Windows/Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/40767373197) passed
  for the immutable release commit.
- [Release workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/40768512396) passed
  its checks, rebuilt the four packages from that commit and pushed all four successfully.
  Publication occurred at approximately 21:04 UTC; all four appeared in the public package
  feed by 21:20 UTC. No duplicate-package skip was used.
- The final local candidate consumer passed **298/298 tests**. A separate consumer with fresh
  package and HTTP caches then restored the published packages from NuGet.org and also passed
  **298/298: 237 transport and 61 authentication**, with zero failures or skips. These are
  repeated validation of the same cases, not 596 distinct tests. Five host-process cases remain
  in the full source suite because they require its executable layout.
- The package consumer used no production project references. Each InterfaceDB package's
  recorded source was NuGet.org and each nuspec referenced the release commit. The .NET SDK
  also supplied its implicit local framework library-packs source during the first restore;
  none of the four InterfaceDB packages came from it.
- Downloaded published packages passed the MIT, assembly and dependency boundary checks.
  All four NuGet repository signatures passed `dotnet nuget verify --all`. Their SHA-256 hashes are recorded below;
  they are the published artifacts, not the unsigned local candidate packages.

| Package, version `1.0.0-preview.13` | Published nupkg SHA-256 |
|---|---|
| [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.13) | `c524ca8e39364702ad3ed4120107f89b7f48a0c1447c51a8702889b38fbd3a11` |
| [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.13) | `7c0c0397e3a5941a2539bfea7af88a3a67479e0447139ca32372d706b91d7fc5` |
| [InterfaceDB.Authentication](https://www.nuget.org/packages/InterfaceDB.Authentication/1.0.0-preview.13) | `7e61f3ff1e26f3aa21aee52db9e956b0d52ab9574bc75023f8fb36d851810e90` |
| [InterfaceDB.Transport](https://www.nuget.org/packages/InterfaceDB.Transport/1.0.0-preview.13) | `e8ed9c2ae4ef85f8dbd8a992f42d8f579f4714cbfc388f4aea4b1e275fdd86bf` |

Local receipts, package inspection and hashes are retained under `.tmp/preview13-release/`;
consumer logs, TRX files and package origins are under `.tmp/preview13-final-consumer/runs/`.
Old preview.13 development packages in this machine's global cache were backed up where present
and replaced with the published packages; all four cached origins and release commits were checked.
This prevents a same-version development package from masking the release in subsequent builds.

The release tag remains on the tested runtime commit. This post-publication documentation receipt
does not change or republish its binaries. Upgrade all four public packages and TCP clients/hosts
together using [the release notes](preview13-release-notes.md#breaking-upgrade-from-preview12).

## Local candidate checks

- Release solution build passed, including analyzer compatibility enforcement.
- Core 393, analyzers 31, SegmentCore 216, TCP 204 and authentication 61 passed:
  **905 total**, with 12 platform-specific skips on this Windows machine.
- The integration harness and benchmark materialization contract passed.
- All four candidate packages passed MIT, assembly and dependency boundary inspection.
- An isolated candidate-package consumer with no project references passed **199 transport,
  authentication and policy tests**. Five host-process tests remain in the ordinary source suite.
- The initial dependency audit found two PKCS advisories only in SQL benchmark dependencies;
  those projects now pin patched 9.0.21. Public runtime packages do not include that dependency.

Local evidence is under `.tmp/preview13/`, `.tmp/preview13-*.log`, and
`artifacts/preview13/packages/`. Candidate package metadata predates the release commit;
the tag workflow rebuilds the published artifacts from its immutable commit.

The final local dependency audit reports no known vulnerable dependencies across the solution.
The six-provider/cache smoke combinations and three SegmentCore review smoke cases also passed.

## Review follow-up validation

The local corrections were tested from an isolated copy of the working tree; 482 source/build
inputs were compared by SHA-256 with the checkout. The nine initial regression cases failed
against the candidate before the corrections. The final focused selection passed 62 cases.

The full Windows public suite then passed **933 tests**, with the same 12 platform-specific
skips: core 393, analyzers 31, SegmentCore 216, transport 232 and authentication 61. The transport
suite includes 28 new regression cases. The integration harness also passed. The Windows
workspace boundary check counted 29 projects with no external references; two are ignored
historical projects. The clean release snapshot contains 27 public projects.

The controls include ordinary unrestricted/shared data, protected tenant data, custom providers
and indexes, pagination, rejected batches, cancellation during selection, ambiguous committed
writes, and migration/adoption/reopen across multiple scopes with intact revision clocks.
No durability, transaction, caching, encryption or identity-provider default was relaxed.

The designer subsequently reports all 232 transport tests passing on Linux against this first
correction snapshot, including the three original probes. This is supplied external evidence,
distinct from our local Windows runs and from CI for an immutable release tag.

## Paging and disconnect-recovery follow-up

The later snapshot passes **943 public tests**, with the same 12 platform-specific skips:
core 393, analyzers 31, SegmentCore 216, transport 242 and authentication 61. The integration
harness passes. The clean source boundary verifies 27 public projects. The build
retains eight existing MSTEST0032 warnings in `SegmentCoreCompatibilityTests`; no new warning
was introduced. These checks ran from an isolated source copy, compared by SHA-256 with the
working tree.

Six additional paging regressions cover hidden oversized payloads, response frame budgets,
custom index bypass, raw and tenant access, reserved-prefix lookalikes, global physical-key
limits, clearing the hint for tenant requests and byte-identical wire encoding. Four additional
TLS cases cover an empty close, a truncated header, a fatal alert and malformed TLS bytes.
Existing certificate/hostname rejection cases now also assert that they cannot trigger fallback.

The new paging benchmark first reproduced the reported cost, then compared the fix with the
previous fallback on the same live store. With 10,000 hidden tenant records and ten shared
records, a 50-item page improved from 555.306 ms to 1.089 ms; the patched fallback control took
545.399 ms. Exact payloads, tenant isolation and raw physical visibility were checked in every
run. See [the complete method, limits and results](policy-paging-review-20261028.md). This does
not measure SQL performance or the separate capacity-scan cost on writes.

The TLS correction identifies an observed empty close before any server response during TLS
setup as unavailability. Explicit authentication failures and malformed or partial responses
retain their classification. It does not relax cache safety, identity, policy or durable-write
requirements. The existing raw mutual-TLS reset ambiguity is documented in the
[security guide](security.md#authentication-authorization-and-data-boundaries).

Local logs, TRX files and source hashes are under `.tmp/policy-review-final-20261028/`.
At this stage the latest paging/TLS changes had only been run on Windows. Linux validation
was subsequently completed as recorded below. Real mobile devices and physical power loss
remain outside these checks. Publication remains held for review; no commit, push or release
tag was created during that validation follow-up.

## Linux validation of the final working-tree snapshot

The unchanged Windows-tested source passed the full public suite under Ubuntu 24.04.5,
.NET SDK 10.0.401 and runtime 10.0.12: **953 passed, zero failed, two Windows-specific skips**.

| Suite | Passed | Skipped |
|---|---:|---:|
| Core | 401 | 2 |
| SegmentCore | 218 | 0 |
| Transport | 242 | 0 |
| Authentication | 61 | 0 |
| Analyzers | 31 | 0 |

Tests ran as UID 1654, not root, on the container's native Linux overlay filesystem rather than
a Windows-mounted working directory. All eight enter-only-folder cases passed. All six paging
hint regressions and four TLS-response controls passed, including explicit-denial controls
elsewhere in the suite. The two skips are the Windows case-only rename and failed-physical-delete
tests; no Linux permission case was skipped.

The full solution build, integration harness, benchmark object-materialization self-test and
27-project clean source boundary check also passed. No runtime or test change was needed for
Linux. The initial self-test invocation lacked its executable because `dotnet test` had not
built that non-test runner; it passed after an explicit full solution build. The initial test
build retained the eight existing MSTEST0032 warnings already recorded above; the subsequent
incremental solution build reported zero warnings and errors.

The local container used four CPU cores and a 6 GiB memory limit on Docker Desktop/WSL2,
kernel 6.18.54.1-microsoft-standard-WSL2. Its preinstalled SDK image digest was
`sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`.
Source hashes matched the Windows validation snapshot and remained unchanged inside Linux.
This is local virtualized Linux correctness evidence, not remote release CI, a new performance
comparison, real-device testing or physical power-loss qualification.

Reproduction uses the normal output layout so host-process tests can locate the executable:

```sh
dotnet test InterfaceDB.sln -c Release -warnaserror:CS9057 --logger trx --results-directory /work/results/public
dotnet build InterfaceDB.sln -c Release -warnaserror:CS9057
dotnet run -c Release --no-build --no-restore --project InterfaceDb.IntegrationTests
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Transactions -- --materialization-self-test
pwsh -NoProfile -File ./Scripts/Test-PublicSourceBoundary.ps1
```

Environment details, source hashes, logs and TRX files are retained locally under
`.tmp/policy-linux-20261028/`. Commercial validation is recorded only in the private repository.
The owner subsequently authorized publication. Successful Linux validation alone did not
automatically publish packages; the release workflow and clean-feed checks still apply.
