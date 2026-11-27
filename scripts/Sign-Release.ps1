#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,

    [string]$Subject = 'CN=Adriansyah101',

    [string]$CertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$description = 'Niskala Utilities'
$projectUrl = 'https://github.com/Adriansyah101/NiskalaUtilities'
$minimumValidity = New-TimeSpan -Days 30
$lifetimeYears = 5
$trustStores = 'Root', 'TrustedPublisher'

function Test-Elevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -Path (Join-Path $kits '*\x64\signtool.exe') -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Parent.Name -as [version] } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $tool) {
        throw 'signtool.exe is missing: install the Windows SDK with "Windows SDK Signing Tools for Desktop Apps".'
    }

    $tool.FullName
}

function Add-ToStore {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$StoreName, [string]$Location)

    $store = New-Object Security.Cryptography.X509Certificates.X509Store $StoreName, $Location
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        if (-not ($store.Certificates | Where-Object Thumbprint -eq $Certificate.Thumbprint)) {
            $store.Add((New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, $Certificate.RawData)))
            Write-Host "Added the $Subject certificate to $Location\$StoreName."
        }
    }
    finally {
        $store.Close()
    }
}

function Get-SigningCertificate {
    $now = Get-Date
    $certificate = Get-ChildItem -Path 'Cert:\CurrentUser\My' -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt $now.Add($minimumValidity) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter $now.AddYears($lifetimeYears)
        Write-Host "Created a self-signed code signing certificate for $Subject, valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))."
    }

    foreach ($store in $trustStores) {
        Add-ToStore $certificate $store 'CurrentUser'
    }

    if (Test-Elevated) {
        foreach ($store in $trustStores) {
            Add-ToStore $certificate $store 'LocalMachine'
        }
    }
    else {
        Write-Warning 'Not elevated: the certificate was not added to LocalMachine\Root and LocalMachine\TrustedPublisher, which the elevation prompt reads. Run this script as administrator, or install with the setup, to trust it on this PC.'
    }

    $certificate
}

function Save-PublicCertificate {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$Destination)

    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    New-Item -ItemType Directory -Force (Split-Path -Parent $full) | Out-Null
    [IO.File]::WriteAllBytes($full, $Certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $saved = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, [IO.File]::ReadAllBytes($full))
    if ($saved.Thumbprint -ne $Certificate.Thumbprint -or $saved.HasPrivateKey) {
        throw "$full does not hold the public $Subject certificate alone."
    }

    Write-Host "Saved the public $Subject certificate to $full."
    $full
}

$files = foreach ($item in $Path) {
    (Resolve-Path -LiteralPath $item).ProviderPath
}

$signTool = Find-SignTool
$certificate = Get-SigningCertificate
$publicCertificate = if ($CertificatePath) { Save-PublicCertificate $certificate $CertificatePath } else { $null }
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My /d $description /du $projectUrl $files | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "signtool.exe sign failed with exit code $LASTEXITCODE."
}

foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "$file is not validly signed by $Subject ($($signature.Status): $($signature.StatusMessage))."
    }
}

[pscustomobject]@{
    SignTool = $signTool
    Thumbprint = $certificate.Thumbprint
    Description = $description
    ProjectUrl = $projectUrl
    Certificate = $publicCertificate
}
