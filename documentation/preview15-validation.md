# Preview.15 release validation — 31 October 2026

Status: **all four `1.0.0-preview.15` packages are published on NuGet.org and verified by an isolated consumer**.
Runtime commit: `1efe046b76324d774a8cc149042e7cf770a0e5c2`, tagged `v1.0.0-preview.15`.
NuGet accepted the uploads at approximately 01:04 UTC on 31 October 2026.
All four packages were available in the public restore feed by 01:14 UTC.

## Immutable release checks

[Windows/Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/41152423887) and the
[NuGet publication workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/41153177444)
passed for the same release commit. Publication used trusted publishing with a temporary
credential. All four uploads succeeded; no duplicate-package skip was used.

| Suite | Windows CI passed | Linux tag workflow passed |
| --- | ---: | ---: |
| Core | 415 | 423 |
| Analyzers | 31 | 31 |
| SegmentCore | 224 | 224 |
| Transport | 287 | 287 |
| Authentication | 84 | 84 |
| **Total passed** | **1,041** | **1,049** |
| Platform/environment skips | 10 | 2 |

The integration harness, object-materialization contract, Windows production pilot, repository
smoke matrix and storage-review smoke checks passed. These are correctness checks, not new
throughput measurements. Builds retained eight existing MSTEST0032 test warnings, with no errors
or analyzer compatibility failures. Public source boundaries passed for 29 projects; all four
packages passed MIT licensing, assembly inventory and dependency boundary checks.

Local WSL still failed to start with `HCS/ERROR_NO_SYSTEM_RESOURCES`. The successful hosted
Linux runs above resolve the public release's Linux validation gate; local WSL failure is not
counted as a test pass. No new physical-phone, live-Keycloak or power-cut qualification is claimed.

## Package consumers

A clean export of the release commit built the local candidate packages. A fresh package and HTTP
cache restored those packages without production project references. **361/361 candidate consumer
tests passed: 277 transport and 84 authentication, with no skips.** Ten source-layout host-process
cases remain covered by the full source suites above. Nuspec metadata identified the exact release
commit, and recorded package origins matched the isolated candidate feed.

The published-feed consumer used another fresh package and HTTP cache with NuGet.org as its only
source, with no production project references. **361/361 published-package tests passed: 277
transport and 84 authentication, with no failures or skips.** These repeat the candidate cases;
they are not 722 distinct tests. Every restored nuspec identifies the immutable release commit.

The four independently downloaded packages passed `dotnet nuget verify --all` repository-signature
verification and the public package-boundary checks. Their SHA-256 hashes exactly match the
packages restored by the isolated consumer.

| Package, version `1.0.0-preview.15` | Published nupkg SHA-256 |
| --- | --- |
| [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.15) | `f324e2ec722e7256d346287b3f067a3a2fbb87fa8cd97f12c11cdba643bc493e` |
| [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.15) | `e267f1185c5137b6382dcbab940977ead8864f50cb44c9b573ff2effa5e682f7` |
| [InterfaceDB.Authentication](https://www.nuget.org/packages/InterfaceDB.Authentication/1.0.0-preview.15) | `0242dcc1e7fbee035753ad8e7e7a6bb38204338a78f6d5cf63611a9ae5ff54cf` |
| [InterfaceDB.Transport](https://www.nuget.org/packages/InterfaceDB.Transport/1.0.0-preview.15) | `928674229b0e8b38291ebc6fdaa4b4c59bce75121420ac03f1cd097ed534e052` |

The earlier 30 October local preview.15 packages are superseded and must not be distributed.

Receipts, immutable source export, package downloads and consumer evidence are retained locally
under the ignored `.tmp/preview15-final/`. Local context, private implementations and credentials
are excluded from public source and packages. The release tag stays on the tested runtime commit;
this publication receipt does not change or republish its binaries.

## Upgrade

Update all four public packages and rebuild clients and hosts using the
[preview.15 release and upgrade notes](preview15-release-notes.md). Coordinate the TCP upgrade
during a write pause. Explicit remote revision imports require a host authorization policy;
existing encrypted fields require the staged reader-first conversion. Durability, isolation,
committed-copy reads and migration ownership remain preserved by the tested paths.
