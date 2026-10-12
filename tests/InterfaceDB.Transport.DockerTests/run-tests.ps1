#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publishDirectory = Join-Path $repository 'artifacts/tcp-host-linux'
$hostProject = Join-Path $repository 'samples/InterfaceDB.TcpStore.Host/InterfaceDB.TcpStore.Host.csproj'
$dockerFile = Join-Path $repository 'samples/InterfaceDB.TcpStore.Host/docker/Dockerfile'
$testProject = Join-Path $PSScriptRoot 'InterfaceDB.Transport.DockerTests.csproj'
$resultsDirectory = Join-Path $repository 'artifacts/tcp-migration-tests/docker'

$dockerOperatingSystem = & docker info --format '{{.OSType}}'
if ($LASTEXITCODE -ne 0) { throw 'Start Docker with Linux containers, then rerun this script.' }
if ($dockerOperatingSystem.Trim() -ne 'linux') { throw 'These tests require Docker Linux containers.' }
& dotnet publish $hostProject -c Release -r linux-x64 --self-contained false /p:UseAppHost=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Linux database host failed.' }
& docker build --file $dockerFile --tag interfacedb-tcp-local:dev $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Building the local database image failed.' }
& dotnet test $testProject -c Release --logger 'trx;LogFilePrefix=docker-storage-migrations' --results-directory $resultsDirectory --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Docker storage/migration tests failed. See the TRX results.' }
