@echo off
REM Rebuild pearl_gemm_capi DLL (SYCL JIT) — mirrors build.ps1 flags.
REM %1 = output dll name (default pearl_gemm_capi_new.dll)
setlocal enabledelayedexpansion
set "OUT=%~1"
if "%OUT%"=="" set "OUT=pearl_gemm_capi_new.dll"
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
set "PATH=C:\Program Files (x86)\Intel\oneAPI\compiler\2025.3\bin;%PATH%"
set "LIB=C:\Program Files (x86)\Intel\oneAPI\compiler\2025.3\lib;%LIB%"
cd /d "%~dp0"
icpx -fsycl -fsycl-device-code-split=per_kernel -O3 -I.. -I..\.. -shared pearl_gemm_capi_sycl.cpp -o "%OUT%"
exit /b %ERRORLEVEL%
