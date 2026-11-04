param(
    [string] $PackageDirectory = 'artifacts/packages',
    [string] $Version = '1.0.0-preview.17'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @{}
foreach ($project in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageProjects.json') -Raw | ConvertFrom-Json)) {
    $expected[$project.id] = $project.assembly
}
$allowedDependencies = @($expected.Keys) + @('MessagePack', 'Microsoft.Extensions.DependencyInjection', 'Microsoft.Extensions.DependencyInjection.Abstractions', 'Microsoft.Extensions.Logging.Abstractions', 'Microsoft.IdentityModel.JsonWebTokens', 'RadixForge', 'System.IO.Hashing')
$licenseText = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Raw).Replace("`r`n", "`n").Trim()
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -ne $expected.Count) { throw "Expected exactly $($expected.Count) public packages, found $($packages.Count)." }
$seen = @{}
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $specs = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
        if ($specs.Count -ne 1) { throw "Expected one nuspec in $($package.Name)." }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.package.metadata
        $id = [string] $metadata.id
        if (!$expected.ContainsKey($id) -or $seen.ContainsKey($id)) { throw "Unexpected or duplicate package: $id" }
        $seen[$id] = $true
        if ([string] $metadata.version -ne $Version) { throw "Unexpected version for $id." }
        if ([string] $metadata.license.type -ne 'expression' -or [string] $metadata.license.'#text' -ne 'MIT') {
            throw "Expected an explicit MIT license for $id."
        }
        $libraries = @($archive.Entries | Where-Object { $_.FullName -like '*.dll' })
        if ($libraries.Count -ne 1 -or $libraries[0].FullName -ne "lib/net10.0/$($expected[$id])") {
            throw "Unexpected packaged assembly inventory in $id."
        }
        foreach ($dependency in $metadata.dependencies.group.dependency) {
            if ([string] $dependency.id -notin $allowedDependencies) { throw "Unexpected dependency in ${id}: $($dependency.id)" }
            if ($expected.ContainsKey([string] $dependency.id) -and [string] $dependency.version -notin @($Version, "[$Version, )", "[$Version]")) {
                throw "Uncoordinated InterfaceDB dependency in ${id}: $($dependency.id) $($dependency.version)"
            }
        }
        $readme = @($archive.Entries | Where-Object FullName -eq 'README.md')
        if ($readme.Count -ne 1) { throw "Expected one public README in $id." }
        $reader = [IO.StreamReader]::new($readme[0].Open())
        try { $readmeText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($readmeText -match '(?i)\.codex-local|C:[/\\](Users|Code)|AVIATOR_FILE_TO_SEGMENT_HANDOFF') {
            throw "Local-only content found in the public README for $id."
        }
        $license = @($archive.Entries | Where-Object FullName -eq 'LICENSE')
        if ($license.Count -ne 1) { throw "Expected the standard MIT LICENSE in $id." }
        $reader = [IO.StreamReader]::new($license[0].Open())
        try { $packedLicense = $reader.ReadToEnd().Replace("`r`n", "`n").Trim() } finally { $reader.Dispose() }
        if ($packedLicense -ne $licenseText) { throw "License text differs in $id." }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch '^(lib/net10\.0/[^/]+\.dll|README\.md|LICENSE|[^/]+\.nuspec|\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp|\.signature\.p7s)$') {
                throw "Unexpected package content in ${id}: $($entry.FullName)"
            }
        }
        Write-Host "Verified public package: $id $Version (MIT; one allowed assembly)."
    } finally { $archive.Dispose() }
}
