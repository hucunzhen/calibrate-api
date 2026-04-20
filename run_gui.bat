@echo off
taskkill /f /im CalibrationGUI.exe 2>nul
timeout /t 1 /nobreak >nul
start "" "D:\work\calibrate-api\CalibrationGUI.exe"
echo Started CalibrationGUI.exe
