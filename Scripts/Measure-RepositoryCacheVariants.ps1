param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CacheDirectory,
    [Parameter(Mandatory)][string]$OptimizedDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$IncludeSqlServer
)
$ErrorActionPreference = 'Stop'
$outRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outRoot) | Out-Null
$binaries = @{
    baseline = (Resolve-Path -LiteralPath $BaselineDirectory).Path
    cache = (Resolve-Path -LiteralPath $CacheDirectory).Path
    cache_a1 = (Resolve-Path -LiteralPath $OptimizedDirectory).Path
}
$harnessName = 'StorageBenchmarks.Transactions.dll'
$hashes = @($binaries.Values | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $_ $harnessName)).Hash } | Select-Object -Unique)
if ($hashes.Count -ne 1) { throw 'All variants must use the identical benchmark harness.' }
$initialBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
for ($round = 1; $round -le 3; $round++) {
    $labels = switch ($round) {
        1 { @('baseline', 'cache', 'cache_a1') }
        2 { @('cache_a1', 'baseline', 'cache') }
        3 { @('cache', 'cache_a1', 'baseline') }
    }
    foreach ($workload in @('mixed-1000', 'warm-10000')) {
        foreach ($label in $labels) {
            $name = "$workload-$label-r$round"
            $output = Join-Path $outRoot "$name.json"
            if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite $output" }
            $arguments = @((Join-Path $binaries[$label] $harnessName), '--sql-comparison',
                '--cache=MetadataOnly,StrongObject,WeakObject', '--read-probes',
                "--label=$label", "--output=$output")
            if ($workload -eq 'warm-10000') {
                $arguments += @('--records=10000', '--iterations=400', '--queries-only', '--skip-file', '--retain-cache-objects')
                if ($label -ne 'baseline') { $arguments += '--only-interfacedb' }
                elseif ($IncludeSqlServer) { $arguments += '--include-sqlserver' }
            } else {
                $arguments += @('--records=1000', '--iterations=100', '--mutation-limit=1000')
                if ($IncludeSqlServer) { $arguments += '--include-sqlserver' }
            }
            if ($round -eq 2) { $arguments += '--reverse' }
            $started = [DateTimeOffset]::UtcNow
            Write-Output "$(Get-Date -Format o) Starting $name"
            & dotnet @arguments > (Join-Path $outRoot "$name.log") 2>&1
            $exitCode = $LASTEXITCODE
            $currentBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
            @{ name = $name; startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                exitCode = $exitCode; initialBootUtc = $initialBoot; completedBootUtc = $currentBoot; arguments = $arguments
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outRoot "$name.receipt.json") -Encoding utf8
            if ($exitCode -ne 0) { throw "$name failed; inspect its log." }
            if ($currentBoot -ne $initialBoot) { throw 'Restart detected; preserve these reports separately before repeating the matrix.' }
            $report = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
            if (-not $report.Passed -or $report.Failures.Count -gt 0) { throw "$name failed correctness checks." }
            Write-Output "$(Get-Date -Format o) Passed $name ($($report.Metrics.Count) metrics)"
        }
    }
}
