# Preview.17 validation and publication

Status: **published and verified on 4 November 2026**. NuGet accepted all nine uploads
at 09:08 UTC; subsequent public-feed download, signature and consumer checks completed.

Release tag: `v1.0.0-preview.17`. Runtime/test commit:
`e053895cc3b04a8d16d49ef53ddcdc03f9611220`. Later documentation commits do not move that tag or replace its packages.

## Release checks

| Check | Result |
| --- | --- |
| Final Windows CI, ten suites | 1,690 passed, 10 platform-specific skips |
| Final Linux CI, ten suites | 1,698 passed, 2 platform-specific skips |
| Linux release workflow, ten suites | 1,698 passed, 2 platform-specific skips |
| Isolated candidate package consumers, seven suites | 1,006 passed, 1 Windows directory-link skip |
| Fresh NuGet.org consumers, seven suites | 1,006 passed, 1 Windows directory-link skip |
| Package metadata/content | All nine: MIT metadata, standard LICENSE, intended assembly and allowed dependencies/assets |
| NuGet signatures and source commit | All nine verified against the release commit |
| Transitive dependency audit | No known vulnerabilities reported by the configured feed |

[Final Windows/Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/41802923826)
and [successful publishing workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/41804433216)
provide the hosted logs. The release also passed the integration harness, complete-object
materialization checks and repository/review benchmark correctness smoke gates. These
smoke checks are not new performance benchmarks.

The first candidate consumers used `6aa07484e6c04184bf9e7b1545a358e0c32102bf`.
The final commit differs only in a test's bounded Busy handling and these validation notes;
runtime and package inputs were compared and unchanged. The subsequent NuGet.org consumers
verified the actual final packages with a fresh cache and no runtime project references.

## Initial failures retained

- The initial local Windows run had one File/LocalFirst notification-feedback timeout.
  All seven focused cases subsequently passed; the entire composition suite then passed
  321/321 unchanged. Both hosted platforms, release and package consumers subsequently
  passed. The underlying cause is not established; this is an intermittent observation
  to investigate if it recurs, not a claimed runtime fix.
- Initial Linux CI encountered supported `Busy` admission while two startup paths
  requested the same subject's setup lease. The isolation test now retries only `Busy`
  within its existing deadline. Authorization errors still propagate. Production locks,
  retry policy, connection limits and isolation were not weakened.

Existing test warnings include assertions against constant capabilities and deliberate
coverage of the obsolete revision-generator API. No CS9057 analyzer-load warning was accepted.

## Scope and remaining qualification

Every active formerly commercial implementation is now in this repository under MIT.
Historical private handoffs, application data, local context and experiments were not
published. Assembly/namespace identities containing `Commercial` remain for compatibility.

Live Keycloak/Docker acceptance was compiled but not run in these gates. Physical power
loss, intended Android/iOS hardware and representative application soak remain qualification
work. No production certification or new performance claim accompanies this release.
gRPC, adaptive Atlas tuning and optional connection pooling remain roadmap work.

Sponsorship is voluntary and separate from the unchanged standard MIT license.
The maintainer still needs to complete GitHub Sponsors enrollment; no live payment page
has been advertised. See [support](../SPONSORING.md) and [setup instructions](sponsorship-setup.md).

## Published package SHA-256

Hashes cover the signed NuGet.org package files, not local unsigned candidates.

| Package | SHA-256 |
| --- | --- |
| `InterfaceDB` | `C5A0F24A4FC49995127406E039D0A5583EEDB8AFF3E6273C49479BA642E119C5` |
| `InterfaceDB.Storage.SegmentCore` | `3B407D0D10BC14A5836E38036D6CC1BC7EDC20955E2C5482B3CB2B8AE282EBD9` |
| `InterfaceDB.Authentication` | `5565BAE2C550B2E72ACCD5BE55B6BB9685AEF0511C5490028378F221974C254C` |
| `InterfaceDB.Transport` | `B97E806852D230249D3BF0D631A3BBCE4443AC9C5AEABC91DA31602501468707` |
| `InterfaceDB.Repositories` | `3447CFB23061A8090D42AE75B8E559E69438DE7AB813A805B4BB4B54AC23F0D2` |
| `InterfaceDB.ConflictResolution` | `E6F6606ACD3FE3043AC9869875E150E40AA08A12DEA6F31290FEB0C9B4E58ECD` |
| `InterfaceDB.Transport.Repositories` | `026614F9E244BAF0E4781F3029726AC31BA5E079D2ED1ED2DE16D20C5B2ED00F` |
| `InterfaceDB.Transport.Atlas` | `3CD0CA496416882001D165563EA51F62D6DA0150402C03BD8988909702FD1E3E` |
| `InterfaceDB.Storage.Atlas` | `FEFEED46E499A3DE502FE5F9E2E85AA56C9170ACA4012CE19F85EFA579020E52` |

Local verification artifacts are retained under the bounded task directory
`C:\Temp\InterfaceDBOpenSourcePreview17`; they are not package contents or required build inputs.
