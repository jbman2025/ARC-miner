@echo off
REM Build sha3t_bench.exe against the sha3t_capi.dll sitting in this folder.
REM Needs the same vcvars + oneAPI LIB setup as build-capi.cmd. Without it the
REM link fails on libmmd.lib, which is an environment problem, not a code one.
setlocal
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
set "PATH=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "LIB=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\lib;%LIB%"
cd /d "%~dp0"
icpx -fsycl -O3 sha3t_bench.cpp -L. -lsha3t_capi -o sha3t_bench.exe
exit /b %ERRORLEVEL%
