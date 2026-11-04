param([string] $ResultsDirectory = 'artifacts/test-results')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($project in (Get-Content (Join-Path $PSScriptRoot 'TestProjects.json') -Raw | ConvertFrom-Json)) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    & dotnet test (Join-Path $root $project) -c Release --no-build --no-restore --logger "trx;LogFileName=$name.trx" --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
