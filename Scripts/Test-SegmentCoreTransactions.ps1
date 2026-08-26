[CmdletBinding()]
param(
    [switch]$Quick,
    [ValidateRange(1, 10000000)]
    [int]$Records = 5000,
    [ValidateRange(1, 10000000)]
    [int]$IndependentRecords = 1000,
    [ValidateRange(1, 100)]
    [int]$Iterations = 3,
    [ValidateSet(1, 2, 4, 8, 16, 32, 64)]
    [int]$Stripes = 8,
    [ValidateSet('both', 'participant', 'wal')]
    [string]$Protocols = 'both',
    [ValidateSet('explicit', 'write-through')]
    [string]$Durability = 'explicit',
    [int[]]$BatchSizes = @(1, 10, 100, 1000),
    [string]$Output = "artifacts\segment-core-transactions\segment-core-transactions-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
)

$ErrorActionPreference = 'Stop'
if ($Quick)
{
    if (-not $PSBoundParameters.ContainsKey('Records')) { $Records = 1000 }
    if (-not $PSBoundParameters.ContainsKey('IndependentRecords')) { $IndependentRecords = 250 }
    if (-not $PSBoundParameters.ContainsKey('Iterations')) { $Iterations = 2 }
    if (-not $PSBoundParameters.ContainsKey('BatchSizes')) { $BatchSizes = @(1, 10, 100) }
}

if ($BatchSizes.Count -eq 0 -or $BatchSizes.Where({ $_ -le 0 }).Count -ne 0)
{
    throw 'BatchSizes must contain one or more positive integers.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj'
$arguments = @(
    'run',
    '--project', $project,
    '-c', 'Release',
    '--no-restore',
    '--',
    '--segment-core-transactions',
    '--records', $Records,
    '--independent-records', $IndependentRecords,
    '--iterations', $Iterations,
    '--batch-sizes', ($BatchSizes -join ','),
    '--stripes', $Stripes,
    '--protocols', $Protocols,
    '--durability', $Durability,
    '--output', $Output
)

Push-Location $repositoryRoot
try
{
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "SegmentCore transaction suite failed with exit code $LASTEXITCODE."
    }
}
finally
{
    Pop-Location
}
