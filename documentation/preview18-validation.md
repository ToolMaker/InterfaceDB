# Preview.18 validation and publication

NuGet accepted all ten `1.0.0-preview.18` packages at 20:29 UTC on 4 November 2026.
All ten were subsequently downloaded from the public feed and verified against commit
`5ef425bc0d43c1d133777a6a7cdd37c6ed699717`. The runtime tag is
[`v1.0.0-preview.18`](https://github.com/ToolMaker/InterfaceDB/tree/v1.0.0-preview.18).

| Check | Result |
| --- | --- |
| Windows CI, 11 suites | 1,743 passed, 10 platform-specific skips |
| Linux CI, 11 suites | 1,751 passed, 2 platform-specific skips |
| Linux release workflow, 11 suites | 1,751 passed, 2 platform-specific skips |
| Candidate package consumers, 8 suites | 505 passed, 1 Windows directory-link skip |
| NuGet.org package consumers, 8 suites | 505 passed, 1 Windows directory-link skip |
| Signed package contents and provenance | All ten passed signature, source-commit, license, and asset checks |
| Dependency audit | No advisories reported by the configured sources |

[Windows and Linux CI](https://github.com/ToolMaker/InterfaceDB/actions/runs/41902748795)
and the [publishing workflow](https://github.com/ToolMaker/InterfaceDB/actions/runs/41906134325)
contain the hosted results. Integration, benchmark materialization and repository smoke
checks passed. The package consumers used a fresh cache with exact versions and no runtime
project references. The two TCP consumer suites used targeted filters for contract discovery,
serializer behavior, host-process integration, managed setup reconnect and migration recovery.
The hosted suites ran without those filters.

## Release fixes and scope

The interrupted migration test now waits for the injected rebind failure, which can arrive
after the old subscription reports a manifest mismatch. A separate reconnect regression test
reproduced a host changing the read policy between setup discovery and subscription. The
client now checks the signed setup once after subscribing. Both the new test and the original
reconnect test passed the final gates.

See the [release notes](preview18-release-notes.md) for opt-in TCP contract discovery,
the optional Protobuf serializer and storage migration boundaries. The qualification gates
in the [roadmap](roadmaps.md#final-preview-candidate) still apply for stable 1.0.

## Public package hashes

SHA-256 hashes cover the signed packages retrieved from NuGet.org. The Linux publish
build produced different DLL hashes from the local Windows candidate. Verification used
the package signature, the source commit and tests against the published binaries.

| Package | SHA-256 |
| --- | --- |
| `InterfaceDB` | `638CE1957510B022F90A7954E0FDD66D25CF0BC642350DACE3FD7FF05A7AAE89` |
| `InterfaceDB.Storage.SegmentCore` | `9407F029956927B8801498FF5A960C9F05BAD521DE26F1190BEDDEE2ECE4CA2B` |
| `InterfaceDB.Authentication` | `2803060DBF14667894DD982EF06DE230E0CEC79C8047377F9701E67FAEB81CB0` |
| `InterfaceDB.Transport` | `4BBBEF4FDE7AB118305B110779BD0FDC93D6BB52D281D7951B5763255E48DD0D` |
| `InterfaceDB.Repositories` | `811DC60EFC95CB7A29F9BA03394108D80F5D49893D731B3A48D3027988DD66EE` |
| `InterfaceDB.ConflictResolution` | `74A81BD2070CDBB4AA8691F8CA46ADC9FC76F10DE6B126713DCE3CE82520DEB1` |
| `InterfaceDB.Transport.Repositories` | `03A3B3CE6A97D56FE9660B8A4EEAE2930AD3C8ABAD7477EDBCA83E0BDE9ECD5C` |
| `InterfaceDB.Transport.Atlas` | `A40EB1BED66FDA77083E847C7B824BBC4E7DD05590A69CDF61925C60F21BDC48` |
| `InterfaceDB.Storage.Atlas` | `B60BE626AE5FA392201A357875428267761D4B7AC2CBE5A8F74B0BD0529FE9F1` |
| `InterfaceDB.Serialization.Protobuf` | `67254A5C1B73C3FBC0FBA74F12AB9F4DE2934A540F02BB88E43AAD112A3F6426` |
