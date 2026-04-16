@echo off
set "MSVC=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717"
set "SDK=C:\Program Files (x86)\Windows Kits\10"

"%MSVC%\bin\Hostx64\x64\cl.exe" ^
  /EHsc ^
  "d:\work\算法API\calibrate_standalone.c" ^
  /Fe"d:\work\算法API\calibrate_standalone.exe" ^
  /I"%MSVC%\include" ^
  /I"%SDK%\Include\10.0.26100.0\ucrt" ^
  /I"%SDK%\Include\10.0.26100.0\shared" ^
  /I"%SDK%\Include\10.0.26100.0\um" ^
  /link ^
  /LIBPATH:"%SDK%\Lib\10.0.26100.0\ucrt\x64" ^
  /LIBPATH:"%SDK%\Lib\10.0.26100.0\um\x64"

echo.
echo Build result: %ERRORLEVEL%
pause
