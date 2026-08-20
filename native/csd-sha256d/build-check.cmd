@echo off
REM Build csd_fused_check.exe (SYCL JIT). Validates the CSD sha256d kernel.
setlocal
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
set "PATH=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin;%PATH%"
set "LIB=C:\Program Files (x86)\Intel\oneAPI\compiler\latest\lib;%LIB%"
cd /d "%~dp0"
icpx -fsycl -fsycl-device-code-split=per_kernel -O3 csd_fused_check.cpp -o csd_fused_check.exe
exit /b %ERRORLEVEL%
