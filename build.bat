@echo off
REM 编译九点标定示例

REM 设置 VS 环境
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -host_arch=x86 -arch=x86

REM 设置库路径
set LIB=d:\work\算法API\release\lib;d:\work\算法API\第三方库_调用算法需要\ipp2021.12;d:\work\算法API\第三方库_调用算法需要\CGAL5.0.2;%LIB%

REM 编译
cl /EHsc /c "d:\work\算法API\calibrate_example.cpp" /I"d:\work\算法API\include头文件" /Fo"d:\work\算法API\calibrate_example.obj" /W3

echo.
echo Compilation result: %ERRORLEVEL%

pause
