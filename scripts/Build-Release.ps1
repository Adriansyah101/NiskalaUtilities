#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\release'
$symbols = Join-Path $repo 'artifacts\symbols'
$staging = Join-Path $repo 'artifacts\staging'
$rules = Join-Path $repo 'rules'
$license = Join-Path $repo 'LICENSE'
$version = ([xml](Get-Content -Raw (Join-Path $repo 'Directory.Build.props'))).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$platform = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }[$Runtime]

$product = 'Niskala Utilities'
$cli = [pscustomobject]@{ Project = 'src\Niskala.CLI'; Name = "NiskalaUtilities-CLI-v$version-$Runtime"; Root = 'niskala.exe'; Files = 'niskala.exe', 'Niskala.EtwHelper.dll' }
$app = [pscustomobject]@{ Project = 'src\Niskala.UI'; Name = "NiskalaUtilities-v$version-$Runtime"; Root = $null; Files = 'Niskala.exe', 'Niskala.EtwHelper.dll' }
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
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $views = @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
        if ($bytes.Length -gt 1) {
            $views += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
        }

        foreach ($term in $Terms) {
            if ($views | Where-Object { $_.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) {
                [pscustomobject]@{ File = $file.FullName.Substring($Root.Length + 1); Term = $term }
            }
        }
    }
}

function Test-Product {
    param([string]$Path)

    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if ($info.ProductName -ne $product -or $info.ProductVersion -notlike "$version*") {
        throw "$Path says '$($info.ProductName)' $($info.ProductVersion), not '$product' $version."
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

function New-Layout {
    param($Payload, [string]$Published, [string]$RootExecutable)

    $output = Join-Path $release $Payload.Name
    $library = New-Item -ItemType Directory -Force (Join-Path $output 'lib')
    Get-ChildItem -LiteralPath $Published -Force | Where-Object Name -ne $Payload.Root | Move-Item -Destination $library
    Copy-Item -LiteralPath $RootExecutable -Destination $output
    Copy-Rules (Join-Path $library 'rules')
    Copy-Item -LiteralPath $license -Destination $library

    $stray = Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.Name -ne 'lib' -and $_.Extension -ne '.exe' }
    if ($stray) {
        throw "$($Payload.Name) has more than executables at its root: $($stray.Name -join ', ')."
    }

    Get-ChildItem -LiteralPath $output -Filter '*.exe' | ForEach-Object { Test-Product $_.FullName }
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
Test-Product (Join-Path $appPublished 'Niskala.exe')
$payloads = @(
    New-Layout $cli $cliPublished (Join-Path $cliPublished $cli.Root)
    New-Layout $app $appPublished $launcher
)
Clear-Folder $staging
Test-Rules (Join-Path $payloads[0] $cli.Root)

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

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$sums = foreach ($payload in $payloads) {
    $zip = "$payload.zip"
    $base = Split-Path -Parent $payload
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
            $entry = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $zip)
}
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))

Get-ChildItem -LiteralPath $release -File | Format-Table Name, @{ Label = 'Bytes'; Expression = { $_.Length.ToString('N0') }; Align = 'Right' } -AutoSize
