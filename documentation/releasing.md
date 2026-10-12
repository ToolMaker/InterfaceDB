# Preparing a public NuGet preview

The public release publishes exactly `InterfaceDB` and `InterfaceDB.Storage.SegmentCore` with
the same version and MIT license. Use the active public source checkout and its `global.json` SDK.
Keep private implementations, context, guides and credentials out of commits and packages.

## Prepare the version and documentation

1. Check the public NuGet feed and existing tags. A local tag or source version alone does not
   prove that publication succeeded.
2. Set the same `<Version>` in both package projects. Update their `PackageReleaseNotes`, packed
   READMEs, the default version in `Scripts/Test-PublicPackageBoundary.ps1`, the documentation
   index and current-status pages. Keep historical reports tied to their original versions.
3. Write upgrade guidance for the last published version, including inherited unpublished source
   changes. Call out API rebuild requirements, encrypted/store format changes and rollback.
4. Review the complete intended public diff. Preserve unrelated local changes and exclude local
   project context. The two packed READMEs must describe only the public products.

## Validate and pack

For preview.10, run from the public checkout:

```powershell
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore -warnaserror:CS9057 -p:ShouldUnsetParentConfigurationAndPlatform=false
dotnet test InterfaceDB.sln -c Release --no-build --no-restore
dotnet run -c Release --no-build --no-restore --project InterfaceDb.IntegrationTests
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Transactions -- --materialization-self-test
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Repository -- --smoke --records 200 --batch 32 --output artifacts/preview10/repository-smoke.json
dotnet pack Idb.Libraries/Idb.Libraries.csproj -c Release --no-build --no-restore -o artifacts/preview10/packages
dotnet pack InterfaceDB.Storage.SegmentCore/InterfaceDB.Storage.SegmentCore.csproj -c Release --no-build --no-restore -o artifacts/preview10/packages
pwsh -NoProfile -File Scripts/Test-PublicPackageBoundary.ps1 -PackageDirectory artifacts/preview10/packages -Version 1.0.0-preview.10
dotnet list InterfaceDB.sln package --vulnerable --include-transitive
```

Use a fresh output directory for each version; the boundary script expects exactly two packages.
Record failures, platform skips and existing warnings. Also consume the packages from a clean
temporary .NET 10 application using package references and an isolated NuGet cache; verify save,
query, notification readiness and reopen with both providers. A project-reference test alone
cannot prove packaged dependencies and assets are correct.

## Publish and verify

After the intended source/docs changes are committed and reviewed, the release tag must exactly
match `v` plus both project versions (for example `v1.0.0-preview.10`). Pushing that tag triggers
`.github/workflows/release.yml` and therefore publication; perform it only as an approved release.
The workflow checks versions, builds/tests on Linux, runs its integration/smoke gates, packs only
the two projects, checks package boundaries, and uses NuGet OIDC credentials for publishing.

Observe the actual workflow result, then verify both versions and dependencies on NuGet and
consume from the public feed. Update release status only after those checks pass. Local validation
does not replace the release workflow or prove that packages were published.
