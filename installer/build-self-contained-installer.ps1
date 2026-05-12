#Requires -Version 5.1
<#
.SYNOPSIS
  One-shot: git describe -> dotnet publish (self-contained win-x64) -> Inno installer.
.DESCRIPTION
  MyGitDescribe: git describe --tags --always --dirty (output filename).
  MyAppVersion:  Inno a.b.c.d from tag vX.Y.Z -> X.Y.Z.0, else 0.0.0.0.
  Side-by-side installs: deterministic AppId + install dir suffix per describe (see .iss).
  Output: installer\dist\IndustrialVisionTools_Setup_<describe>_win_x64_selfcontained.exe
  Prerequisite: Visual Studio MSBuild (C++ workload) to build IndustrialVisionToolsNative before dotnet publish.
.PARAMETER SkipPublish
  Skip publish; only run ISCC (publish folder must already exist).
.PARAMETER SkipNativeBuild
  Skip MSBuild of IndustrialVisionToolsNative (use only if bin\x64\Release\IndustrialVisionToolsNative.dll already exists).
#>
param(
    [switch] $SkipPublish,
    [switch] $SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
$InstallerDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $InstallerDir '..')).Path
Set-Location $RepoRoot

$describe = (& git -C $RepoRoot describe --tags --always --dirty 2>$null)
if (-not $describe) { $describe = 'unknown' }
$describe = $describe.Trim()

function Get-SafeInstallSuffix([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return 'unknown' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -match '^[0-9A-Za-z._-]$') { [void]$sb.Append($ch) } else { [void]$sb.Append('_') }
    }
    $r = $sb.ToString().Trim('._-')
    if ([string]::IsNullOrWhiteSpace($r)) { $r = 'unknown' }
    if ($r.Length -gt 48) { $r = $r.Substring(0, 48).TrimEnd('._') }
    $r
}

function Get-AppInstanceGuidFromDescribe([string]$seed) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('IndustrialVisionTools:' + $seed)
    $hash = $md5.ComputeHash($bytes)
    [guid][byte[]]$hash
}

$installSuffix = Get-SafeInstallSuffix $describe
$instanceGuid = (Get-AppInstanceGuidFromDescribe $describe).ToString().ToUpperInvariant()

$appVer = '0.0.0.0'
if ($describe -match '^v(\d+)\.(\d+)\.(\d+)') {
    $appVer = '{0}.{1}.{2}.0' -f $Matches[1], $Matches[2], $Matches[3]
}

# XVCalibrate.sln maps CLI project to Release|AnyCPU -> bin\Release\... (not bin\x64\Release\).
$publishRel = 'XVCalibrate\IndustrialVisionTools\bin\Release\net8.0-windows\win-x64\publish'
$publishDir = Join-Path $RepoRoot $publishRel
$nativeDll = Join-Path $RepoRoot 'bin\x64\Release\IndustrialVisionToolsNative.dll'
$publishedExe = Join-Path $publishDir 'IndustrialVisionTools.exe'

Write-Host "Repo: $RepoRoot"
Write-Host "git describe: $describe"
Write-Host "Inno AppVersion: $appVer"
Write-Host "Inno coexist suffix (dir): $installSuffix"
Write-Host "Inno AppId (deterministic): $instanceGuid"

if (-not $SkipPublish) {
    if (-not $SkipNativeBuild) {
        Write-Host ""
        Write-Host "[1/3] MSBuild IndustrialVisionToolsNative (Release|x64)..."
        & (Join-Path $InstallerDir 'build-native-release.ps1') -RepoRoot $RepoRoot
    } else {
        Write-Host ""
        Write-Host "[1/3] Skipping native build (-SkipNativeBuild)."
        if (-not (Test-Path -LiteralPath $nativeDll)) {
            throw "SkipNativeBuild set but missing: $nativeDll. Build IndustrialVisionToolsNative Release x64 first."
        }
    }

    Write-Host ""
    Write-Host "[2/3] dotnet publish (self-contained win-x64)..."
    & dotnet publish (Join-Path $RepoRoot 'XVCalibrate\IndustrialVisionTools\IndustrialVisionTools.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE). Fix build errors above (e.g. OpenCV paths in csproj)."
    }
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Publish output missing: $publishedExe."
    }
    Write-Host "Publish dir: $publishDir"
} else {
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "SkipPublish set but missing: $publishedExe. Run full build without -SkipPublish or publish-self-contained.bat first."
    }
    Write-Host "[1/2] Skipping publish (using existing folder)"
}

$iscc = $null
if ($env:ISCC -and (Test-Path -LiteralPath $env:ISCC)) {
    $iscc = $env:ISCC
}
if (-not $iscc) {
    foreach ($p in @(
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:LocalAppData 'Programs\Inno Setup 7\ISCC.exe'),
            (Join-Path 'D:\Program Files' 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 5\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 5\ISCC.exe')
        )) {
        if (Test-Path -LiteralPath $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    $cmd = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) {
        $iscc = $cmd.Source
    }
}
if (-not $iscc) {
    :reg foreach ($root in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($item in (Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty -LiteralPath $item.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            $name = [string]$props.DisplayName
            if ($name -notmatch '^Inno Setup\s*\d+') { continue }
            $dir = [string]$props.InstallLocation
            if ([string]::IsNullOrWhiteSpace($dir)) { $dir = [string]$props.'Inno Setup: App Path' }
            if ([string]::IsNullOrWhiteSpace($dir)) { continue }
            $candidate = Join-Path ($dir.Trim().TrimEnd('\', '/')) 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate) {
                $iscc = $candidate
                break reg
            }
        }
    }
}
if (-not $iscc) {
    throw @"
Inno Setup ISCC.exe not found.

Install Inno Setup (6 or 7): https://jrsoftware.org/isdl.php
Or set environment variable ISCC to the full path of ISCC.exe, e.g.:
  set ISCC=D:\Program Files\Inno Setup 7\ISCC.exe
"@
}

$iss = Join-Path $InstallerDir 'CalibOperatorCLI_SelfContained.iss'
Write-Host ""
if ($SkipPublish) {
    Write-Host "[2/2] Inno Setup (ISCC)..."
} else {
    Write-Host "[3/3] Inno Setup (ISCC)..."
}
Write-Host "ISCC: $iscc"
& $iscc "/DMyAppVersion=$appVer" "/DMyGitDescribe=$describe" "/DMyAppInstanceGuid=$instanceGuid" "/DMyInstallSuffix=$installSuffix" $iss

$outDir = Join-Path $InstallerDir 'dist'
Write-Host ""
Write-Host "Done. Installer output: $outDir"
