# Preparing an InterfaceDB NuGet preview

All implemented modules are MIT licensed. `Scripts/PackageProjects.json` is the explicit
runtime package inventory; `Scripts/TestProjects.json` lists the standard suites.
Use the repository's `global.json` SDK. Local context, credentials, customer data and
private historical handoffs do not belong in source commits or packages.

## Prepare

1. Check the NuGet feed and Git tags; never reuse a published package version.
2. Set the same Version in every package project and update release notes, package
   READMEs, the documentation index and the package-boundary script's default version.
3. Preserve assembly and serialized type identities unless an explicit compatibility
   migration is included. Package IDs need not match assembly names.
4. Review staged source, new files, third-party notices and package contents. Keep MIT
   standard; sponsorship requests remain voluntary and separate.

## Validate and pack

Run from the checkout. On Windows, put process TEMP/TMP and generated test data in a
task-owned directory beneath `C:\Temp`; do not change machine-wide settings.

```powershell
./Scripts/Test-PublicSourceBoundary.ps1
./Scripts/Test-ReleaseVersion.ps1 -Tag v1.0.0-preview.17
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore -warnaserror:CS9057
./Scripts/Test-AllSuites.ps1
dotnet run -c Release --no-build --no-restore --project InterfaceDb.IntegrationTests
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Transactions -- --materialization-self-test
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Repository -- --smoke --records 200 --batch 32 --output artifacts/repository-smoke.json
./Scripts/Pack-PublicPackages.ps1 -OutputDirectory artifacts/packages
./Scripts/Test-PublicPackageBoundary.ps1 -Version 1.0.0-preview.17
dotnet list InterfaceDB.sln package --vulnerable --include-transitive
```

Use a fresh output directory. Package checks require exactly the inventory's nine
packages, coordinated versions, MIT metadata, the standard license, one expected
assembly per package and allowed dependencies/assets. Historical assembly names
containing `Commercial` are compatibility identities, not a licensing boundary.

Consume the candidate packages from an isolated cache without runtime source project
references. Test storage/reopen, migration, conflict resolution, composition and transport.
Run optional Docker/Keycloak acceptance separately when its environment is available.
Record skipped tests, warnings and qualifications accurately.

## Publish and verify

Commit the complete snapshot and push for Windows/Linux CI. Once both pass, push the
authorized `v<version>` tag. The release workflow checks versions, builds, runs the standard
suites and the integration/smoke gates, packs the manifest and publishes with NuGet's
short-lived trusted-publishing credential.

Observe the workflow result, then verify every package version on NuGet, download and
check signatures and source-commit metadata, and rerun clean package consumers. Only then
mark publication verified. Record the runtime tag and workflow links; a later documentation
receipt commit does not move the runtime tag.
