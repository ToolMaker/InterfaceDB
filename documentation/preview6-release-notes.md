# InterfaceDB 1.0.0-preview.6

This preview releases the MIT InterfaceDB package (including file storage) and the separate MIT InterfaceDB.Storage.SegmentCore package. No additional provider implementation, commercial repository or synchronization product is included.

## Changes

- Bounded object materialization, keyed batch reads, ordered duplicate/missing-key handling and filtered file prefetch.
- Reused file bulk writer leases, cached encrypted-field metadata and changed-field-only index maintenance.
- Safe reusable predicate shapes and weak expression-syntax caching; captured values remain live and query results are not cached.
- Lower-allocation SegmentCore write, WAL and checkpoint paths, owned-buffer boundaries, retained checkpoint validation and guarded ARM CRC support.
- Optional opaque envelope metadata, bounded at 64 KiB, validated detached metadata-aware saves, exact identity checks and explicit commit-durability reporting.

See [measured results](performance-preview6-20260905.md) for both gains and regressions. Performance work did not weaken the durability contract or change the one-store transaction/one-writer-process boundary.

## Upgrade warning

New writes use IDB5. Readers accept IDB2, IDB3, IDB4 and IDB5; preview.5 cannot read IDB5 payloads. Back up before upgrading if rollback is required. Restoring a pre-upgrade backup is required to return newly written stores to preview.5.

Metadata-aware writes use IEnvelopeMetadataRepository and validated detached candidates. Internal staging is not a public envelope-lifecycle bypass. Failed candidates consumed by staging must be rebuilt for retry. Exact-key validation is not compare-and-swap; follow the documented single-logical-writer rules.

Opaque extension metadata is not interpreted by the database and is not protected by field-only encryption. Use whole-envelope encryption when it is sensitive.

## Qualification and remaining limits

The public-only checkout built Release with warnings treated as errors. Windows qualification passed 276 unit tests (four platform-specific skips), all 50 integration scenarios, three object-materialization controls and 56 repository smoke cases. A clean-cache package-only consumer also passed all 56 cases, and a raw SegmentCore store created with the published preview.5 package passed current-source upgrade validation. Release CI repeats the core, SegmentCore, analyzer, integration and smoke gates on Linux before publishing.

Managed .NET 10 consumption by Android, iOS, Mac Catalyst and macOS remains supported within the existing documented platform restrictions. This pass does not add new physical Android/Apple runtime evidence or claim mobile performance gains. Apple runtime qualification and actual acknowledged-write physical power-cut evidence remain outstanding; the release remains a preview.

## Install

```powershell
dotnet add package InterfaceDB --version 1.0.0-preview.6
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.6
```

The optional SegmentCore package references InterfaceDB at the same version. NuGet availability is established by the successful tag-triggered release workflow, not by this document alone.
