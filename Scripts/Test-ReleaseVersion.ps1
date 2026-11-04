param([string] $Tag = $env:GITHUB_REF_NAME)
$ErrorActionPreference = 'Stop'
foreach ($package in (Get-Content (Join-Path $PSScriptRoot 'PackageProjects.json') -Raw | ConvertFrom-Json)) {
    [xml] $xml = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) $package.project)
    $version = [string] $xml.Project.PropertyGroup.Version
    if ($Tag -ne "v$version") { throw "Release tag $Tag does not match $($package.id) version $version." }
    if ([string] $xml.Project.PropertyGroup.PackageId -ne $package.id) { throw "Package ID mismatch: $($package.project)" }
}
