# Preview.14 release validation — 30 October 2026

Status: **all four `1.0.0-preview.14` packages are published on NuGet.org and verified by an
isolated consumer**. Runtime commit: `6b2db23154004f6193ffd3ca79a281781a05cdbf`, tagged
`v1.0.0-preview.14`. Uploads were accepted at approximately 11:05 UTC; all four were available
in the public package feed by 11:14 UTC.

## Release checks

[Windows/Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/41043984715) and the
[NuGet publication workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/41045100044)
passed for the immutable release commit. Publication used NuGet trusted publishing with a
temporary credential. All four uploads succeeded; no duplicate-package skip was used.

| Suite | Windows CI passed | Linux tag workflow passed |
| --- | ---: | ---: |
| Core | 393 | 401 |
| Analyzers | 31 | 31 |
| SegmentCore | 218 | 218 |
| Transport | 276 | 276 |
| Authentication | 80 | 80 |
| **Total passed** | **998** | **1,006** |
| Platform-specific skips | 10 | 2 |

The integration harness, object-materialization contract, Windows production pilot,
repository smoke matrix and storage-review smoke checks also passed. These are correctness
checks, not new throughput measurements. Full builds retained eight existing MSTEST0032
warnings in SegmentCore compatibility tests, with no errors or analyzer compatibility failures.
The clean source boundary and all package boundaries passed.

## Published package consumer

A fresh package cache and HTTP cache restored only from the public NuGet feed. The consumer
uses no production project references. All four recorded package origins were NuGet.org,
and every nuspec referenced the release commit above.

**346/346 tests passed: 266 transport and 80 authentication, with no skips or failures.**
Ten host-process cases require the source executable layout and remain covered by the full
source suites above. The local candidate consumer also passed the same 346 cases against
`fc2fbc6`; the final commit changed only README installation references and validation wording.
These are repeated checks of the same cases, not 692 distinct tests.

The downloaded packages passed MIT, assembly and dependency boundary checks and
`dotnet nuget verify --all` repository-signature verification. Hashes from the isolated
consumer exactly match the separately downloaded, inspected published packages.

| Package, version `1.0.0-preview.14` | Published nupkg SHA-256 |
| --- | --- |
| [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.14) | `2fae4b0c3dae663ca26bb3210d4501318c1cacd71b1f85833bc7edcf1b4a0554` |
| [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.14) | `844145924db714fdb2abf578bf9c44f31058c2d0ea2685fce6603fdc53eb1727` |
| [InterfaceDB.Authentication](https://www.nuget.org/packages/InterfaceDB.Authentication/1.0.0-preview.14) | `9593b28de99f53815a7c423dede3f36b0369548428cc62fb89c3b73626823e58` |
| [InterfaceDB.Transport](https://www.nuget.org/packages/InterfaceDB.Transport/1.0.0-preview.14) | `380df9aba9f869aa33f20b1a0ef3f733f74f146d79ef1f39a7e004a154f7eeee` |

Receipts and downloaded artifacts are retained locally under `.tmp/preview14-release/`;
consumer logs, TRX files and package origins are under `.tmp/preview14-consumer/runs/`.
Local context, private implementations, credentials and build experiments are excluded from
public source commits and packages. The release tag stays on the tested runtime commit;
this publication receipt does not change or republish its binaries.

Update all four public packages and rebuild clients/hosts using the
[release notes](preview14-release-notes.md). Bridges need their own liveness integration;
see [connection behavior](tcp-connection-reliability.md). The preview does not claim new
real-phone, live-Keycloak or physical power-cut qualification. Actual device testing follows
deployment through the consuming application's release process.
