#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SignLocal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\release'
$symbols = Join-Path $repo 'artifacts\symbols'
$staging = Join-Path $repo 'artifacts\staging'
$rules = Join-Path $repo 'rules'
$license = Join-Path $repo 'LICENSE'
$patchNotes = Join-Path $repo 'PATCHNOTES.md'
$certificateStaging = Join-Path $staging 'publisher'
$publisherCertificate = Join-Path $certificateStaging 'niskala-pub.cer'
$installerScript = Join-Path $repo 'installer\NiskalaUtilities.iss'
$archiver = Join-Path $PSScriptRoot 'New-ReleaseArchive.cs'
$props = [xml](Get-Content -Raw (Join-Path $repo 'Directory.Build.props'))
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$languages = $props.SelectSingleNode('/Project/PropertyGroup/SatelliteResourceLanguages').InnerText -split ';'
$platform = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }[$Runtime]

$product = 'Niskala Utilities'
Add-Type -Namespace Niskala -Name Shell -MemberDefinition '[DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);'
$cli = [pscustomobject]@{ Project = 'src\Niskala.CLI'; Name = "NiskalaUtilities-CLI-v$version-$Runtime"; Root = 'niskala.exe'; Files = 'niskala.exe', 'Niskala.EtwHelper.dll'; Description = "$product CLI" }
$app = [pscustomobject]@{ Project = 'src\Niskala.UI'; Name = "NiskalaUtilities-v$version-$Runtime"; Root = $null; Files = 'Niskala.exe', 'Niskala.EtwHelper.dll'; Description = $product }
$launcherProject = Join-Path $repo 'native\Niskala.Launcher\Niskala.Launcher.vcxproj'
$launcher = Join-Path $repo "native\Niskala.Launcher\bin\$platform\Release\Niskala.exe"

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)

    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path -Leaf $FilePath) $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Test-Elevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Clear-Folder {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Find-PrivateText {
    param([string]$Root, [string[]]$Terms)

    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $base = if (Test-Path -LiteralPath $Root -PathType Leaf) { Split-Path -Parent $Root } else { $Root }
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $views = @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
        if ($bytes.Length -gt 1) {
            $views += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
        }

        foreach ($term in $Terms) {
            if ($views | Where-Object { $_.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) {
                [pscustomobject]@{ File = $file.FullName.Substring($base.Length + 1); Term = $term }
            }
        }
    }
}

function Test-Product {
    param([string]$Path, [string]$Description)

    $info = (Get-Item -LiteralPath $Path).VersionInfo
    $name = "$($info.ProductName)".Trim()
    $productVersion = "$($info.ProductVersion)".Trim()
    $fileDescription = "$($info.FileDescription)".Trim()
    if ($name -ne $product -or $productVersion -notlike "$version*") {
        throw "$Path says '$name' $productVersion, not '$product' $version."
    }

    if ($fileDescription -ne $Description) {
        throw "$Path describes itself as '$fileDescription', not '$Description'."
    }

    if ([Niskala.Shell]::ExtractIconExW($Path, -1, $null, $null, 0) -eq 0) {
        throw "$Path carries no icon."
    }
}

function Publish-Payload {
    param($Payload)

    $output = Join-Path $staging $Payload.Name
    Invoke-Native 'dotnet' @('publish', (Join-Path $repo $Payload.Project), '-c', 'Release', '-r', $Runtime, '-o', $output, '-nologo')

    foreach ($file in $Payload.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file))) {
            throw "$($Payload.Name) is missing $file."
        }
    }

    $managed = Get-ChildItem -LiteralPath $output -Filter 'Niskala.*.dll' | Where-Object Name -ne 'Niskala.EtwHelper.dll'
    if ($managed) {
        throw "$($Payload.Name) holds managed assemblies ($($managed.Name -join ', ')): Native AOT did not run."
    }

    $symbolDirectory = New-Item -ItemType Directory -Force (Join-Path $symbols $Payload.Name)
    Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pdb' | Move-Item -Destination $symbolDirectory
    $output
}

function Copy-Rules {
    param([string]$Destination)

    $sources = @(Get-ChildItem -LiteralPath $rules -File -Filter '*.json')
    if ($sources.Count -eq 0) {
        throw "No cleaning rules in $rules."
    }

    $folder = New-Item -ItemType Directory -Force $Destination
    $sources | Copy-Item -Destination $folder
    $copied = @(Get-ChildItem -LiteralPath $folder -File -Filter '*.json')
    if ($copied.Count -ne $sources.Count) {
        throw "$Destination holds $($copied.Count) rule files, not $($sources.Count)."
    }
}

function Test-Rules {
    param([string]$Cli)

    & $Cli clean rules --json | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path -Leaf $Cli) could not load its rules from lib\rules (exit code $LASTEXITCODE)."
    }
}

function Test-CultureFolder {
    param([string]$Name)

    if ($Name -notmatch '^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$') {
        return $false
    }

    try {
        [void][Globalization.CultureInfo]::GetCultureInfo($Name)
        $true
    }
    catch [Globalization.CultureNotFoundException] {
        $false
    }
}

function Remove-ForeignLanguages {
    param([string]$Library)

    $foreign = @(Get-ChildItem -LiteralPath $Library -Directory -Recurse | Where-Object { (Test-CultureFolder $_.Name) -and $languages -notcontains $_.Name })
    $foreign | Sort-Object { $_.FullName.Length } -Descending | Remove-Item -Recurse -Force
    $foreign.Count
}

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $editions = 'Inno Setup 7', 'Inno Setup 6'
    $folders = @()
    foreach ($edition in $editions) {
        foreach ($hive in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
            $entry = Get-ItemProperty -LiteralPath (Join-Path $hive "$($edition)_is1") -ErrorAction SilentlyContinue
            if ($entry -and $entry.PSObject.Properties['InstallLocation']) {
                $folders += $entry.InstallLocation
            }
        }

        $folders += @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs')) |
            Where-Object { $_ } |
            ForEach-Object { Join-Path $_ $edition }
    }

    $folders | ForEach-Object { Join-Path $_ 'ISCC.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function New-Layout {
    param($Payload, [string]$Published, [string]$RootExecutable)

    $output = Join-Path $release $Payload.Name
    $library = New-Item -ItemType Directory -Force (Join-Path $output 'lib')
    Get-ChildItem -LiteralPath $Published -Force | Where-Object Name -ne $Payload.Root | Move-Item -Destination $library
    Copy-Item -LiteralPath $RootExecutable -Destination $output
    Copy-Rules (Join-Path $library 'rules')
    Copy-Item -LiteralPath $license -Destination $library
    Copy-Item -LiteralPath $patchNotes -Destination $library
    $removed = Remove-ForeignLanguages $library
    if ($removed -gt 0) {
        Write-Host "$($Payload.Name): removed $removed language folders other than $($languages -join ', ')."
    }

    $symbolFiles = Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pdb'
    if ($symbolFiles) {
        throw "$($Payload.Name) still holds symbol files: $($symbolFiles.Name -join ', ')."
    }

    $stray = Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.Name -ne 'lib' -and $_.Extension -ne '.exe' }
    if ($stray) {
        throw "$($Payload.Name) has more than executables at its root: $($stray.Name -join ', ')."
    }

    Get-ChildItem -LiteralPath $output -Filter '*.exe' | ForEach-Object { Test-Product $_.FullName $Payload.Description }
    $output
}

$vswhereFolder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $vswhereFolder 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'vswhere.exe is missing: install the Visual Studio C++ build tools (MSVC v143).'
}
$env:PATH = "$env:PATH;$vswhereFolder"
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'The Visual Studio C++ build tools (MSVC v143) are missing.'
}

Write-Host "Niskala $version ($Runtime)"
if (-not (Test-Elevated)) {
    Write-Warning 'Not elevated: the four elevated tests are skipped.'
}

Invoke-Native 'dotnet' @('test', '--solution', (Join-Path $repo 'Niskala.sln'), '-c', 'Release')

foreach ($path in $release, $symbols, $staging) {
    Clear-Folder $path
}

$sdkVariables = 'MSBuildExtensionsPath', 'MSBuildSDKsPath', 'MSBUILD_EXE_PATH'
$saved = @{}
foreach ($name in $sdkVariables) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
try {
    Invoke-Native $msbuild @($launcherProject, '-nologo', '-v:minimal', '-nodeReuse:false', '-t:Rebuild', '-p:Configuration=Release', "-p:Platform=$platform")
}
finally {
    foreach ($name in $sdkVariables) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name])
    }
}
$launcherSymbols = New-Item -ItemType Directory -Force (Join-Path $symbols 'Niskala.Launcher')
Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($launcher, '.pdb')) -Destination $launcherSymbols

$cliPublished = Publish-Payload $cli
$appPublished = Publish-Payload $app
Test-Product (Join-Path $appPublished 'Niskala.exe') $app.Description
$payloads = @(
    New-Layout $cli $cliPublished (Join-Path $cliPublished $cli.Root)
    New-Layout $app $appPublished $launcher
)
Clear-Folder $staging
Test-Rules (Join-Path $payloads[0] $cli.Root)

$signing = $null
if ($SignLocal) {
    $signing = & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -CertificatePath $publisherCertificate -Path @(
        Join-Path $payloads[1] 'Niskala.exe'
        Join-Path $payloads[1] 'lib\Niskala.exe'
        Join-Path $payloads[1] 'lib\Niskala.EtwHelper.dll'
        Join-Path $payloads[0] $cli.Root
        Join-Path $payloads[0] 'lib\Niskala.EtwHelper.dll'
    )
}

$terms = @($env:USERNAME, $env:COMPUTERNAME, $env:USERPROFILE, $repo) |
    Where-Object { $_ -and $_.Length -ge 3 } |
    Sort-Object -Unique
$leaks = foreach ($payload in $payloads) {
    Find-PrivateText -Root $payload -Terms $terms |
        ForEach-Object { "$(Split-Path -Leaf $payload)\$($_.File) (term of $($_.Term.Length) characters)" }
}
if ($leaks) {
    throw "Local traces in the release:`n$($leaks -join "`n")"
}

if ($signing -and (Find-PrivateText -Root $signing.Certificate -Terms $terms)) {
    Clear-Folder $certificateStaging
    throw "Local traces in $(Split-Path -Leaf $signing.Certificate)."
}

$packages = @()
$iscc = Find-InnoCompiler
try {
    if ($iscc) {
        $installer = Join-Path $release "NiskalaUtilities-Setup-v$version-$Runtime.exe"
        $isccArguments = @('/Q', "/DAppVersion=$version", "/DRuntime=$Runtime", "/DAppSource=$($payloads[1])", "/DCliSource=$($payloads[0])", "/DOutputDir=$release")
        if ($signing) {
            $isccArguments += '/DSignInstaller'
            $isccArguments += "/DPublisherCertificate=$($signing.Certificate)"
            $isccArguments += "/DPublisherThumbprint=$($signing.Thumbprint)"
            $isccArguments += ('/Sniskala=$q{0}$q sign /fd SHA256 /sha1 {1} /s My /d $q{2}$q /du {3} $f' -f $signing.SignTool, $signing.Thumbprint, $signing.Description, $signing.ProjectUrl)
        }

        Invoke-Native $iscc ($isccArguments + $installerScript)
        if (-not (Test-Path -LiteralPath $installer)) {
            throw "ISCC.exe finished without writing $installer."
        }

        Test-Product $installer "$product Setup"
        if ($signing -and (Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') {
            throw "$installer is not validly signed."
        }

        $leaks = Find-PrivateText -Root $installer -Terms $terms
        if ($leaks) {
            throw "Local traces in $(Split-Path -Leaf $installer)."
        }

        $packages += $installer
    }
    else {
        Write-Warning 'Inno Setup (ISCC.exe) was not found, so no installer was built. Install it with "winget install --id JRSoftware.InnoSetup -e" (or from jrsoftware.org) and run this script again.'
    }
}
finally {
    Clear-Folder $certificateStaging
    if ((Test-Path -LiteralPath $staging) -and -not (Get-ChildItem -LiteralPath $staging -Force)) {
        Remove-Item -LiteralPath $staging -Force
    }
}

if (Test-Path -LiteralPath $certificateStaging) {
    throw "$certificateStaging was not removed after the installer was built."
}

$zips = $payloads | ForEach-Object { "$_.zip" }
$archiveArguments = for ($i = 0; $i -lt $payloads.Count; $i++) { $payloads[$i]; $zips[$i] }
Invoke-Native 'dotnet' (@('run', '--file', $archiver, '--') + $archiveArguments)
$packages += $zips

$sums = foreach ($package in $packages) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $package)
}
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))

$expected = @($packages | ForEach-Object { Split-Path -Leaf $_ }) + 'SHA256SUMS.txt'
$extra = @(Get-ChildItem -LiteralPath $release -File | Where-Object { $expected -notcontains $_.Name })
if ($extra) {
    throw "$release holds files that are not release assets: $($extra.Name -join ', ')."
}

$certificates = @(Get-ChildItem -LiteralPath $release -Recurse -File -Filter '*.cer')
if ($certificates) {
    throw "$release holds certificate files: $($certificates.Name -join ', ')."
}

Get-ChildItem -LiteralPath $release -File | Format-Table Name, @{ Label = 'Bytes'; Expression = { $_.Length.ToString('N0') }; Align = 'Right' } -AutoSize
