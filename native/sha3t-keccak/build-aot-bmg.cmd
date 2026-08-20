@echo off
REM Single-arch bmg_g21 AOT build of sha3t_capi.dll into a scratch dir, for the
REM tuning loop. %1 = output directory (must already hold the SYCL runtime DLLs
REM and sha3t_bench.exe). The shipping build is build.ps1's fat AOT step; this
REM is the same codegen path for one die so the measure-edit-measure cycle is
REM seconds instead of minutes. Prints ocloc's register/spill warnings, which
REM are the whole point of running it.
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=."
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
REM ocloc lives in the TOOLKIT-level bin and llvm-foreach in compiler\bin\compiler;
REM without both, AOT dies with "ocloc tool could not be found" / "llvm-foreach:
REM no such file or directory". Same PATH set build.ps1 assembles by hand.
set "ONEAPI=C:\Program Files (x86)\Intel\oneAPI"
set "OAVER=2026.1"
set "PATH=%ONEAPI%\compiler\%OAVER%\bin;%ONEAPI%\compiler\%OAVER%\bin\compiler;%ONEAPI%\%OAVER%\bin;%ONEAPI%\ocloc\%OAVER%\bin;%PATH%"
set "LIB=%ONEAPI%\compiler\%OAVER%\lib;%LIB%"
cd /d "%~dp0"
icpx -fsycl -fsycl-targets=spir64_gen -Xsycl-target-backend=spir64_gen "-device bmg_g21" -fsycl-device-code-split=per_kernel -O3 -shared sha3t_capi.cpp -o "%OUT%\sha3t_capi.dll"
exit /b %ERRORLEVEL%
