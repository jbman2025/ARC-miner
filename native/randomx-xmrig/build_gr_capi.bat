@echo off
setlocal
REM Builds ghostrider_capi.dll ? the C ABI over XMRig's GhostRider (Raptoreum).
REM Mirrors build_capi.bat (RandomX) but a separate library: GhostRider needs the
REM sph x16 hashes + the six CryptoNight GR variants.
REM
REM We compile XMRig's OWN crypto/ghostrider/ghostrider.cpp (vendored verbatim)
REM rather than reimplementing the hash loop. Two feature switches shape it:
REM   XMRIG_FEATURE_ASM      ON  ? upstream CnHash.cpp JITs the gr_sse41
REM                                mainloops from cn_main_loop.asm (ml64, step 0).
REM                                This is XMRig's production CryptoNight path.
REM   XMRIG_FEATURE_HWLOC    OFF ? selects ghostrider.cpp's simple 8-lane
REM                                hash_octa: no helper threads, no hwloc/libuv.
REM                                Stubs at compat\uv.h and base\io\log\* satisfy
REM                                its unconditional includes so the vendored
REM                                source needs no edits.
REM The lone VAES translation unit is compiled /arch:AVX2 and is never executed
REM at runtime (cn_vaes_enabled stays false).

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

set INC=/I "%ROOT%." /I "%ROOT%compat" /I "%ROOT%crypto\ghostrider"
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
set DEF=/D_CRT_SECURE_NO_WARNINGS /DNDEBUG /DXMRIG_ALGO_GHOSTRIDER /DXMRIG_FEATURE_ASM
set CXXF=/nologo /c /O2 /EHsc /MD /std:c++17 %DEF% %INC%
set CF=/nologo /c /O2 /MD %DEF% %INC%

echo [0/4] Assembling CryptoNight v1 mainloop (ml64)...
pushd crypto\cn\asm\win64
ml64 /nologo /c /Fo"%ROOT%cn_main_loop.obj" cn_main_loop.asm
if errorlevel 1 ( echo ml64 FAILED & popd & exit /b 1 )
popd

echo [1/4] Compiling C++ (shim + CryptoNight glue)...
cl %CXXF% ^
  ghostrider_capi.cpp ^
  compat\cn_r_stubs.cpp ^
  crypto\ghostrider\ghostrider.cpp ^
  crypto\cn\CnHash.cpp ^
  crypto\cn\CnCtx.cpp ^
  backend\cpu\Cpu.cpp ^
  crypto\common\VirtualMemory.cpp ^
  crypto\common\Assembly.cpp ^
  base\crypto\keccak.cpp
if errorlevel 1 ( echo cl C++ FAILED & exit /b 1 )

echo [2/4] Compiling VAES translation unit /arch:AVX2 (never executed)...
cl %CXXF% /arch:AVX2 crypto\cn\CryptoNight_x86_vaes.cpp
if errorlevel 1 ( echo cl VAES FAILED & exit /b 1 )

echo [3/4] Compiling C (sph x16 hashes + cn finalizers)...
cl %CF% ^
  crypto\ghostrider\sph_blake.c ^
  crypto\ghostrider\sph_bmw.c ^
  crypto\ghostrider\sph_groestl.c ^
  crypto\ghostrider\sph_jh.c ^
  crypto\ghostrider\sph_keccak.c ^
  crypto\ghostrider\sph_skein.c ^
  crypto\ghostrider\sph_luffa.c ^
  crypto\ghostrider\sph_cubehash.c ^
  crypto\ghostrider\sph_shavite.c ^
  crypto\ghostrider\sph_simd.c ^
  crypto\ghostrider\sph_echo.c ^
  crypto\ghostrider\sph_hamsi.c ^
  crypto\ghostrider\sph_fugue.c ^
  crypto\ghostrider\sph_shabal.c ^
  crypto\ghostrider\sph_whirlpool.c ^
  crypto\ghostrider\sph_sha2.c ^
  crypto\cn\c_groestl.c ^
  crypto\cn\c_blake256.c ^
  crypto\cn\c_jh.c ^
  crypto\cn\c_skein.c
if errorlevel 1 ( echo cl C FAILED & exit /b 1 )

echo [4/4] Linking ghostrider_capi.dll...
link /nologo /DLL /OUT:ghostrider_capi.dll ghostrider_capi.obj cn_r_stubs.obj ghostrider.obj CnHash.obj CnCtx.obj cn_main_loop.obj Cpu.obj VirtualMemory.obj Assembly.obj keccak.obj CryptoNight_x86_vaes.obj sph_blake.obj sph_bmw.obj sph_groestl.obj sph_jh.obj sph_keccak.obj sph_skein.obj sph_luffa.obj sph_cubehash.obj sph_shavite.obj sph_simd.obj sph_echo.obj sph_hamsi.obj sph_fugue.obj sph_shabal.obj sph_whirlpool.obj sph_sha2.obj c_groestl.obj c_blake256.obj c_jh.obj c_skein.obj advapi32.lib
if errorlevel 1 ( echo link FAILED & exit /b 1 )

echo BUILD OK: %ROOT%ghostrider_capi.dll
endlocal
