#requires -Version 7.0
[CmdletBinding()]
param([string]$Image = "interfacedb-composition-test:local")
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publish = Join-Path $repository 'artifacts/composition-tests/linux-host'
if ((& docker info --format '{{.OSType}}').Trim() -ne 'linux' -or $LASTEXITCODE -ne 0) {
    throw 'Start Docker with Linux containers.'
}
& dotnet publish (Join-Path $repository 'samples/InterfaceDB.TcpStore.Host/InterfaceDB.TcpStore.Host.csproj') -c Release -r linux-x64 --self-contained false /p:UseAppHost=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
& docker build --file (Join-Path $repository 'samples/InterfaceDB.TcpStore.Host/docker/Dockerfile') --tag $Image $publish
if ($LASTEXITCODE -ne 0) { throw 'Isolated host image build failed.' }
& docker image inspect quay.io/keycloak/keycloak:26.7.1 --format '{{.Id}}'
if ($LASTEXITCODE -ne 0) { throw 'Pull the explicitly selected Keycloak test image first.' }
$env:IDB_TYPED_TEST_IMAGE = $Image
& dotnet test (Join-Path $PSScriptRoot 'InterfaceDB.Authentication.KeycloakTests.csproj') -c Release --logger 'trx;LogFilePrefix=typed-keycloak-linux' --results-directory (Join-Path $repository 'artifacts/composition-tests') --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Keycloak/typed Docker acceptance failed. Inspect the TRX.' }
