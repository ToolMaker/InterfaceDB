[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CurrentDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int[]]$RecordCounts = @(2000, 10000, 50000),
    [ValidateRange(3, 100)][int]$Rounds = 3,
    [ValidateRange(20, 10000)][int]$SearchSamples = 30,
    [ValidateRange(1, 1000000)][int]$UpdateLimit = 10000
)
$ErrorActionPreference = 'Stop'
$baseline = (Resolve-Path -LiteralPath $BaselineDirectory).Path
$current = (Resolve-Path -LiteralPath $CurrentDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($directory in @($baseline, $current)) {
    if (!(Test-Path -LiteralPath (Join-Path $directory 'StorageBenchmarks.Repository.dll'))) {
        throw "Missing benchmark executable in $directory."
    }
}
if (@(Get-ChildItem -LiteralPath $output -Filter '*.json' -File).Count) {
    throw 'Use a fresh output directory so measurements are never overwritten.'
}
$previousTelemetry = $env:INTERFACEDB_BENCH_TELEMETRY
$env:INTERFACEDB_BENCH_TELEMETRY = 'disabled'
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        # Alternate both build order and working-set order to reduce systematic drift.
        $labels = if ($round % 2) { @('baseline', 'current') } else { @('current', 'baseline') }
        $sizes = @($RecordCounts)
        if (!($round % 2)) { [Array]::Reverse($sizes) }
        foreach ($count in $sizes) {
            if ($count -lt 4) { throw 'Record counts must be at least four.' }
            foreach ($label in $labels) {
                $directory = if ($label -eq 'baseline') { $baseline } else { $current }
                $name = "$label-$count-round$round"
                $report = Join-Path $output "$name.json"
                $log = Join-Path $output "$name.log"
                Write-Host "$(Get-Date -Format o) Starting $name"
                & dotnet (Join-Path $directory 'StorageBenchmarks.Repository.dll') --review-baseline --run `
                    --records $count --engines SegmentCore --cache-modes MetadataOnly,WeakObject,StrongObject `
                    --iterations $SearchSamples --warmup 5 --update-limit $UpdateLimit --payload-bytes 512 `
                    --label $name --output $report *> $log
                if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $log" }
                Write-Host "$(Get-Date -Format o) Completed $name"
            }
        }
    }
}
finally {
    $env:INTERFACEDB_BENCH_TELEMETRY = $previousTelemetry
}
