@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
cl calibrate_standalone.cpp /Fe:calibrate_standalone_new.exe /Ot /EHsc /link user32.lib
