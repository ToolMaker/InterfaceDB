# Preview.15 candidate validation — 31 October 2026

Status: **Windows source verified; immutable CI and current package validation pending**.
Preview.14 remains the published NuGet baseline. Server-owned remote revisions and staged
context-bound field encryption are now implemented. Follow the coordinated upgrade instructions
in [release notes](preview15-release-notes.md); the older local packages below do not contain them.

## 31 October source verification

Current Windows Release results: core 415 passed / 10 platform skips; SegmentCore 222 / 2;
transport 287 / 0; authentication 84 / 0. The final core suite includes the requirement that
custom encrypted-field lifecycles receive an explicit repository context. Six focused TCP
field/clock cases also passed after that final change; these are included in the transport count,
not additional suite coverage.
The independent source review identified a conflict-validator materialization edge case. A new
regression reproduced it before the ordering correction and passes in the final core suite.
Scoped clock mutation rejection, explicit far-future import/restart, detached encrypted edits,
legacy field conversion and strict reopen have focused regression coverage.

Linux validation remains blocked: `wsl.exe -d Ubuntu -- sh -c 'uname -s; dotnet --version'`
failed before starting Linux with `Wsl/Service/CreateInstance/CreateVm/HCS/ERROR_NO_SYSTEM_RESOURCES`.
The release will use the existing GitHub-hosted Linux correctness job for the immutable public
source snapshot. Local WSL failure does not count as a Linux test result.
No Linux, Android, physical power-cut or live Keycloak qualification is claimed for this change.
No new performance comparison has been run. The TCP reservation adds one request/response per
write batch; its clock is committed atomically with the records, without a second durable flush.
Public source boundary validation passed for 29 projects. Final immutable CI and fresh package
consumer verification are still release steps.

## Historical 30 October verification (before the final protections)

### Source verification

The source fixes passed the following Windows Release suites before the version-only packaging
change. This table counts each suite once, not focused reruns as additional coverage.

| Suite | Passed | Platform-specific skips |
| --- | ---: | ---: |
| Core | 403 | 10 |
| SegmentCore | 220 | 2 |
| Authentication | 84 | 0 |
| Transport | 281 | 0 |
| Analyzers | 31 | 0 |

Transport validation used two runs: 271 cases passed with isolated build outputs; ten host-process
cases require the normal source/executable directory layout and passed from an isolated source copy.
Their first attempt failed before starting the host because the harness could not find the solution.
The final core rerun includes the encrypted conflict-candidate staging correction.

Linux validation has not run for this candidate: WSL failed to start twice with
`Wsl/Service/CreateInstance/CreateVm/HCS/ERROR_NO_SYSTEM_RESOURCES`. Docker was unavailable,
so live Keycloak and Docker integration tests were not rerun. Existing deterministic authentication
tests do not substitute for a new live-provider or device qualification.

### Package preparation (superseded binaries)

All four public `1.0.0-preview.15` packages built successfully. The source boundary verified 29
projects, and package checks verified MIT licensing, the expected assembly and allowed dependencies
in each package. The Release solution build passed with eight existing MSTEST0032 test warnings.

A fresh package/HTTP cache restored the four packages from the local candidate feed, with no
production project references. **355/355 package-consumer tests passed: 271 transport and 84
authentication, with no skips.** The ten source-layout host-process tests are covered above.
Package origins and hashes were checked against the candidate feed.

The integration harness, materialization contract, six provider/cache smoke combinations,
three SegmentCore storage-review smoke cases and Windows quick production pilot passed.
The pilot includes atomic changes, checkpointing, compaction, reopen, backup and restore.
These are correctness checks; their timings are not a new performance comparison.

Candidate artifacts are under the ignored `.tmp/preview15-release/packages/`; consumer logs,
TRX results and origin records are under `.tmp/preview15-consumer/runs/candidate-20261030-184651/`.
Their repository metadata deliberately says `uncommitted-preview15-candidate`.
They must be rebuilt from the eventual immutable release commit before publication.

| Candidate package | SHA-256 |
| --- | --- |
| InterfaceDB | `ad68d98e5a8ccf8d2f3a3bb1e3c2addd232c11667875b1f988f52fdf05f5e1d7` |
| InterfaceDB.Authentication | `5eef5533e585233a7ed934e89798f4239fc24fef9330b2ed3cc82e5717539a3e` |
| InterfaceDB.Storage.SegmentCore | `04c2cea3380aa408bcd23ed59b4fdf567a41a0d8b0a433bada39645ab19be74a` |
| InterfaceDB.Transport | `971dd9a249c7b11e2c95d5c0fe2e9df1093d877343109e6535b19ff098103cdc` |

Publication awaits completed cross-platform validation, an immutable release snapshot and rebuilt
packages. The 30 October package-consumer results do not validate the later security changes.
No commercial implementation, local context or credentials are included in these packages.
