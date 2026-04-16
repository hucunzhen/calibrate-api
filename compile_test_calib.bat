@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
cl test_calibration.cpp /Fe:test_calibration_fixed.exe /Ot /EHsc /link user32.lib
