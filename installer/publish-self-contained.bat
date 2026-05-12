@echo off
setlocal
rem 一键 publish+安装包请用: powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-self-contained-installer.ps1"
rem 本 bat 仅执行 dotnet publish。
set "ROOT=%~dp0.."
pushd "%ROOT%" || exit /b 1

echo [0/1] MSBuild IndustrialVisionToolsNative (Release x64)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-native-release.ps1" -RepoRoot "%CD%"
if errorlevel 1 (
  echo Native build failed. Install VS with C++ workload or build IndustrialVisionToolsNative from XVCalibrate.sln first.
  popd
  exit /b 1
)

echo [1/1] dotnet publish (自包含 win-x64，非单文件，便于与 IndustrialVisionToolsNative / OpenCV / HALCON 等 DLL 同目录)...
dotnet publish "XVCalibrate\IndustrialVisionTools\IndustrialVisionTools.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=false

set "PUBLISH=%ROOT%\XVCalibrate\IndustrialVisionTools\bin\Release\net8.0-windows\win-x64\publish"
if not exist "%PUBLISH%\IndustrialVisionTools.exe" (
  echo 错误: 未找到发布输出: "%PUBLISH%\IndustrialVisionTools.exe"
  echo 请先在本机构建 Release 的 IndustrialVisionToolsNative，并保证 csproj 中 OpenCV 等 CopyToOutputDirectory 路径有效。
  popd
  exit /b 1
)

echo.
echo 发布目录就绪:
echo   %PUBLISH%
echo.
echo 一键生成安装包（含 git 版本与 Inno）:
echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-self-contained-installer.ps1"
echo.
echo 若已 publish，仅打安装包:
echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0compile-setup.ps1"
echo.
popd
endlocal
