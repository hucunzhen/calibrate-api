#Requires -Version 5.1
<#
.SYNOPSIS
  Renames XVCalibrate\CalibOperatorCLI_Example -> IndustrialVisionTools and fixes paths (close VS/Cursor first).
#>
$ErrorActionPreference = 'Stop'
$xv = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $xv '..')).Path
$oldDir = Join-Path $xv 'CalibOperatorCLI_Example'
$newDir = Join-Path $xv 'IndustrialVisionTools'

if (Test-Path -LiteralPath $newDir) {
    Write-Host "Already present: $newDir — nothing to do."
    exit 0
}
if (-not (Test-Path -LiteralPath $oldDir)) {
    Write-Host "Source folder missing: $oldDir — already renamed or wrong layout."
    exit 0
}

Write-Host "Renaming folder: CalibOperatorCLI_Example -> IndustrialVisionTools ..."
Rename-Item -LiteralPath $oldDir -NewName 'IndustrialVisionTools'

$patchFiles = @(
    (Join-Path $repo 'XVCalibrate\XVCalibrate.sln'),
    (Join-Path $repo 'installer\build-self-contained-installer.ps1'),
    (Join-Path $repo 'installer\publish-self-contained.bat'),
    (Join-Path $repo 'installer\CalibOperatorCLI_SelfContained.iss'),
    (Join-Path $repo '.vscode\launch.json')
)
foreach ($f in $patchFiles) {
    if (-not (Test-Path -LiteralPath $f)) { continue }
    $t = [IO.File]::ReadAllText($f)
    $t2 = $t.Replace('CalibOperatorCLI_Example\IndustrialVisionTools.csproj', 'IndustrialVisionTools\IndustrialVisionTools.csproj')
    $t2 = $t2.Replace('XVCalibrate\CalibOperatorCLI_Example', 'XVCalibrate\IndustrialVisionTools')
    $t2 = $t2.Replace('XVCalibrate/CalibOperatorCLI_Example', 'XVCalibrate/IndustrialVisionTools')
    $t2 = $t2.Replace('..\XVCalibrate\CalibOperatorCLI_Example', '..\XVCalibrate\IndustrialVisionTools')
    if ($t2 -ne $t) {
        $enc = New-Object System.Text.UTF8Encoding $true
        [IO.File]::WriteAllText($f, $t2, $enc)
        Write-Host "Patched: $f"
    }
}

Write-Host "Done. Open XVCalibrate.sln again."
