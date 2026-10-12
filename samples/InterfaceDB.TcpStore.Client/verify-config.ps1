param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$sampleDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $sampleDirectory 'InterfaceDB.TcpStore.Client.csproj'
if (-not $NoBuild) {
    & dotnet build $project --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Client sample build failed.' }
}
$assembly = Join-Path $sampleDirectory 'bin/Debug/net10.0/InterfaceDB.TcpStore.Client.dll'
$template = Get-Content -LiteralPath (Join-Path $sampleDirectory 'clientsettings.example.json') -Raw
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testDirectory = Join-Path $temporaryBase ('interfacedb-client-config-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$missingVariable = 'IDB_MISSING_TEST_SECRET_' + [guid]::NewGuid().ToString('N')
$redactionMarker = 'fixture-credential-must-not-appear-in-diagnostics'
$passed = 0

function Invoke-Rejection([string]$Name, [string[]]$ClientArguments, [int]$ExpectedExit = 1) {
    $output = (& dotnet $assembly @ClientArguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Unexpected exit code for $Name." }
    if ($output.Contains($redactionMarker)) { throw "Sensitive configuration was printed for $Name." }
    $script:passed++
    Write-Output "PASS: $Name"
}

try {
    Invoke-Rejection 'missing arguments' @() 2
    Invoke-Rejection 'unknown command option' @('unused.json', '--unsupported') 2
    $cases = [ordered]@{}
    $base = $template | ConvertFrom-Json -AsHashtable
    $base.clientCredentials.clientSecretEnvironmentVariable = $missingVariable
    $cases['missing credential environment'] = $base | ConvertTo-Json -Depth 12
    $duplicate = '{"host":"localhost","HOST":"' + $redactionMarker + '"}'
    $cases['duplicate property including case variants'] = $duplicate
    $unknown = $template | ConvertFrom-Json -AsHashtable
    $unknown['clientSecret'] = $redactionMarker
    $cases['literal credential and unknown property'] = $unknown | ConvertTo-Json -Depth 12
    $both = $base.Clone()
    $both['accessTokenEnvironmentVariable'] = $missingVariable
    $cases['two token sources'] = $both | ConvertTo-Json -Depth 12
    $unsecured = $template | ConvertFrom-Json -AsHashtable
    $unsecured.clientCredentials.authority = 'http://identity.example.com/realms/interfacedb'
    $cases['HTTP authority'] = $unsecured | ConvertTo-Json -Depth 12
    $legacy = $base.Clone()
    $legacy.authenticationMode = 'MutualTls'
    $cases['mode without token authentication'] = $legacy | ConvertTo-Json -Depth 12
    $numericMode = $base.Clone()
    $numericMode.authenticationMode = 1
    $cases['numeric enum'] = $numericMode | ConvertTo-Json -Depth 12
    $cases['oversized configuration'] = (' ' * 65537)
    foreach ($entry in $cases.GetEnumerator()) {
        $path = Join-Path $testDirectory ($passed.ToString() + '.json')
        [System.IO.File]::WriteAllText($path, $entry.Value)
        Invoke-Rejection $entry.Key @($path)
    }
    Write-Output "$passed configuration failure checks passed; no network or storage access was attempted."
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($testDirectory)
    $prefix = $temporaryBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolved) -notlike 'interfacedb-client-config-*') {
        throw 'Refusing to clean an unexpected test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
