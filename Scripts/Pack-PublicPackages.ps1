param([string] $OutputDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($package in (Get-Content (Join-Path $PSScriptRoot 'PackageProjects.json') -Raw | ConvertFrom-Json)) {
    & dotnet pack (Join-Path $root $package.project) -c Release --no-build --no-restore --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $($package.id)" }
}
