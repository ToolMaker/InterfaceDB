[CmdletBinding()]
param(
    [switch]$Quick,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$phaseRepoRoot = Split-Path -Parent $PSScriptRoot
$phaseStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$phasePlatform = if ($IsLinux) { 'linux-native' } elseif ($IsWindows) { 'windows' } else { 'unsupported' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $phaseRepoRoot "artifacts/phase-zero/$phasePlatform-$phaseStamp.json"
}

Push-Location $phaseRepoRoot
try {
    dotnet test './InterfaceDB.Storage.SegmentCore.Tests/InterfaceDB.Storage.SegmentCore.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 0 deterministic tests failed with exit code $LASTEXITCODE."
    }

    $phaseArguments = @(
        'run', '-c', 'Release',
        '--project', './StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj',
        '--', '--phase-zero',
        '--output', $OutputPath
    )
    if ($Quick) {
        $phaseArguments += '--quick'
    }

    dotnet @phaseArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 0 admission gate did not pass. Inspect '$OutputPath' for the measured no-go evidence."
    }
}
finally {
    Pop-Location
}
