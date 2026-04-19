@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
cl /EHsc /std:c++14 /utf-8 /DNOMINMAX /DWIN32_LEAN_AND_MEAN /I d:\work\calibrate-api\opencv\build\include test_detect_trajectory.cpp /Fe:test_detect_trajectory.exe /link /LIBPATH:d:\work\calibrate-api\opencv\build\x64\vc16\lib opencv_world4130.lib
