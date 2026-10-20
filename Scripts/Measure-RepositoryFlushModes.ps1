param(
    [Parameter(Mandatory)][string]$BinaryDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$Records = 10000,
    [switch]$IncludeSqlServer
)
$ErrorActionPreference = 'Stop'
$binaryRoot = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
foreach ($round in 1..3) {
    $modes = if ($round % 2 -eq 1) { @('ExplicitFlush', 'WriteThrough') } else { @('WriteThrough', 'ExplicitFlush') }
    foreach ($mode in $modes) {
        $name = "$mode-r$round"
        $output = Join-Path $outputRoot "$name.json"
        if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite $output" }
        $arguments = @(
            (Join-Path $binaryRoot 'StorageBenchmarks.Transactions.dll'), '--sql-comparison',
            "--records=$Records", '--iterations=50', '--mutation-limit=1000',
            '--cache=MetadataOnly', '--skip-file', "--segment-durability=$mode",
            "--label=$name", "--output=$output"
        )
        if ($round % 2 -eq 0) { $arguments += '--reverse' }
        if ($IncludeSqlServer) { $arguments += '--include-sqlserver' }
        Write-Output "$(Get-Date -Format o) Starting $name"
        & dotnet @arguments > (Join-Path $outputRoot "$name.log") 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$name failed; inspect its log." }
        $report = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        if (-not $report.Passed -or $report.SegmentDurability -ne $mode) { throw "$name failed validation." }
        Write-Output "$(Get-Date -Format o) Passed $name"
    }
}
