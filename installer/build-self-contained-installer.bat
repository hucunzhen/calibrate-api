@echo off
setlocal
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-self-contained-installer.ps1"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Failed, exit code: %ERR%
  pause
  exit /b %ERR%
)
echo.
pause
endlocal
