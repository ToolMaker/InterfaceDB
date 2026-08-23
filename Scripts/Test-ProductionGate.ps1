[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$IncludeSqlServer,
    [switch]$SkipTests,
    [string]$Records,
    [string]$Duration,
    [string]$Concurrency,
    [string]$Cache,
    [string]$Durability,
    [string]$Baseline,
    [double]$MaximumRegressionPercent = 20,
    [string]$Output = "TestResults\production-gate.json"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$benchmarkProject = Join-Path $repositoryRoot "StorageBenchmarks.Transactions\StorageBenchmarks.Transactions.csproj"

Push-Location $repositoryRoot
try {
    dotnet build $benchmarkProject -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Production gate build failed."
    }

    if (-not $SkipTests) {
        dotnet test ".\Idb.Analyzers.Tests\Idb.Analyzers.Tests.csproj" -c Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Analyzer tests failed."
        }

        dotnet test ".\InterfaceDb.Tests\InterfaceDb.Tests.csproj" -c Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "InterfaceDB tests failed."
        }

        dotnet run -c Release --no-build --no-restore --project ".\InterfaceDb.IntegrationTests\InterfaceDb.IntegrationTests.csproj"
        if ($LASTEXITCODE -ne 0) {
            throw "InterfaceDB integration harness failed."
        }
    }

    $gateArguments = @(
        "run",
        "-c", "Release",
        "--no-build",
        "--project", $benchmarkProject,
        "--",
        "--production-gate",
        "--output", $Output,
        "--max-regression-percent", $MaximumRegressionPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    if ($Quick) { $gateArguments += "--quick" }
    if ($IncludeSqlServer) { $gateArguments += "--include-sqlserver" }
    if ($Records) { $gateArguments += @("--records", $Records) }
    if ($Duration) { $gateArguments += @("--duration", $Duration) }
    if ($Concurrency) { $gateArguments += @("--concurrency", $Concurrency) }
    if ($Cache) { $gateArguments += @("--cache", $Cache) }
    if ($Durability) { $gateArguments += @("--durability", $Durability) }
    if ($Baseline) { $gateArguments += @("--baseline", $Baseline) }

    & dotnet @gateArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Production benchmark/stress gate failed."
    }
}
finally {
    Pop-Location
}
