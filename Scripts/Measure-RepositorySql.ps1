param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CurrentDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$IncludeSqlServer
)
$ErrorActionPreference = 'Stop'
$outRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outRoot) | Out-Null
$binaries = @{
    before = (Resolve-Path -LiteralPath $BaselineDirectory).Path
    current = (Resolve-Path -LiteralPath $CurrentDirectory).Path
}
$harnessName = 'StorageBenchmarks.Transactions.dll'
if ((Get-FileHash -LiteralPath (Join-Path $binaries.before $harnessName)).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $binaries.current $harnessName)).Hash) {
    throw 'Both builds must use the same benchmark harness.'
}
for ($round = 1; $round -le 3; $round++) {
    $sizes = if ($round % 2 -eq 1) { @(1000, 10000) } else { @(10000, 1000) }
    foreach ($size in $sizes) {
        $labels = if (($round + $size / 1000) % 2 -eq 0) { @('before', 'current') } else { @('current', 'before') }
        foreach ($label in $labels) {
            $name = "$label-$size-r$round"
            $output = Join-Path $outRoot "$name.json"
            if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite $output" }
            $cache = if ($size -eq 1000) { 'MetadataOnly,StrongObject,WeakObject' } else { 'MetadataOnly' }
            $arguments = @(
                (Join-Path $binaries[$label] $harnessName), '--sql-comparison',
                "--records=$size", '--iterations=50', '--mutation-limit=1000',
                "--cache=$cache", "--label=$label", "--output=$output"
            )
            if ($round % 2 -eq 0) { $arguments += '--reverse' }
            if ($IncludeSqlServer) { $arguments += '--include-sqlserver' }
            Write-Output "$(Get-Date -Format o) Starting $name"
            & dotnet @arguments > (Join-Path $outRoot "$name.log") 2>&1
            if ($LASTEXITCODE -ne 0) { throw "$name failed; inspect its log." }
            $report = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
            if (-not $report.Passed) { throw "$name returned failed correctness checks." }
            Write-Output "$(Get-Date -Format o) Passed $name ($($report.Metrics.Count) metrics)"
        }
    }
}
