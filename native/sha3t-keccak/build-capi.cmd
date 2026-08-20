@echo off
REM Build sha3t_capi.dll (SYCL JIT). %1 = output name (default sha3t_capi.dll).
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=sha3t_capi.dll"
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
set "PATH=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "LIB=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\lib;%LIB%"
cd /d "%~dp0"
icpx -fsycl -fsycl-device-code-split=per_kernel -O3 -shared sha3t_capi.cpp -o "%OUT%"
exit /b %ERRORLEVEL%
