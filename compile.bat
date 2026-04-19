@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
cd /d D:\work\calibrate-api
cl /EHsc /O2 /MD /nologo /Fe:CalibrationGUI.exe XVCalibrate\CalibrationGUI\CalibrationGUI.cpp /Iopencv\build\include /Idebug\lib /Iinclude user32.lib gdi32.lib comdlg32.lib opencv\build\x64\vc16\lib\opencv_world4130.lib > compile_log.txt 2>&1
echo EXIT_CODE=%ERRORLEVEL% >> compile_log.txt
