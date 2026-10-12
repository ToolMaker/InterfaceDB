#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$Port = 7443,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$localRoot = Join-Path $repositoryRoot 'artifacts/tcp-local'
$certificateDirectory = Join-Path $localRoot 'certificates'
$configurationDirectory = Join-Path $localRoot 'config'
$publishDirectory = Join-Path $repositoryRoot 'artifacts/tcp-host-linux'
$environmentPath = Join-Path $localRoot '.env'
$configurationPath = Join-Path $configurationDirectory 'hostsettings.json'

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repositoryRoot 'samples/InterfaceDB.TcpStore.Host/InterfaceDB.TcpStore.Host.csproj') `
        -c Release -r linux-x64 --self-contained false /p:UseAppHost=false -o $publishDirectory --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the Linux host failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'InterfaceDB.TcpStore.Host.dll'))) {
    throw 'The Linux publish output is missing. Run this script without -SkipPublish first.'
}

$null = New-Item -ItemType Directory -Force -Path $certificateDirectory, $configurationDirectory
$serverPath = Join-Path $certificateDirectory 'server.pfx'
$clientPath = Join-Path $certificateDirectory 'client.pfx'
$rootPath = Join-Path $certificateDirectory 'development-ca.cer'
$expectedFiles = @($serverPath, $clientPath, $rootPath, $environmentPath, $configurationPath)
$presentFiles = @($expectedFiles | Where-Object { Test-Path -LiteralPath $_ })
if ($presentFiles.Count -ne 0 -and $presentFiles.Count -ne $expectedFiles.Count) {
    throw 'The local setup is incomplete. Preserve any needed files, then move artifacts/tcp-local aside before preparing a fresh setup. The Docker data volume is separate.'
}

if ($presentFiles.Count -eq 0) {
    $serverPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $clientPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $rootKey = [System.Security.Cryptography.RSA]::Create(2048)
    $rootCertificate = $null
    try {
        $rootRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=InterfaceDB local Docker development CA', $rootKey,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $rootRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $rootRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign, $true))
        $rootRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey, $false))
        $rootCertificate = $rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(31))
        [System.IO.File]::WriteAllBytes($rootPath, $rootCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        foreach ($identity in @(
            @{ Name = 'localhost'; Usage = '1.3.6.1.5.5.7.3.1'; Path = $serverPath; Password = $serverPassword; Server = $true },
            @{ Name = 'InterfaceDB local development client'; Usage = '1.3.6.1.5.5.7.3.2'; Path = $clientPath; Password = $clientPassword; Server = $false }
        )) {
            $identityKey = [System.Security.Cryptography.RSA]::Create(2048)
            $signedCertificate = $null
            $identityCertificate = $null
            try {
                $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
                    "CN=$($identity.Name)", $identityKey,
                    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
                $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
                $usage = [System.Security.Cryptography.OidCollection]::new()
                $null = $usage.Add([System.Security.Cryptography.Oid]::new($identity.Usage))
                $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $true))
                if ($identity.Server) {
                    $names = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
                    $names.AddDnsName('localhost')
                    $names.AddDnsName('storage')
                    $names.AddIpAddress([System.Net.IPAddress]::Loopback)
                    $request.CertificateExtensions.Add($names.Build())
                }
                $serial = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
                $signedCertificate = $request.Create($rootCertificate, [DateTimeOffset]::UtcNow.AddMinutes(-2), [DateTimeOffset]::UtcNow.AddDays(30), $serial)
                $identityCertificate = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($signedCertificate, $identityKey)
                [System.IO.File]::WriteAllBytes($identity.Path, $identityCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $identity.Password))
                if (-not $identity.Server) {
                    $clientFingerprint = $identityCertificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
                }
            }
            finally {
                if ($null -ne $identityCertificate) { $identityCertificate.Dispose() }
                if ($null -ne $signedCertificate) { $signedCertificate.Dispose() }
                $identityKey.Dispose()
            }
        }

        $configuration = @{
            bindAddress = '0.0.0.0'; port = 7443; storeAlias = 'local-example'
            dataDirectory = '/var/lib/interfacedb'; serverCertificatePath = '/run/secrets/server.pfx'
            serverCertificatePasswordEnvironmentVariable = 'IDB_SERVER_PFX_PASSWORD'
            trustedClientCertificateSha256 = @($clientFingerprint)
            manifest = @{
                apiVersion = 'InterfaceDB-remote-v1'; storageEngine = 'SegmentCore'; storageFormat = 'local-development-v1'
                envelopeFormat = 'opaque-development-v1'; contractIdentity = 'LocalDockerExample'; contractVersion = '1'
                contractSchemaHash = 'local-example-schema'; implementationManifestHash = 'local-example-types'
                indexDefinitionHash = 'local-example-indexes'; approvedBuildIdentity = $null
            }
        }
        $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configurationPath -Encoding utf8NoBOM
        @("IDB_LOCAL_PORT=$Port", "IDB_SERVER_PFX_PASSWORD=$serverPassword", "IDB_CLIENT_PFX_PASSWORD=$clientPassword") |
            Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
    }
    finally {
        if ($null -ne $rootCertificate) { $rootCertificate.Dispose() }
        $rootKey.Dispose()
    }
}
else {
    # Keep identities and existing data stable; changing the local host port needs no certificate regeneration.
    $environmentLines = Get-Content -LiteralPath $environmentPath
    $environmentLines | ForEach-Object {
        if ($_ -match '^IDB_LOCAL_PORT=') { "IDB_LOCAL_PORT=$Port" } else { $_ }
    } | Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
}

Write-Host 'Prepared the Linux host and local development credentials. New certificates last 30 days; existing credentials are reused.'
Write-Host 'Credentials remain in the Git-excluded artifacts/tcp-local directory; no system trust store was changed.'
Write-Host "Start: docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml up --build -d"
Write-Host "Local endpoint: localhost:$Port, alias local-example. The named Docker data volume survives container replacement."
