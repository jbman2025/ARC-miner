@echo off
setlocal
REM Builds neuromorph_capi.dll - the C ABI over NeuroMorph (nm/1, Cereblix/CRB).
REM
REM crypto\nm\* is vendored from the xmrig-cereblix fork (GPLv3). Upstream only
REM ever builds it with MinGW-GCC, so two lines needed MSVC equivalents - see the
REM "ARC PATCH" comments in nm_neuromorph.c (__umulh) and nm_aes.h (AES-NI gate).
REM
REM /fp:strict matches upstream's -fno-fast-math -ffp-contract=off. NeuroMorph is
REM consensus-bound to plain IEEE-754 float64 with no fused operations; every VM
REM float op is already a single binary operation on its own line, so there is
REM nothing for the compiler to contract, but we pin it rather than rely on that.

REM Locate the newest VS install via vswhere (falls back to the hardcoded path).
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if defined VSPATH (
  call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
) else (
  call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
)
set ROOT=%~dp0
cd /d "%ROOT%"

set INC=/I "%ROOT%."
REM ---------------------------------------------------------------------------
REM VirtualMemory.cpp (and Cpu.cpp, where used) is also compiled by the sibling
REM build_*_capi scripts. That duplication is DELIBERATE -- do not consolidate it
REM into a shared object.
REM   * The three shims use different flag sets: rx defines XMRIG_FEATURE_ASM,
REM     gr adds XMRIG_ALGO_GHOSTRIDER, and nm compiles /fp:strict (required by
REM     the NeuroMorph port) and defines neither. Sharing one .obj would bind all
REM     three to whichever flags built it first.
REM   * Each shim links into its OWN DLL, so there is no ODR concern -- only a
REM     little repeated work: 3 redundant compiles out of ~68 TUs, of the two
REM     smallest files in the tree (323 lines total).
REM   * Consolidating would make three currently-independent builds order-
REM     dependent, to save ~4% of compilations. Not worth it.
REM ---------------------------------------------------------------------------
set DEF=/D_CRT_SECURE_NO_WARNINGS /DNDEBUG
set CXXF=/nologo /c /O2 /EHsc /MD /std:c++17 /fp:strict %DEF% %INC%
set CF=/nologo /c /O2 /MD /fp:strict %DEF% %INC%

echo [1/3] Compiling C++ (shim + huge-page allocator)...
cl %CXXF% neuromorph_capi.cpp crypto\common\VirtualMemory.cpp
if errorlevel 1 ( echo cl C++ FAILED & exit /b 1 )

echo [2/3] Compiling C (NeuroMorph VM + params)...
cl %CF% crypto\nm\nm_neuromorph.c crypto\nm\nm_params.c
if errorlevel 1 ( echo cl C FAILED & exit /b 1 )

echo [3/3] Linking neuromorph_capi.dll...
link /nologo /DLL /OUT:neuromorph_capi.dll neuromorph_capi.obj VirtualMemory.obj nm_neuromorph.obj nm_params.obj advapi32.lib
if errorlevel 1 ( echo link FAILED & exit /b 1 )

echo BUILD OK: %ROOT%neuromorph_capi.dll
endlocal
