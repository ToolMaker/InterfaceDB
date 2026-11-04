# Preview.16 release validation — 4 November 2026

Status: published to NuGet.org and independently verified on 4 November 2026.

The local pre-release working tree passed core (424), SegmentCore (222), analyzers
(31), transport (287) and authentication (84), with 12 platform/privilege skips.
Four additional separate-process lease checks passed on Windows. They verify ordinary
writes and migration admission with the runtime locking switch both enabled and disabled;
the Unix-only disable switch must reject unsafe operations on Linux.

Release commit: `0bfb7898def315edb708022768e3db6177f78ada`.
Tag: `v1.0.0-preview.16`. [Windows/Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/41776741362)
passed on that commit before the tag was pushed.

| Public suite | Windows passed | Linux passed |
| --- | ---: | ---: |
| Core | 428 | 436 |
| Analyzers | 31 | Repeated by publication workflow |
| SegmentCore | 224 | 224 |
| Transport | 287 | 287 |
| Authentication | 84 | 84 |
| Total | 1,054 | 1,031 |

Windows had 10 platform-specific skips; Linux had two. All executed tests passed.
Both integration harnesses and the benchmark object-materialization checks passed.
Windows also passed the repository production pilot and package-boundary verification.
The four separate-process guard cases ran on both systems: the Unix runtime switch
rejects unsafe writes/migration; Windows sharing remains enforced with either value.

An immutable export of the release commit produced all four candidate packages.
Fresh-cache package consumers passed 277 transport and 84 authentication tests, with
no public source-project references. The ten source-layout host-process cases omitted
from that consumer are included in the full CI transport suite above.

The [publication workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/41777655430)
passed: 1,062 Linux tests and two skips, including all 31 analyzer tests. It also passed
integration, repository smoke and package-boundary validation. Trusted publishing
successfully uploaded all four packages using a temporary NuGet credential.

Local WSL cannot start (`ERROR_NO_SYSTEM_RESOURCES`); this is not counted as a Linux
pass. Device, power-loss and external identity-provider qualification remain separate.
All four packages were downloaded directly from NuGet.org. `dotnet nuget verify --all`
passed for each; their nuspec source commit matches the release commit above. Fresh-cache
consumers using only NuGet.org passed the same 277 transport and 84 authentication tests.
Restored package origins and SHA-256 hashes match the independently verified downloads.

| Package | Version | Published package SHA-256 |
| --- | --- | --- |
| [InterfaceDB](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.16) | 1.0.0-preview.16 | `CD4A5D5FE2C13C3C0145F26ACF3C15E0B6BBB288CAC0C6477B575C8F5E8AF0AB` |
| [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore/1.0.0-preview.16) | 1.0.0-preview.16 | `A60B2543F0E5C461F9C8594DD70D198CFC9C9766CB04C280E68E1239C5227A38` |
| [InterfaceDB.Authentication](https://www.nuget.org/packages/InterfaceDB.Authentication/1.0.0-preview.16) | 1.0.0-preview.16 | `00FCAECD1D0A8C3A2F9A698141021857E873C2B5D336BE2DF525DD0DFE2C63FF` |
| [InterfaceDB.Transport](https://www.nuget.org/packages/InterfaceDB.Transport/1.0.0-preview.16) | 1.0.0-preview.16 | `9C1DB47F67E1B8112F5A91D65B348926E23F4C4DAE1E16E34FA038B138F507B8` |

The release tag continues to identify the tested runtime commit. This later documentation
receipt records publication without moving that tag or changing the published binaries.
