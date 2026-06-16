<#
.SYNOPSIS
  Build the Akoya reference miner end-to-end on NATIVE Windows (no WSL).

.DESCRIPTION
  Windows counterpart to build.sh. Builds the three native pieces and stages a
  ready-to-run .\out folder:
    1. pearl_gemm_capi.dll   — GPU proof-of-work kernels  (CUDA via CMake+nvcc, or SYCL via icpx)
    2. cuda.dll              — SYCL only: CUDA Driver API → SYCL shim
    2. pearl_mining_capi.dll — BLAKE3 keyed-merkle C ABI    (Rust / cargo)
    3. akoya-miner.exe       — the .NET host, Native AOT, self-contained

  CUDA: requires Visual Studio ("Desktop development with C++" workload), CUDA
  Toolkit (nvcc), Rust, .NET 10 SDK, python, and git.

  SYCL (Intel Arc): requires Intel oneAPI Base Toolkit (icpx), Rust, .NET 10 SDK.
  Source the oneAPI environment before running, or let this script find it:
    . "C:\Program Files (x86)\Intel\oneAPI\setvars.ps1"
    .\build.ps1 -Backend sycl

.EXAMPLE
  .\build.ps1                            # CUDA, auto-detect GPU arch, Release
  .\build.ps1 -Arch ampere               # CUDA, force RTX 30-series / A100
  .\build.ps1 -Arch ada                  # CUDA, RTX 40-series
  .\build.ps1 -Backend sycl              # Intel Arc, JIT (any Arc GPU)
  .\build.ps1 -Backend sycl -SyclArch intel_gpu_acm_g10   # Arc A770/A750, AOT
  .\build.ps1 -Backend sycl -SyclArch intel_gpu_acm_g11   # Arc A580/A380, AOT
  .\build.ps1 -Backend sycl -SyclArch intel_gpu_bmg_g21   # Arc B580/B770, AOT
#>
[CmdletBinding()]
param(
  [ValidateSet('cuda','sycl')]
  [string]$Backend = $(if ($env:BACKEND) { $env:BACKEND } else { 'cuda' }),

  # CUDA-only: GPU architecture.
  [ValidateSet('h100','volta','turing','portable','ampere','ada','blackwell','b200')]
  [string]$Arch = $env:PEARL_GEMM_ARCH,           # empty ⇒ auto-detect

  # SYCL-only: AOT target device. Empty = JIT (works on any Intel GPU at runtime).
  [string]$SyclArch = $(if ($env:SYCL_ARCH) { $env:SYCL_ARCH } else { '' }),

  [ValidateSet('Release','Debug')]
  [string]$Config = $(if ($env:CONFIG) { $env:CONFIG } else { 'Release' }),
  [string]$Rid = 'win-x64',
  [string]$Out = $(if ($PSScriptRoot) { Join-Path $PSScriptRoot 'out' } else { Join-Path (Get-Location).Path 'out' }),
  # CUDA only: nvcc only supports MSVC from VS 2019–2022. Newer VS needs this.
  [ValidateSet('auto','on','off')]
  [string]$AllowUnsupportedCompiler = 'auto'
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

function Say  ($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Die  ($m) { Write-Host "`nERROR: $m" -ForegroundColor Red; exit 1 }
function Step ($m) { Write-Host "  - $m" -ForegroundColor DarkCyan }

# ── Locate Intel oneAPI and source its environment ───────────────────────────
function Find-OneApiSetvars {
  $candidates = @(
    'C:\Program Files (x86)\Intel\oneAPI\setvars.bat',
    'C:\Program Files\Intel\oneAPI\setvars.bat',
    "$env:ONEAPI_ROOT\setvars.bat"
  )
  foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
  return $null
}

function Import-OneApiVars ($setvars) {
  cmd /c "`"$setvars`" --force >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_()]*)=(.*)$') {
      Set-Item -Path "Env:\$($matches[1])" -Value $matches[2]
    }
  }
}

# ── Locate a Visual Studio install with the C++ toolset ──────────────────────
function Find-VsInstall {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (-not (Test-Path $vswhere)) { return $null }
  $path = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath 2>$null | Select-Object -First 1
  if (-not $path) { return $null }
  [pscustomobject]@{
    Path     = $path
    VcVars   = Join-Path $path 'VC\Auxiliary\Build\vcvars64.bat'
    Installer= Split-Path $vswhere
  }
}

# Import vcvars64.bat's environment into the current PowerShell session so nvcc
# finds cl.exe and the .NET AOT linker finds link.exe. Filters out cmd's hidden
# "=X:" per-drive vars and the stale CXX/CC that break CMake auto-detection.
function Import-VcVars ($vcvars, $installerDir) {
  cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_()]*)=(.*)$') {
      Set-Item -Path "Env:\$($matches[1])" -Value $matches[2]
    }
  }
  Remove-Item Env:\CXX, Env:\CC -ErrorAction SilentlyContinue
  if ($installerDir) { $env:PATH = "$installerDir;$env:PATH" }  # vswhere for AOT linker
}

# Map the installed NVIDIA GPU's compute capability → PEARL_GEMM_ARCH.
function Detect-Arch {
  $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
  if (-not $smi) { return '' }
  $cap = (& $smi --query-gpu=compute_cap --format=csv,noheader,nounits 2>$null |
          Select-Object -First 1).Trim()
  switch -Regex ($cap) {
    '^7\.0$'        { 'volta' ;    break }
    '^7\.5$'        { 'turing';    break }
    '^8\.(0|6|7)$'  { 'ampere';    break }
    '^8\.9$'        { 'ada' ;      break }
    '^9\.0$'        { 'h100';      break }
    '^10\.'         { 'b200';      break }
    '^12\.'         { 'blackwell'; break }
    default         { '' }
  }
}

# Resolve cmake/ninja: prefer PATH, else the copies bundled with Visual Studio.
function Resolve-Tool ($name, $vsRelPaths, $vsPath) {
  $c = Get-Command $name -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  foreach ($rel in $vsRelPaths) {
    $p = Join-Path $vsPath $rel
    if (Test-Path $p) { return $p }
  }
  return $null
}

# ── Preflight ────────────────────────────────────────────────────────────────
Say "Checking prerequisites (Backend=$Backend)"
$miss = @()

# Common to all backends
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  $miss += 'dotnet (.NET 10 SDK)  ->  https://dotnet.microsoft.com/download'
} elseif (-not (dotnet --list-sdks 2>$null | Select-String '^10\.')) {
  $miss += ".NET 10 SDK (have: $(dotnet --version 2>$null))  ->  https://dotnet.microsoft.com/download"
}
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $miss += 'cargo (Rust toolchain)  ->  https://rustup.rs' }

$vs = $null; $cmake = $null; $ninja = $null

if ($Backend -eq 'cuda') {
  $vs = Find-VsInstall
  if (-not $vs) { $miss += 'Visual Studio with "Desktop development with C++"  ->  https://visualstudio.microsoft.com (VC.Tools.x86.x64)' }
  if (-not (Get-Command nvcc -ErrorAction SilentlyContinue)) { $miss += 'nvcc (CUDA Toolkit >= 12)  ->  https://developer.nvidia.com/cuda-downloads' }
  if (-not (Get-Command python -ErrorAction SilentlyContinue) -and
      -not (Get-Command py     -ErrorAction SilentlyContinue)) { $miss += 'python (CUDA kernel codegen)  ->  https://python.org' }
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $miss += 'git (CUTLASS submodule)  ->  https://git-scm.com' }
  if ($vs) {
    $cmake = Resolve-Tool 'cmake' @('Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe') $vs.Path
    $ninja = Resolve-Tool 'ninja' @('Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe')      $vs.Path
    if (-not $cmake) { $miss += 'cmake  ->  install the VS "C++ CMake tools" component, or https://cmake.org' }
    if (-not $ninja) { $miss += 'ninja  ->  install the VS "C++ CMake tools" component' }
  }
} elseif ($Backend -eq 'sycl') {
  # Try to find and source oneAPI environment if icpx isn't already on PATH.
  if (-not (Get-Command icpx -ErrorAction SilentlyContinue)) {
    $setvars = Find-OneApiSetvars
    if ($setvars) {
      Step "Sourcing Intel oneAPI environment: $setvars"
      Import-OneApiVars $setvars
    }
  }
  if (-not (Get-Command icpx -ErrorAction SilentlyContinue)) {
    # setvars.bat sometimes fails to propagate PATH into PowerShell; fall back to
    # a direct disk search for the compiler bin directory.
    $icpxDisk = Get-ChildItem 'C:\Program Files (x86)\Intel\oneAPI\compiler' `
      -Recurse -Filter 'icpx.exe' -ErrorAction SilentlyContinue |
      Sort-Object FullName -Descending | Select-Object -First 1
    if ($icpxDisk) {
      $icpxBin = $icpxDisk.Directory.FullName
      $icpxLib = Join-Path (Split-Path $icpxBin) 'lib'
      $env:PATH = "$icpxBin;$env:PATH"
      if (Test-Path $icpxLib) { $env:LIB = "$icpxLib;$env:LIB" }
      Step "Found icpx at $($icpxDisk.FullName) (added to PATH + LIB)"
    }
  }
  if (-not (Get-Command icpx -ErrorAction SilentlyContinue)) {
    $miss += 'icpx (Intel oneAPI DPC++ Compiler)  ->  https://www.intel.com/content/www/us/en/developer/tools/oneapi/base-toolkit.html'
  }
}

if ($miss.Count -gt 0) {
  Write-Host "`nMissing prerequisites:" -ForegroundColor Red
  $miss | ForEach-Object { Write-Host "  - $_" }
  Die 'Install the tools above, then re-run .\build.ps1'
}

# ── Backend-specific setup ────────────────────────────────────────────────────
$stageDlls = @()   # paths of DLLs to copy into $Out

if ($Backend -eq 'cuda') {
  Import-VcVars $vs.VcVars $vs.Installer
  $env:PATH = "$(Split-Path $ninja);$env:PATH"
  Step "Visual Studio: $($vs.Path)"
  Step "cmake: $cmake"
  Step "nvcc:  $((Get-Command nvcc).Source)"

  # Resolve GPU architecture
  if (-not $Arch) {
    $Arch = Detect-Arch
    if ($Arch) { Say "Auto-detected GPU -> PEARL_GEMM_ARCH=$Arch" }
    else { $Arch = 'h100'; Say "No supported GPU detected -> defaulting to h100 (override with -Arch)" }
  }

  # nvcc unsupported-compiler override
  $cudaFlags = @()
  $useAllow = $AllowUnsupportedCompiler
  if ($useAllow -eq 'auto') {
    $clVer = (& cl 2>&1 | Select-String -Pattern 'Version (\d+)\.(\d+)' | Select-Object -First 1)
    $msvcMajorMinor = if ($clVer) { [version]("$($clVer.Matches[0].Groups[1].Value).$($clVer.Matches[0].Groups[2].Value)") } else { [version]'0.0' }
    $useAllow = if ($msvcMajorMinor -gt [version]'19.44') { 'on' } else { 'off' }
  }
  if ($useAllow -eq 'on') {
    Step "MSVC newer than nvcc's supported range -> passing -allow-unsupported-compiler"
    $cudaFlags += '-allow-unsupported-compiler'
  }

  # CUTLASS submodule
  $cutlassHdr = Join-Path $root 'native\pearl-gemm\third_party\cutlass\include\cutlass\cutlass.h'
  if (-not (Test-Path $cutlassHdr)) {
    Say "Fetching CUTLASS submodule"
    git -C $root submodule update --init --depth 1 native/pearl-gemm/third_party/cutlass
    if ($LASTEXITCODE -ne 0) { Die "CUTLASS submodule fetch failed. Clone with --recurse-submodules." }
  }

  # ── 1a. pearl-gemm -> pearl_gemm_capi.dll (CMake + nvcc) ─────────────────
  Say "Building pearl_gemm_capi.dll (CUDA, $Arch)"
  $gemmSrc   = Join-Path $root 'native\pearl-gemm\csrc\capi'
  $gemmBuild = Join-Path $gemmSrc "build-win\$Arch"
  $cfgArgs = @('-S', $gemmSrc, '-B', $gemmBuild, '-G', 'Ninja',
               "-DPEARL_GEMM_ARCH=$Arch", "-DCMAKE_BUILD_TYPE=$Config")
  if ($cudaFlags.Count -gt 0) { $cfgArgs += "-DCMAKE_CUDA_FLAGS=$($cudaFlags -join ' ')" }
  & $cmake @cfgArgs           ; if ($LASTEXITCODE -ne 0) { Die 'CMake configure failed' }
  & $cmake --build $gemmBuild ; if ($LASTEXITCODE -ne 0) { Die 'CMake build failed' }
  $gemmDll = Join-Path $gemmBuild 'pearl_gemm_capi.dll'
  if (-not (Test-Path $gemmDll)) { Die "expected $gemmDll not produced" }
  $stageDlls += $gemmDll

} elseif ($Backend -eq 'sycl') {
  Step "icpx: $((Get-Command icpx).Source)"

  $syclSrc = Join-Path $root 'native\pearl-gemm\csrc\sycl'
  $capiHdr  = Join-Path $root 'native\pearl-gemm\csrc\capi'
  $csrcRoot = Join-Path $root 'native\pearl-gemm\csrc'

  # AOT flags (empty = JIT)
  $aotFlags = @()
  $archDefine = @()
  if ($SyclArch) {
    $aotFlags = @('-fsycl-targets=spir64_gen',
                  "-Xsycl-target-backend=spir64_gen", "-device $SyclArch")
    # Single-arch AOT must pin ONE XMX variant. The JIT build ships both the
    # Xe-HPG (sg8) and Xe2 (sg16) kernels and dispatches at runtime, but the
    # offline gen compiler tries to codegen BOTH for the one target arch and
    # ABORTS on the foreign generation's DPAS shapes (sg8 joint_matrix is
    # invalid for Battlemage and vice-versa — seen as `gen compiler command
    # failed` mid-build). PEARL_XMX_ONLY_SG{8,16} instantiates only the
    # matching variant. Keyed off the ocloc device name.
    if ($SyclArch -match 'bmg') {
      $archDefine = @('-DPEARL_XMX_ONLY_SG16'); $variant = 'Xe2/sg16'
    } elseif ($SyclArch -match 'acm|dg2') {
      $archDefine = @('-DPEARL_XMX_ONLY_SG8');  $variant = 'Xe-HPG/sg8'
    } else {
      $variant = 'both (unknown arch — no pin)'
    }
    Say "Building Intel Arc backend (AOT, SyclArch=$SyclArch, variant=$variant)"
  } else {
    Say "Building Intel Arc backend (JIT - works on any Intel GPU)"
  }

  # per_kernel device-code split is REQUIRED: the kernels ship BOTH the
  # Xe-HPG (sg8) and Xe2 (sg16) XMX variants in one binary, dispatched at
  # runtime. Without the split, the whole module JITs as one image and the
  # foreign generation's DPAS shapes fail the build on A-series cards
  # (observed as install_B rc=-100 in noise_B on an A750).
  $commonFlags = @('-fsycl', '-fsycl-device-code-split=per_kernel') + $aotFlags + $archDefine + @('-O3',
                  "-I$csrcRoot", "-I$syclSrc\..")

  # ── 1b. pearl_gemm_capi.dll (SYCL CAPI) ──────────────────────────────────
  Say "Building pearl_gemm_capi.dll (SYCL)"
  $capiSrc = Join-Path $syclSrc 'pearl_gemm_capi_sycl.cpp'
  $capiDll = Join-Path $syclSrc 'pearl_gemm_capi.dll'
  & icpx @commonFlags -shared $capiSrc -o $capiDll
  if ($LASTEXITCODE -ne 0) { Die 'SYCL CAPI build failed' }
  if (-not (Test-Path $capiDll)) { Die "expected $capiDll not produced" }
  $stageDlls += $capiDll

  # ── 1c. cuda.dll (CUDA Driver API -> SYCL shim) ───────────────────────────
  # Named cuda.dll so .NET's [LibraryImport("cuda")] resolves it on Windows.
  Say "Building cuda.dll (CUDA->SYCL shim)"
  $shimSrc = Join-Path $syclSrc 'cuda_sycl_shim.cpp'
  $shimDll = Join-Path $syclSrc 'cuda.dll'
  & icpx -fsycl @aotFlags -O2 -shared $shimSrc -o $shimDll
  if ($LASTEXITCODE -ne 0) { Die 'SYCL shim build failed' }
  if (-not (Test-Path $shimDll)) { Die "expected $shimDll not produced" }
  $stageDlls += $shimDll
}

# ── 2. pearl-mining-capi -> pearl_mining_capi.dll (Rust) ─────────────────────
Say "Building pearl_mining_capi.dll (Rust)"
cargo build --release --manifest-path (Join-Path $root 'native\Cargo.toml')
if ($LASTEXITCODE -ne 0) { Die 'cargo build failed' }
$miningDll = Join-Path $root 'native\target\release\pearl_mining_capi.dll'
if (-not (Test-Path $miningDll)) { Die "expected $miningDll not produced" }
$stageDlls += $miningDll

# ── 3. .NET host -> akoya-miner.exe (Native AOT) ─────────────────────────────
# Native AOT needs the VS linker (link.exe) regardless of GPU backend.
if ($Backend -eq 'sycl') {
  $vs = Find-VsInstall
  if ($vs) {
    Step "Setting up VS environment for Native AOT linker"
    Import-VcVars $vs.VcVars $vs.Installer
  } else {
    Die 'Native AOT requires Visual Studio ("Desktop development with C++" workload) for the linker'
  }
}
Say "Publishing akoya-miner.exe (Native AOT, $Rid)"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
dotnet publish (Join-Path $root 'src\Akoya.Miner\Akoya.Miner.csproj') `
  -c $Config -r $Rid --self-contained true -p:PublishAot=true `
  -p:DebugType=none -p:DebugSymbols=false -o $Out
if ($LASTEXITCODE -ne 0) { Die 'dotnet publish failed' }
Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

# ── 4. Stage native DLLs next to the binary ──────────────────────────────────
foreach ($dll in $stageDlls) { Copy-Item $dll $Out -Force }
Step "Staged $($stageDlls.Count) native DLL(s) into $Out"

# ── 5. SYCL: copy Intel runtime DLLs needed at runtime ───────────────────────
if ($Backend -eq 'sycl') {
  $icpxBin = Split-Path (Get-Command icpx).Source
  # Minimal SYCL runtime chain, verified empirically on a B580 (2026-06):
  # the DPC++ runtime (sycl8 → ur_win_proxy_loader → ur_loader, libmmd), the
  # OpenCL UR adapter + Khronos ICD loader (OpenCL.dll → enumerates the Arc
  # GPU driver's runtime; kernel JIT happens in the driver's IGC, so
  # sycl-jit.dll is NOT needed even for JIT builds).
  #
  # Deliberately NOT staged (≈210 MB of dead weight):
  #   • intelocl64.dll + svml_dispmd.dll + libiomp5md.dll — Intel CPU OpenCL
  #     runtime; can't even load as shipped (needs tbb12/common_clang64) and
  #     the miner targets the GPU only.
  #   • sycl-jit.dll — driver IGC does the kernel JIT; unused at runtime.
  #   • ur_adapter_level_zero(_v2).dll — needs umf.dll+libhwloc-15.dll to
  #     load, and when complete the Level Zero path measured ~35% SLOWER
  #     than the OpenCL adapter for this workload (18 vs 28 TMADs/s).
  # The DPC++ runtime DLL is versioned with the toolkit: sycl8.dll (oneAPI
  # 2025.x) became sycl9.dll (2026.x). Match either by pattern so a toolkit
  # bump doesn't silently drop the runtime (the AOT exe won't load without it).
  $runtimeDlls = @(
    'ur_win_proxy_loader.dll', 'ur_loader.dll',
    'ur_adapter_opencl.dll', 'OpenCL.dll',
    'libmmd.dll'
  )
  $copied = 0
  # sycl<N>.dll (exclude the -preview / debug 'd' variants).
  $syclRt = Get-ChildItem $icpxBin -Filter 'sycl*.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^sycl\d+\.dll$' } | Select-Object -First 1
  if ($syclRt) { Copy-Item $syclRt.FullName $Out -Force; $copied++ }
  else { Write-Host "  ! WARNING: no sycl<N>.dll found in $icpxBin" -ForegroundColor Yellow }
  foreach ($name in $runtimeDlls) {
    $src = Join-Path $icpxBin $name
    if (Test-Path $src) {
      Copy-Item $src $Out -Force
      $copied++
    }
  }
  Step "Staged $copied Intel SYCL runtime DLL(s) into $Out"
}

Write-Host "`nBuild complete - ready-to-run folder:" -ForegroundColor Green
Write-Host "   $Out"
Get-ChildItem $Out | ForEach-Object { Write-Host "     $($_.Name)" }
Write-Host "`nRun it:" -ForegroundColor Green
Write-Host "   `$env:AKOYA_POOL_WALLET='prl1youraddresshere'; & '$Out\akoya-miner.exe'"
