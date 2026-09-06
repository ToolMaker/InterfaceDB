# InterfaceDB 1.0.0-preview.8

This is a documentation-only release of the preview.7 runtime implementation. NuGet embeds its displayed README in each immutable published package, so expanded package-page documentation requires a new version.

## What changed

- The InterfaceDB NuGet page now explains all preview.7 additions: revision-checked saves/deletes, conditional metadata/imports, local repository notifications, retained editing, custom strategy contracts, maintenance policies/budgets/summaries, and storage fast paths.
- The page includes save/conflict, notification subscription and retained-edit examples, a default/opt-in feature table, ownership/lifetime limits, qualification and upgrade guidance.
- SegmentCore now has a dedicated package README covering its integrated capabilities, independent-file cleanup configuration, format transitions, atomic writes/batches and operational boundaries.
- Package links use full, version-pinned GitHub URLs so they work from NuGet. The detailed guides no longer incorrectly describe shipped notifications/revisions as unpublished or retained editing as unavailable.

No runtime `.cs` implementation or storage format changed from preview.7. Package versions, README selection and release metadata changed to publish this documentation. The [preview.7 feature and upgrade notes](preview7-release-notes.md) remain the implementation reference.

## Compatibility

Updating from preview.7 adds no format transition. Both versions write IDB6 and read IDB2–IDB6. Upgrades from preview.6 or earlier still require stopping old writers, rebuilding custom integrations and retaining a verified pre-upgrade backup. Older readers cannot read IDB6. Optional independent SegmentCore compaction still performs the separate engine-format 1.1 opt-in upgrade; changing the option back does not downgrade the store.

Notifications remain local/non-durable, edit leases remain process/session scoped, and SegmentCore retains one owning process per live root and one-contract-store transactions. Physical power-cut and Apple-device runtime qualification are not added by a documentation release.

## Install

```powershell
dotnet add package InterfaceDB --version 1.0.0-preview.8
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.8
```

The tag-triggered release workflow repeats build, correctness, integration, materialization, smoke and package-boundary checks before publishing the two MIT packages.
