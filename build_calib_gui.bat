@echo off
REM Build CalibrationGUI - Debug版本 (x64)

REM 设置 VS 环境 (x64)
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -host_arch=x64 -arch=x64

REM 设置DLL搜索路径
set PATH=%CD%\debug\dll;%PATH%

echo.
echo Compiling CalibrationGUI.cpp (Debug x64)...
echo.

REM 编译 - Debug版本 (静态链接运行时库)
cl /EHsc /Zi /Od /MTd /Fe:CalibrationGUI.exe "XVCalibrate\CalibrationGUI\CalibrationGUI.cpp" /I"debug\lib" /I"include" user32.lib gdi32.lib comdlg32.lib "debug\lib\BlobAnalysisPro.lib" "debug\lib\ContourPos.lib" "debug\lib\FitShape.lib" "debug\lib\Measure.lib" "debug\lib\Path.lib" "debug\lib\Preprocess.lib"

echo.
echo Compilation result: %ERRORLEVEL%
echo.

pause
