[CmdletBinding()]
param(
    [switch]$Quick,
    [ValidateRange(1, 10000000)]
    [int]$Records = 2000,
    [ValidateRange(1, 10485760)]
    [int]$PayloadBytes = 512,
    [ValidateRange(1, 1024)]
    [int]$Concurrency = 16,
    [ValidateRange(1, 1000)]
    [int]$ChurnCycles = 5,
    [ValidateRange(1, 10000000)]
    [int]$CheckpointRecords = 50000,
    [string]$Output = "artifacts\segment-core-limits\segment-core-limits-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
)

$ErrorActionPreference = 'Stop'
if ($Quick)
{
    if (-not $PSBoundParameters.ContainsKey('Records')) { $Records = 500 }
    if (-not $PSBoundParameters.ContainsKey('Concurrency')) { $Concurrency = 8 }
    if (-not $PSBoundParameters.ContainsKey('ChurnCycles')) { $ChurnCycles = 3 }
    if (-not $PSBoundParameters.ContainsKey('CheckpointRecords')) { $CheckpointRecords = 10000 }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj'
$arguments = @(
    'run',
    '--project', $project,
    '-c', 'Release',
    '--no-restore',
    '--',
    '--segment-core-limits',
    '--records', $Records,
    '--payload-bytes', $PayloadBytes,
    '--concurrency', $Concurrency,
    '--churn-cycles', $ChurnCycles,
    '--checkpoint-records', $CheckpointRecords,
    '--output', $Output
)

if ($Quick)
{
    $arguments += '--quick'
}

Push-Location $repositoryRoot
try
{
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "SegmentCore limits suite failed with exit code $LASTEXITCODE."
    }
}
finally
{
    Pop-Location
}
