<#
.SYNOPSIS
  Build the Akoya reference miner end-to-end on NATIVE Windows (no WSL).

.DESCRIPTION
  Windows counterpart to build.sh. Builds the native pieces and stages a
  ready-to-run .\out folder:
    1. pearl_gemm_capi.dll   — GPU proof-of-work kernels  (SYCL via icpx)
    2. cuda.dll              — CUDA Driver API → SYCL shim (see src/Akoya.Cuda)
    3. pearl_mining_capi.dll — BLAKE3 keyed-merkle C ABI    (Rust / cargo)
    4. arc-miner.exe         — the .NET host, Native AOT, self-contained

  Intel Arc / SYCL is the only backend. Requires Intel oneAPI Base Toolkit
  (icpx), Rust, and the .NET 10 SDK. Source the oneAPI environment before
  running, or let this script find it:
    . "C:\Program Files (x86)\Intel\oneAPI\setvars.ps1"
    .\build.ps1

.EXAMPLE
  .\build.ps1                                # JIT (works on any Intel GPU)
  .\build.ps1 -SyclArch intel_gpu_acm_g10    # Arc A770/A750, AOT
  .\build.ps1 -SyclArch intel_gpu_acm_g11    # Arc A580/A380, AOT
  .\build.ps1 -SyclArch intel_gpu_bmg_g21    # Arc B580/B770, AOT
  .\build.ps1 -SyclArch fat                  # ONE fat binary: A + B-series AOT
#>
[CmdletBinding()]
param(
  # AOT target device. Empty = JIT (works on any Intel GPU at runtime).
  [string]$SyclArch = $(if ($env:SYCL_ARCH) { $env:SYCL_ARCH } else { '' }),

  # Fold the PoW transcript through SLM (joint_matrix_store) instead of
  # joint_matrix_apply element access. This is the workaround for the IGC AOT
  # lowering bug (docs/IGC-BUG-coop-matrix-aot.md) that blocks AOT on Linux. The
  # transcript is BIT-IDENTICAL either way, so this switch exists mainly to A/B the
  # SLM round-trip's cost on Windows hardware before Linux adopts it as the default.
  [switch]$FoldViaMem = [bool]$env:FOLD_VIA_MEM,

  [ValidateSet('Release','Debug')]
  [string]$Config = $(if ($env:CONFIG) { $env:CONFIG } else { 'Release' }),
  [string]$Rid = 'win-x64',
  [string]$Out = $(if ($PSScriptRoot) { Join-Path $PSScriptRoot 'out' } else { Join-Path (Get-Location).Path 'out' })
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

function Clean-EnvPathVar ($name) {
  $val = [System.Environment]::GetEnvironmentVariable($name, 'Process')
  if ($val) {
    $parts = $val -split ';'
    $existing = $parts | Where-Object { $_ -and (Test-Path $_) }
    [System.Environment]::SetEnvironmentVariable($name, ($existing -join ';'), 'Process')
  }
}

function Import-OneApiVars ($setvars) {
  cmd /c "`"$setvars`" --force >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_()]*)=(.*)$') {
      Set-Item -Path "Env:\$($matches[1])" -Value $matches[2]
    }
  }
  Clean-EnvPathVar 'LIB'
  Clean-EnvPathVar 'LIBPATH'
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

# Import vcvars64.bat's environment into the current PowerShell session so the
# .NET AOT linker finds link.exe. Filters out cmd's hidden "=X:" per-drive vars
# and the stale CXX/CC.
function Import-VcVars ($vcvars, $installerDir) {
  cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_()]*)=(.*)$') {
      Set-Item -Path "Env:\$($matches[1])" -Value $matches[2]
    }
  }
  Remove-Item Env:\CXX, Env:\CC -ErrorAction SilentlyContinue
  if ($installerDir) { $env:PATH = "$installerDir;$env:PATH" }  # vswhere for AOT linker
  Clean-EnvPathVar 'LIB'
  Clean-EnvPathVar 'LIBPATH'
}

# ── Preflight ────────────────────────────────────────────────────────────────
Say "Checking prerequisites (Intel Arc / SYCL)"
$miss = @()

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  $miss += 'dotnet (.NET 10 SDK)  ->  https://dotnet.microsoft.com/download'
} elseif (-not (dotnet --list-sdks 2>$null | Select-String '^10\.')) {
  $miss += ".NET 10 SDK (have: $(dotnet --version 2>$null))  ->  https://dotnet.microsoft.com/download"
}
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $miss += 'cargo (Rust toolchain)  ->  https://rustup.rs' }

$vs = $null

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
    # bin\compiler holds the driver's internal tools (llvm-foreach, ocloc
    # wrappers). Without it on PATH, AOT device compiles die mid-build with
    # "llvm-foreach: no such file or directory" (seen with oneAPI 2026.0
    # when setvars.bat failed to propagate and only bin\ was added).
    $icpxTools = Join-Path $icpxBin 'compiler'
    # AOT device compiles also need ocloc, which lives in the TOOLKIT-level
    # bin (oneAPI\<ver>\bin), not under compiler\<ver>\bin — setvars.bat
    # would add it, but this branch only runs when setvars failed to
    # propagate, so rebuild the same PATH set by hand for this version.
    $ver = Split-Path (Split-Path $icpxBin) -Leaf                # compiler\<ver>\bin -> <ver>
    $oneapiRoot = Split-Path (Split-Path (Split-Path $icpxBin))  # ...\oneAPI\compiler\<ver>\bin -> ...\oneAPI
    $extraBins = @((Join-Path $oneapiRoot "$ver\bin"),
                   (Join-Path $oneapiRoot "ocloc\$ver\bin"),
                   $icpxTools) | Where-Object { Test-Path $_ }
    $env:PATH = (@($icpxBin) + $extraBins + @($env:PATH)) -join ';'
    if (Test-Path $icpxLib) { $env:LIB = "$icpxLib;$env:LIB" }
    Step "Found icpx at $($icpxDisk.FullName) (added to PATH + LIB + $($extraBins.Count) tool dirs)"
  }
}
if (-not (Get-Command icpx -ErrorAction SilentlyContinue)) {
  $miss += 'icpx (Intel oneAPI DPC++ Compiler)  ->  https://www.intel.com/content/www/us/en/developer/tools/oneapi/base-toolkit.html'
}

if ($miss.Count -gt 0) {
  Write-Host "`nMissing prerequisites:" -ForegroundColor Red
  $miss | ForEach-Object { Write-Host "  - $_" }
  Die 'Install the tools above, then re-run .\build.ps1'
}

# ── Backend-specific setup ────────────────────────────────────────────────────
$stageDlls = @()   # paths of DLLs to copy into $Out

Step "icpx: $((Get-Command icpx).Source)"

$syclSrc = Join-Path $root 'native\pearl-gemm\csrc\sycl'
$capiHdr  = Join-Path $root 'native\pearl-gemm\csrc\capi'
$csrcRoot = Join-Path $root 'native\pearl-gemm\csrc'

# AOT flags (empty = JIT)
$aotFlags = @()
$archDefine = @()
# The non-pearl SYCL algos (csd today; was btx too before it was removed) get
# their own AOT flags: they share the die list but need none of pearl's XMX
# defines (no DPAS dual-variant), and they append a generic spir64 image so GPUs
# absent from the AOT list still run by JIT. Empty = JIT-only.
$syclAotFlags = @()
# Single source of truth for the fat die list (see exact-arch-match note below).
$fatDies = 'intel_gpu_acm_g10,intel_gpu_acm_g11,intel_gpu_acm_g12,intel_gpu_bmg_g21,intel_gpu_bmg_g31'
if ($SyclArch -match '^fat') {
  # FAT multi-arch AOT: ONE pearl_gemm_capi.dll carrying BOTH generations'
  # AOT-optimized kernels (no JIT perf hit, no per-die builds). Compiles the
  # sg8 (Xe-HPG/Alchemist) AND sg16 (Xe2/Battlemage) DPAS kernels in a single
  # invocation; -DPEARL_FAT_AOT switches the XMX kernel bodies to the
  # if_architecture_is guard (pk::xmx_arch_guard) so the foreign generation's
  # DPAS body is elided PER TARGET IMAGE — dodging the gen-compiler abort that
  # forces the single-arch pins below. is_xe_hpg() selects the matching kernel
  # to LAUNCH at runtime. Validated 2026-07-03 on real hardware (A750 acm_g10 +
  # 2x B580 bmg_g21, accepted shares). One AOT image PER DIE is REQUIRED: in a
  # multi-image binary the runtime needs an EXACT arch match to resolve kernels
  # — a bmg_g31-only image does NOT serve a B580 (bmg_g21); kernels come back
  # "not found" (even though a SINGLE-arch g31 build runs on g21). pearl (unlike
  # BTX below) ships NO generic spir64 image to fall back to — if_architecture_is
  # is AOT-only and static_asserts on a generic target — so an unlisted die does
  # not JIT, it simply has no kernels. Enumerate every die we ship:
  # acm_g10=A770/A750, acm_g11=A580/A380, acm_g12=DG2-G12 (mobile ACM),
  # bmg_g21=B580/B570, bmg_g31=B770.
  # acm_g12 added 2026-07-29: is_xe_hpg() and the sg8 xmx_arch_guard in
  # pearl_kernels.hpp already claim intel_gpu_acm_g12, so the runtime expected to
  # serve that die while the fat build emitted no image for it. `ocloc compile
  # --help` (2026.1) lists acm-g12/dg2-g12 as a valid -device.
  $aotFlags = @("-fsycl-targets=$fatDies")
  $archDefine = @('-DPEARL_FAT_AOT')
  # Non-pearl SYCL algos: same dies + generic spir64 fallback for anything
  # unlisted. No XMX pin.
  $syclAotFlags = @("-fsycl-targets=$fatDies,spir64")
  $variant = 'FAT acm_g10/g11/g12 + bmg_g21/g31 (if_architecture_is)'
  Say "Building Intel Arc backend (FAT multi-arch AOT, variant=$variant)"
} elseif ($SyclArch) {
  $aotFlags = @('-fsycl-targets=spir64_gen',
                "-Xsycl-target-backend=spir64_gen", "-device $SyclArch")
  # Non-pearl SYCL algos: single-arch AOT + spir64 JIT fallback for any other GPU.
  $syclAotFlags = @('-fsycl-targets=spir64_gen,spir64',
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
$foldDefine = @()
if ($FoldViaMem) {
  $foldDefine = @('-DPEARL_XMX_FOLD_VIA_MEM')
  Step 'transcript fold: SLM store path (PEARL_XMX_FOLD_VIA_MEM)'
}
$commonFlags = @('-fsycl', '-fsycl-device-code-split=per_kernel') + $aotFlags + $archDefine + $foldDefine + @('-O3',
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

# ── 1d. csd_capi.dll (CSD sha256d PoW — --algo csd) ───────────────────────
# Pure integer sha256d, no XMX arch defines. Follows $syclAotFlags: fat or
# single-arch AOT for the shipping dies (+ spir64 JIT fallback for anything
# unlisted), or pure JIT when -SyclArch is empty.
$csdVariant = if ($syclAotFlags.Count -eq 0) { 'JIT' } elseif ($SyclArch -match '^fat') { "FAT $fatDies + spir64" } else { "$SyclArch + spir64" }
Say "Building csd_capi.dll (SYCL, CSD algo, variant=$csdVariant)"
$csdSrc = Join-Path $root 'native\csd-sha256d\csd_capi.cpp'
$csdDll = Join-Path $root 'native\csd-sha256d\csd_capi.dll'
& icpx -fsycl @syclAotFlags -fsycl-device-code-split=per_kernel -O3 -shared $csdSrc -o $csdDll
if ($LASTEXITCODE -ne 0) { Die 'CSD CAPI build failed' }
if (-not (Test-Path $csdDll)) { Die "expected $csdDll not produced" }
$stageDlls += $csdDll

# ── 1e. sha3t_capi.dll (BitcoinIII SHA3-256t PoW — --algo sha3t) ──────────
# Three keccak-f[1600] permutations per nonce, no XMX and no memory traffic.
# Same AOT story as csd: $syclAotFlags gives the shipping dies plus a generic
# spir64 fallback, or pure JIT when -SyclArch is empty.
$sha3tVariant = if ($syclAotFlags.Count -eq 0) { 'JIT' } elseif ($SyclArch -match '^fat') { "FAT $fatDies + spir64" } else { "$SyclArch + spir64" }
Say "Building sha3t_capi.dll (SYCL, BitcoinIII algo, variant=$sha3tVariant)"
$sha3tSrc = Join-Path $root 'native\sha3t-keccak\sha3t_capi.cpp'
$sha3tDll = Join-Path $root 'native\sha3t-keccak\sha3t_capi.dll'
& icpx -fsycl @syclAotFlags -fsycl-device-code-split=per_kernel -O3 -shared $sha3tSrc -o $sha3tDll
if ($LASTEXITCODE -ne 0) { Die 'sha3t CAPI build failed' }
if (-not (Test-Path $sha3tDll)) { Die "expected $sha3tDll not produced" }
$stageDlls += $sha3tDll

# ── 2. pearl-mining-capi -> pearl_mining_capi.dll (Rust) ─────────────────────
Say "Building pearl_mining_capi.dll (Rust)"
cargo build --release --manifest-path (Join-Path $root 'native\Cargo.toml')
if ($LASTEXITCODE -ne 0) { Die 'cargo build failed' }
$miningDll = Join-Path $root 'native\target\release\pearl_mining_capi.dll'
if (-not (Test-Path $miningDll)) { Die "expected $miningDll not produced" }
$stageDlls += $miningDll

# ── 2b. randomx_capi.dll (XMRig RandomX backend — --algo rx) ──────────────────
# CPU algo, independent of the GPU backend/arch. Self-contained MSVC build
# (build_capi.bat sets up its own vcvars + ml64 for the JIT stub).
Say "Building randomx_capi.dll (XMRig RandomX, --algo rx)"
$rxBat = Join-Path $root 'native\randomx-xmrig\build_capi.bat'
& cmd /c "`"$rxBat`""
if ($LASTEXITCODE -ne 0) { Die 'RandomX CAPI build failed' }
$rxDll = Join-Path $root 'native\randomx-xmrig\randomx_capi.dll'
if (-not (Test-Path $rxDll)) { Die "expected $rxDll not produced" }
$stageDlls += $rxDll

# ── 2c. ghostrider_capi.dll (XMRig GhostRider — --algo gr) ────────────────────
# CPU algo (Raptoreum). Self-contained MSVC build (build_gr_capi.bat sets up its
# own vcvars). Shares the sph x16 hashes + CryptoNight GR variants with nothing
# else, so it is a separate library from randomx_capi.
Say "Building ghostrider_capi.dll (XMRig GhostRider, --algo gr)"
$grBat = Join-Path $root 'native\randomx-xmrig\build_gr_capi.bat'
& cmd /c "`"$grBat`""
if ($LASTEXITCODE -ne 0) { Die 'GhostRider CAPI build failed' }
$grDll = Join-Path $root 'native\randomx-xmrig\ghostrider_capi.dll'
if (-not (Test-Path $grDll)) { Die "expected $grDll not produced" }
$stageDlls += $grDll

# -- 2d. neuromorph_capi.dll (NeuroMorph nm/1 - --algo nm) ---------------------
# CPU algo (Cereblix / CRB). crypto/nm/* is vendored from the xmrig-cereblix
# fork; two lines are patched for MSVC (see the ARC PATCH comments there).
# Self-contained MSVC build - build_nm_capi.bat sets up its own vcvars.
Say "Building neuromorph_capi.dll (NeuroMorph, --algo nm)"
$nmBat = Join-Path $root 'native\randomx-xmrig\build_nm_capi.bat'
& cmd /c "`"$nmBat`""
if ($LASTEXITCODE -ne 0) { Die 'NeuroMorph CAPI build failed' }
$nmDll = Join-Path $root 'native\randomx-xmrig\neuromorph_capi.dll'
if (-not (Test-Path $nmDll)) { Die "expected $nmDll not produced" }
$stageDlls += $nmDll

# ── 3. .NET host -> arc-miner.exe (Native AOT) ───────────────────────────────
# Native AOT needs the VS linker (link.exe).
$vs = Find-VsInstall
if ($vs) {
  Step "Setting up VS environment for Native AOT linker"
  Import-VcVars $vs.VcVars $vs.Installer
} else {
  Die 'Native AOT requires Visual Studio ("Desktop development with C++" workload) for the linker'
}
Say "Publishing arc-miner.exe (Native AOT, $Rid)"

# Preserve WinRing0x64.sys if it exists in the output folder before we clean it
$winRingBackup = $null
if (Test-Path (Join-Path $Out 'WinRing0x64.sys')) {
  $winRingBackup = Join-Path $root 'native\randomx-xmrig\WinRing0x64.sys'
  Copy-Item (Join-Path $Out 'WinRing0x64.sys') $winRingBackup -Force
}

if (Test-Path $Out) {
  try {
    Remove-Item $Out -Recurse -Force -ErrorAction Stop
  } catch {
    Get-ChildItem $Out | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  }
}

# Create EmbeddedLibs directory and stage dlls to embed
$embDir = Join-Path $root 'src\Akoya.Miner\EmbeddedLibs'
if (Test-Path $embDir) { Remove-Item $embDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $embDir -Force >$null

# 1. Copy staged native DLLs
foreach ($dll in $stageDlls) {
  Copy-Item $dll $embDir -Force
}

# 2. Copy WinRing0x64.sys if we backed it up
if ($winRingBackup -and (Test-Path $winRingBackup)) {
  Copy-Item $winRingBackup $embDir -Force
}

# 3. Copy Intel SYCL runtime DLLs so they get embedded too
$icpxBin = Split-Path (Get-Command icpx).Source
$runtimeDlls = @(
  'ur_win_proxy_loader.dll', 'ur_loader.dll',
  'ur_adapter_opencl.dll', 'OpenCL.dll',
  'libmmd.dll'
)
$syclRt = Get-ChildItem $icpxBin -Filter 'sycl*.dll' -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match '^sycl\d+\.dll$' } | Select-Object -First 1
if ($syclRt) { Copy-Item $syclRt.FullName $embDir -Force }
foreach ($name in $runtimeDlls) {
  $src = Join-Path $icpxBin $name
  if (Test-Path $src) { Copy-Item $src $embDir -Force }
}

dotnet publish (Join-Path $root 'src\Akoya.Miner\Akoya.Miner.csproj') `
  -c $Config -r $Rid --self-contained true -p:PublishAot=true `
  -p:DebugType=none -p:DebugSymbols=false -o $Out
if ($LASTEXITCODE -ne 0) {
  # Cleanup staging on failure
  Remove-Item $embDir -Recurse -Force -ErrorAction SilentlyContinue
  Die 'dotnet publish failed'
}

# Clean up the staging directory now that compilation is done
Remove-Item $embDir -Recurse -Force -ErrorAction SilentlyContinue

Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

# ── 4. Stage native DLLs next to the binary (still copy for compatibility/reference) ──────────────────
foreach ($dll in $stageDlls) { Copy-Item $dll $Out -Force }
if ($winRingBackup -and (Test-Path $winRingBackup)) {
  Copy-Item $winRingBackup $Out -Force
  Remove-Item $winRingBackup -Force
}
Step "Staged $($stageDlls.Count) native DLL(s) into $Out"

# ── 5. Copy Intel SYCL runtime DLLs needed at runtime ────────────────────────
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

Write-Host "`nBuild complete - ready-to-run folder:" -ForegroundColor Green
Write-Host "   $Out"
Get-ChildItem $Out | ForEach-Object { Write-Host "     $($_.Name)" }
Write-Host "`nRun it:" -ForegroundColor Green
# arc-miner.exe — must match <AssemblyName> in src/Akoya.Miner/Akoya.Miner.csproj.
Write-Host "   `$env:ARC_POOL_WALLET='prl1youraddresshere'; & '$Out\arc-miner.exe'"
