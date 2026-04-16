@echo off
setlocal enabledelayedexpansion

REM 设置 VS 安装路径
set VSPATH=C:\Program Files\Microsoft Visual Studio\18\Community
set MSVC_VERSION=14.50.35717

REM 查找 Windows SDK
for /d %%i in ("%VSPATH%\WinSDK\10\Lib\10.*") do set SDKVER=%%~ni
for /d %%i in ("%VSPATH%\WinSDK\10\Include\10.*") do set SDKINC=%%~ni

if defined SDKVER (
    echo Found Windows SDK: %SDKVER%
    set LIB=%VSPATH%\VC\Tools\MSVC\%MSVC_VERSION%\lib\x86;%VSPATH%\VC\Auxiliary\VS\lib\x86;%VSPATH%\WinSDK\10\lib\%SDKVER%\ucrt\x86;%VSPATH%\WinSDK\10\lib\%SDKVER%\um\x86;%LIB%
    set INCLUDE=%VSPATH%\VC\Tools\MSVC\%MSVC_VERSION%\include;%VSPATH%\WinSDK\10\Include\%SDKINC%\ucrt;%VSPATH%\WinSDK\10\Include\%SDKINC%\shared;%VSPATH%\WinSDK\10\Include\%SDKINC%\um;%VSPATH%\WinSDK\10\Include\%SDKINC%\winrt;%INCLUDE%
) else (
    echo Windows SDK not found, using basic VC setup
    set LIB=%VSPATH%\VC\Tools\MSVC\%MSVC_VERSION%\lib\x86;%VSPATH%\VC\Auxiliary\VS\lib\x86;%VSPATH%\VC\lib\x86;%LIB%
    set INCLUDE=%VSPATH%\VC\Tools\MSVC\%MSVC_VERSION%\include;%INCLUDE%
)

REM 设置 PATH
set PATH=%VSPATH%\VC\Tools\MSVC\%MSVC_VERSION%\bin\Hostx86\x86;%VSPATH%\Common7\IDE;%VSPATH%\Common7\Tools;%PATH%

echo.
echo INCLUDE=%INCLUDE%
echo.
echo PATH set, now compiling...
echo.

REM 编译
cl /EHsc /c "d:\work\算法API\calibrate_example.cpp" /I"d:\work\算法API\include头文件" /Fo"d:\work\算法API\calibrate_example.obj" /W3 /source-charset:utf-8

echo.
echo Compilation result: %ERRORLEVEL%

if %ERRORLEVEL% EQU 0 (
    echo.
    echo SUCCESS: Object file created!
    echo Now trying to link...
    echo.

    REM 链接
    link /OUT:"d:\work\算法API\calibrate_example.exe" "d:\work\算法API\calibrate_example.obj" ^
         "d:\work\算法API\release\lib\BlobAnalysisPro.lib" ^
         "d:\work\算法API\release\lib\ContourPos.lib" ^
         "d:\work\算法API\release\lib\FitShape.lib" ^
         "d:\work\算法API\release\lib\Measure.lib" ^
         "d:\work\算法API\release\lib\Path.lib" ^
         "d:\work\算法API\release\lib\Preprocess.lib" ^
         kernel32.lib user32.lib gdi32.lib winspool.lib comdlg32.lib advapi32.lib shell32.lib ole32.lib oleaut32.lib uuid.lib odbc32.lib odbccp32.lib /MACHINE:X86 /SUBSYSTEM:CONSOLE

    echo.
    echo Link result: %ERRORLEVEL%
)

endlocal
pause
