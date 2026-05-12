#Requires -Version 5.1
<#
.SYNOPSIS
  从当前仓库 git describe 解析版本并调用 Inno Setup 编译安装包。
.DESCRIPTION
  - MyGitDescribe: git describe --tags --always --dirty（用于输出文件名）
  - MyAppVersion:  供 Inno AppVersion 的 a.b.c.d（从 vX.Y.Z 标签解析；无标签时为 0.0.0.0）
  请先运行 publish-self-contained.bat 生成 publish 目录。
#>
$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $RepoRoot

$describe = (& git -C $RepoRoot describe --tags --always --dirty 2>$null)
if (-not $describe) { $describe = 'unknown' }
$describe = $describe.Trim()

# Inno AppVersion：四段数字；标签 vX.Y.Z 映射为 X.Y.Z.0（与 git 标签一致）
$appVer = '0.0.0.0'
if ($describe -match '^v(\d+)\.(\d+)\.(\d+)') {
    $appVer = '{0}.{1}.{2}.0' -f $Matches[1], $Matches[2], $Matches[3]
}

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) {
    $iscc = Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
}
if (-not (Test-Path -LiteralPath $iscc)) {
    throw "未找到 Inno Setup 6（ISCC.exe）。请安装: https://jrsoftware.org/isdl.php"
}

$iss = Join-Path $PSScriptRoot 'CalibOperatorCLI_SelfContained.iss'
Write-Host "git describe: $describe"
Write-Host "Inno AppVersion: $appVer"
Write-Host "ISCC: $iscc"

& $iscc "/DMyAppVersion=$appVer" "/DMyGitDescribe=$describe" $iss
Write-Host "完成。输出目录: $(Join-Path $PSScriptRoot 'dist')"
