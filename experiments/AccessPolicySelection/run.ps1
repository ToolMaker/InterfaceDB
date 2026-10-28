$ErrorActionPreference = 'Stop'
$experimentRoot = $PSScriptRoot
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $experimentRoot '../..'))
$originalIndexMode = $env:IDB_EXPERIMENT_INDEX
$originalTiering = $env:DOTNET_TieredCompilation
Push-Location $sourceRoot
try {
    python experiments/AccessPolicySelection/prepare.py
    if ($LASTEXITCODE -ne 0) { throw 'Preparation failed.' }
    $runFolder = Join-Path $sourceRoot ('.tmp/access-policy-selection/runs/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $runFolder | Out-Null
    Copy-Item -LiteralPath '.tmp/access-policy-selection/inputs.json' -Destination $runFolder
    dotnet build .tmp/access-policy-selection/Runner/Runner.csproj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Runner build failed.' }
    dotnet build .tmp/access-policy-selection/Tests/Tests.csproj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    foreach ($variant in 'Scan','Array','Tree','Lazy') {
        $env:IDB_EXPERIMENT_INDEX = $variant
        dotnet test .tmp/access-policy-selection/Tests/Tests.csproj -c Release --no-build --nologo --logger "trx;LogFileName=$variant.trx" --results-directory $runFolder
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $variant" }
    }
    $runnerPath = '.tmp/access-policy-selection/Runner/bin/Release/net10.0/AccessPolicySelection.dll'
    $env:DOTNET_TieredCompilation = '0'
    dotnet $runnerPath micro (Join-Path $runFolder 'micro.json')
    if ($LASTEXITCODE -ne 0) { throw 'Microbenchmark failed.' }
    foreach ($count in 10000,100000) {
        dotnet $runnerPath storage (Join-Path $runFolder "storage-$count.json") $count
        if ($LASTEXITCODE -ne 0) { throw "Storage benchmark failed: $count" }
    }
    $storePath = (Get-Content -LiteralPath (Join-Path $runFolder 'storage-100000.json') -Raw | ConvertFrom-Json).dataPath
    foreach ($round in 0..2) {
        foreach ($variant in 'Scan','Array','Tree','Lazy') {
            dotnet $runnerPath memory (Join-Path $runFolder "memory-$variant-$round.json") 100000 $storePath $variant
            if ($LASTEXITCODE -ne 0) { throw "Memory benchmark failed: $variant" }
        }
    }
    python experiments/AccessPolicySelection/summarize.py $runFolder
    if ($LASTEXITCODE -ne 0) { throw 'Summarization failed.' }
    Write-Output $runFolder
}
finally {
    $env:IDB_EXPERIMENT_INDEX = $originalIndexMode
    $env:DOTNET_TieredCompilation = $originalTiering
    Pop-Location
}
