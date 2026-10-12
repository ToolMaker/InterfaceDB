# InterfaceDB 1.0.0-preview.10

Prepared 12 October 2026 for the two public MIT packages, `InterfaceDB` and
`InterfaceDB.Storage.SegmentCore`. Preparation and local package validation do not publish to NuGet.
The public NuGet feed checked on this date ends at preview.8; the source also contains the preview.9
security changes. This release includes those changes. Keep both packages on preview.10 together.

## Changes since preview.9 source

| Change | How to use it |
| --- | --- |
| Subscription readiness | Await each `IRepositorySubscription.Ready` after registration and before the initial query. Every new subscriber waits for writes admitted before its registration, including when another subscriber is already present. |
| Running contract version | Read `IRepository<T>.RunningVersion` to inspect the provider's selected contract baseline. This is distinct from the NuGet version. Custom repositories that have not implemented version reporting return `null`. |
| Provider revision clocks | Custom providers can supply `IRevisionTimestampGenerator` to `EnvelopeWriteCoordinator<T>`. Existing file-root constructors remain available. See the [revision guide](envelope-revisions.md#provider-owned-revision-clocks). |
| Provider marker | `ContractRepositoryType.Remote` identifies an externally implemented provider. This enum value does not install or configure a transport. |
| SegmentCore migrations | Encrypted fields are restored before the first migration's `Up()`. Canonical source versions are checked before conversion. Later migration steps retain their own changed/null values. Revisions and supported extension metadata survive migration. |
| Dependencies and build tools | MessagePack 3.1.11, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12 and System.IO.Hashing 10.0.12. Repository builds select .NET SDK 10.0.401 or a compatible newer feature band; analyzer projects use Roslyn 5.9. |

`Ready` is a registration barrier, not a promise that every callback has finished. Use
`WaitForIdleAsync` outside callbacks to wait for the current subscriber mailbox to become idle. Notifications remain bounded local
observations; observe `Completion` and requery after a gap. See [notifications](repository-notifications.md).

Native migration rejects a malformed/mismatched canonical source version, unreadable encrypted
field, or ambiguous/unwritable migration property. Validation happens per record: earlier batches
can already exist in the separate target when a later record fails. Preserve the source, inspect the
result's failures and verify the complete target before cutover. See [migrations](migrations.md).

## Upgrade checklist

1. Stop old writers and retain a verified complete backup, the previous runtime and all encryption keys.
2. Update both public packages together and rebuild applications and custom providers. Custom
   `IRepositorySubscription` implementations must implement the new `Ready` property. The default
   `IRepository<T>.RunningVersion` member permits existing repository implementations to return `null`.
3. **From preview.8 or earlier with encrypted data:** follow the complete
   [preview.9 encrypted-store migration and rollback procedure](preview9-release-notes.md).
   Writes use context-bound `IDBE` v3; older readers cannot read that ciphertext. A package update
   alone does not complete the required rewrite. Use the documented temporary migration settings,
   verify every record, then reopen in strict mode.
4. **From preview.9:** preview.10 introduces no additional envelope or engine-format change.
   Plaintext remains `IDB6`, encrypted frames remain `IDBE` v3, and independent compaction retains
   its existing explicit SegmentCore engine-format 1.1 transition.
5. Inventory stored concrete types and configure an explicit frozen runtime-type allowlist when
   implementations live outside the contract assembly. Recheck encryption, notifications,
   migrations, backup/restore and revision conflicts with the application's own contracts.

For source/analyzer consumers, `global.json` selects SDK 10.0.401 with `latestFeature` roll-forward.
The analyzer requires a Roslyn 5.9-compatible compiler host. Older hosts can issue `CS9057` and
skip it; CI treats that diagnostic as an error. Analyzers run during compilation in Debug and
Release to protect immutable contract baselines; they add no database runtime execution.
The public release workflow publishes only the two database packages.

## Validation and release boundaries

Release gates are Release builds with analyzers enabled, core/SegmentCore/analyzer tests,
the standalone integration harness, materialization self-test, repository smoke, and an exact
two-package content/dependency boundary check. Local results are recorded in the [release
validation report](preview10-validation.md). The tag-triggered release workflow repeats its
gates before publishing. A prepared package is not evidence of a completed CI or NuGet release.

The implemented fixes do not establish an absence of all security risk or complete stable-1.0
qualification. Physical power-cut evidence, representative application/device soak, additional
Windows hardware, platform-specific handle-pinned filesystem hardening and adversarial filesystem
qualification remain scoped work. Apple runtime qualification also remains outstanding. See
[security](security.md) and [roadmap](roadmaps.md) for the applicable boundaries.

Start with [getting started](getting-started.md), the [package overview](../Idb.Libraries/README.md),
or [SegmentCore setup](segment-core-storage-provider.md). Historical performance and test reports
retain the versions and environments of their original runs.
