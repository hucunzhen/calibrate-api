#Requires -Version 5.1
<#
.SYNOPSIS
  Inno only (no publish). Version from git describe.
.DESCRIPTION
  Full pipeline: .\installer\build-self-contained-installer.ps1
#>
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-self-contained-installer.ps1') -SkipPublish
