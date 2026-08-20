@echo off
setlocal
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

REM The link step below globs *.obj, so ANY stray object file in this directory
REM gets swept into randomx_capi.dll. A stand-alone test program built here (e.g.
REM test_gr.c / test_nm.c) leaves a test_*.obj with its own main() and the link
REM dies with "LNK2005: main already defined". This build recompiles every TU
REM anyway, so start from a clean slate and the link is deterministic.
del /q *.obj >nul 2>&1

echo [1/3] Assembling JIT static stub with ml64...
pushd crypto\randomx
ml64 /nologo /c /Fo"%ROOT%obj_jit_static.obj" jit_compiler_x86_static.asm
if errorlevel 1 ( echo ml64 FAILED & popd & exit /b 1 )
popd

echo [2/3] Compiling RandomX + argon2 + shim...
set INC=/I "%ROOT%." /I "%ROOT%3rdparty\argon2\lib" /I "%ROOT%3rdparty\argon2\include"
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
set DEF=/D_CRT_SECURE_NO_WARNINGS /DNDEBUG /DXMRIG_FEATURE_ASM
set CF=/nologo /c /O2 /EHsc /MD /std:c++17 %DEF% %INC%

cl %CF% ^
  randomx_capi.cpp ^
  capi_support.cpp ^
  backend\cpu\Cpu.cpp ^
  crypto\common\VirtualMemory.cpp ^
  crypto\randomx\aes_hash.cpp ^
  crypto\randomx\allocator.cpp ^
  crypto\randomx\blake2_generator.cpp ^
  crypto\randomx\bytecode_machine.cpp ^
  crypto\randomx\dataset.cpp ^
  crypto\randomx\instructions_portable.cpp ^
  crypto\randomx\jit_compiler_x86.cpp ^
  crypto\randomx\randomx.cpp ^
  crypto\randomx\soft_aes.cpp ^
  crypto\randomx\superscalar.cpp ^
  crypto\randomx\virtual_machine.cpp ^
  crypto\randomx\virtual_memory.cpp ^
  crypto\randomx\vm_compiled.cpp ^
  crypto\randomx\vm_compiled_light.cpp ^
  crypto\randomx\vm_interpreted.cpp ^
  crypto\randomx\vm_interpreted_light.cpp ^
  crypto\randomx\reciprocal.c ^
  crypto\randomx\blake2\blake2b.c ^
  crypto\randomx\blake2\blake2b_sse41.c ^
  3rdparty\argon2\lib\argon2.c ^
  3rdparty\argon2\lib\core.c ^
  3rdparty\argon2\lib\encoding.c ^
  3rdparty\argon2\lib\genkat.c ^
  3rdparty\argon2\lib\impl-select.c ^
  3rdparty\argon2\lib\blake2\blake2.c ^
  3rdparty\argon2\arch\generic\lib\argon2-arch.c
if errorlevel 1 ( echo cl COMPILE FAILED & exit /b 1 )

echo [3/3] Linking randomx_capi.dll...
link /nologo /DLL /OUT:randomx_capi.dll *.obj advapi32.lib
if errorlevel 1 ( echo link FAILED & exit /b 1 )

echo BUILD OK
endlocal
