@echo off
setlocal

set "VSPATH=C:\Program Files\Microsoft Visual Studio\18\Community"
set "MSVC=%VSPATH%\VC\Tools\MSVC\14.50.35717"
set "SDKINCPATH=C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\ucrt"

set "CL=%MSVC%\bin\Hostx64\x64\cl.exe"

"%CL%" /EHsc "d:\work\算法API\calibrate_test.cpp" /Fe"d:\work\算法API\calibrate_test.exe" /I"%MSVC%\include" /I"%SDKINCPATH%" /W3

echo.
echo Result: %ERRORLEVEL%
pause
