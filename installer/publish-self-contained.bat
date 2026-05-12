@echo off
setlocal
rem 仓库根目录 = 本脚本所在目录的上一级
set "ROOT=%~dp0.."
pushd "%ROOT%" || exit /b 1

echo [1/1] dotnet publish (自包含 win-x64，非单文件，便于与 CalibOperatorNative / OpenCV / HALCON 等 DLL 同目录)...
dotnet publish "XVCalibrate\CalibOperatorCLI_Example\CalibOperatorCLI_Example.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=false

set "PUBLISH=%ROOT%\XVCalibrate\CalibOperatorCLI_Example\bin\x64\Release\net8.0-windows\win-x64\publish"
if not exist "%PUBLISH%\CalibOperatorCLI_Example.exe" (
  echo 错误: 未找到发布输出: "%PUBLISH%\CalibOperatorCLI_Example.exe"
  echo 请先在本机构建 Release 的 CalibOperatorNative，并保证 csproj 中 OpenCV 等 CopyToOutputDirectory 路径有效。
  popd
  exit /b 1
)

echo.
echo 发布目录就绪:
echo   %PUBLISH%
echo.
echo 下一步: 用 git 版本号编译安装包（推荐）:
echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0compile-setup.ps1"
echo.
echo 或手动传入 /DMyAppVersion 与 /DMyGitDescribe（见 CalibOperatorCLI_SelfContained.iss 头部说明）。
echo.
popd
endlocal
