#Requires -Version 5.1
<#
.SYNOPSIS
  MSBuild IndustrialVisionToolsNative (Release|x64) so bin\x64\Release\IndustrialVisionToolsNative.dll exists for dotnet publish.
#>
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

function Get-MsBuildExe {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $lines = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        foreach ($line in $lines) {
            if ($line -and (Test-Path -LiteralPath $line)) { return $line }
        }
    }
    foreach ($p in @(
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe')
        )) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    $cmd = Get-Command 'msbuild.exe' -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) { return $cmd.Source }
    $null
}

$nativeSln = Join-Path $RepoRoot 'XVCalibrate\XVCalibrate.sln'
$nativeDll = Join-Path $RepoRoot 'bin\x64\Release\IndustrialVisionToolsNative.dll'

if (-not (Test-Path -LiteralPath $nativeSln)) {
    throw "Solution not found: $nativeSln"
}

$msbuild = Get-MsBuildExe
if (-not $msbuild) {
    throw @"
MSBuild.exe not found. Install Visual Studio 2022 with Desktop development with C++ (or Build Tools + MSVC v143).
"@
}

Write-Host "MSBuild: $msbuild"
Write-Host "Target: IndustrialVisionToolsNative (Release|x64) -> $nativeDll"
& $msbuild $nativeSln /t:IndustrialVisionToolsNative /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild IndustrialVisionToolsNative failed (exit $LASTEXITCODE)."
}
if (-not (Test-Path -LiteralPath $nativeDll)) {
    throw "Expected output missing: $nativeDll"
}
Write-Host "OK: $nativeDll"
