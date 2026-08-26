[CmdletBinding()]
param(
    [switch]$Quick,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$phaseRepoRoot = Split-Path -Parent $PSScriptRoot
$phaseReportDirectory = Join-Path $phaseRepoRoot 'artifacts/phase-zero'
New-Item -ItemType Directory -Force -Path $phaseReportDirectory | Out-Null
$phaseStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = "linux-wsl2-docker-$phaseStamp.json"
}
elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    throw 'For the Docker gate, OutputPath must be a filename relative to artifacts/phase-zero.'
}

$phaseQuickArgument = if ($Quick) { ' --quick' } else { '' }
$phaseContainerCommand = @"
set -euo pipefail
mkdir -p /work
cp -a /src/. /work/
cd /work
dotnet test InterfaceDB.Storage.SegmentCore.Tests/InterfaceDB.Storage.SegmentCore.Tests.csproj -c Release
dotnet run -c Release --project StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj -- --phase-zero$phaseQuickArgument --output /reports/$OutputPath
"@

docker run --rm `
    --mount "type=bind,source=$phaseRepoRoot,target=/src,readonly" `
    --mount "type=bind,source=$phaseReportDirectory,target=/reports" `
    'mcr.microsoft.com/dotnet/sdk:10.0' `
    bash -lc $phaseContainerCommand

if ($LASTEXITCODE -ne 0) {
    throw "The virtualized Linux Phase 0 gate did not pass. Inspect '$(Join-Path $phaseReportDirectory $OutputPath)'."
}
