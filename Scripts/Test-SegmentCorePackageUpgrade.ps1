param(
    [string]$BaselineVersion = '1.0.0-preview.2',
    [string]$PackageSource = 'C:\Code\C#\InterfaceDB\QualificationArtifacts\packages-release-optimized',
    [string]$Output = 'artifacts\segment-core\segment-core-package-upgrade.json',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $scriptRoot
$writerTemplate = Join-Path $scriptRoot 'SegmentCoreUpgradeBaselineWriter.cs.txt'
$benchmarkProject = Join-Path $repositoryRoot 'StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj'
$resolvedPackageSource = [System.IO.Path]::GetFullPath($PackageSource)
$resolvedOutput = if ([System.IO.Path]::IsPathRooted($Output)) {
    [System.IO.Path]::GetFullPath($Output)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
}

if (-not (Test-Path -LiteralPath $writerTemplate -PathType Leaf)) {
    throw "Upgrade baseline writer template does not exist: $writerTemplate"
}

if (-not (Test-Path -LiteralPath $resolvedPackageSource -PathType Container)) {
    throw "Baseline package source does not exist: $resolvedPackageSource"
}

$expectedPackage = Join-Path $resolvedPackageSource "InterfaceDB.Storage.SegmentCore.$BaselineVersion.nupkg"
if (-not (Test-Path -LiteralPath $expectedPackage -PathType Leaf)) {
    throw "Baseline package does not exist: $expectedPackage"
}

$temporaryParent = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) 'InterfaceDB-SegmentCore-Upgrade'))
[System.IO.Directory]::CreateDirectory($temporaryParent) | Out-Null
$temporaryRoot = Join-Path $temporaryParent ([Guid]::NewGuid().ToString('N'))
$writerRoot = Join-Path $temporaryRoot 'writer'
$storeRoot = Join-Path $temporaryRoot 'store'

try {
    & dotnet new console --framework net10.0 --output $writerRoot --force --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the temporary baseline writer project (exit $LASTEXITCODE)."
    }

    Copy-Item -LiteralPath $writerTemplate -Destination (Join-Path $writerRoot 'Program.cs') -Force
    $writerProject = Join-Path $writerRoot 'writer.csproj'

    & dotnet add $writerProject package InterfaceDB.Storage.SegmentCore --version $BaselineVersion --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Could not add baseline package $BaselineVersion (exit $LASTEXITCODE)."
    }

    & dotnet restore $writerProject --source $resolvedPackageSource --source 'https://api.nuget.org/v3/index.json'
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restore the baseline writer (exit $LASTEXITCODE)."
    }

    & dotnet run --project $writerProject -c $Configuration --no-restore -- $storeRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The baseline package could not create its store (exit $LASTEXITCODE)."
    }

    & dotnet build $benchmarkProject -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Could not build the current upgrade validator (exit $LASTEXITCODE)."
    }

    $outputParent = Split-Path -Parent $resolvedOutput
    [System.IO.Directory]::CreateDirectory($outputParent) | Out-Null
    & dotnet run --project $benchmarkProject -c $Configuration --no-build --no-restore -- `
        --segment-core-upgrade-validate `
        --root $storeRoot `
        --baseline-version $BaselineVersion `
        --output $resolvedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "The current source failed package upgrade validation (exit $LASTEXITCODE)."
    }

    Write-Host "SegmentCore package upgrade qualification passed: $resolvedOutput"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $requiredPrefix = $temporaryParent.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTemporaryRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected qualification path: $resolvedTemporaryRoot"
        }

        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
