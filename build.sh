#!/usr/bin/env bash
#
# Build the Akoya reference miner end-to-end (native libraries + .NET) into a
# runnable layout. The native libs are staged next to the managed binary so the
# miner's P/Invoke calls resolve them automatically.
#
# Intel Arc / oneAPI (SYCL) is the only backend.
#
# Usage:
#   ./build.sh                                     # JIT (any Intel GPU), Release
#   SYCL_ARCH=intel_gpu_acm_g10 ./build.sh         # AOT for A770/A750
#   SYCL_ARCH=fat FOLD_VIA_MEM=1 ./build.sh        # ONE fat binary: A + B-series AOT
#
# Runs on Linux (x64 and ARM64), including WSL2. Windows: build inside WSL2.
#
# Environment (all optional — sensible defaults):
#   SYCL_ARCH         intel_gpu_acm_g10 | intel_gpu_acm_g11 | fat | …
#                                                    (AOT target; fat = one
#                                                     multi-arch binary; empty = JIT)
#   FOLD_VIA_MEM      1 = fold the PoW transcript via SLM joint_matrix_store
#                                                    instead of joint_matrix_apply.
#                                                    REQUIRED for AOT on Linux (IGC
#                                                    bug); bit-identical shares.
#   RID               .NET runtime identifier        (default: from uname -m → linux-x64 / linux-arm64)
#   CONFIG            .NET build configuration        (default: Release)
#   OUT               ready-to-run output folder      (default: ./out)
#   ONEAPI_RUNTIME    1 = bundle the Intel oneAPI runtime .so files into OUT
#                                                    (default: 1). Set 0 only for a
#                                                    box that has oneAPI installed
#                                                    system-wide — a mining rig does
#                                                    not, and an unbundled folder
#                                                    dies at startup on libsycl.
#   ONEAPI_ROOT       oneAPI install prefix           (default: /opt/intel/oneapi)
#
# Prerequisites are verified at startup (see preflight): .NET 10 SDK, Rust,
# git, make, clang+zlib1g-dev, python3, and the Intel oneAPI DPC++ compiler
# (icpx) with a matching Arc GPU + driver.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SYCL_ARCH="${SYCL_ARCH:-}"               # empty ⇒ JIT (works on any Intel GPU)
CONFIG="${CONFIG:-Release}"
# .NET runtime identifier for the AOT publish — default from the CPU arch
# (x86_64 → linux-x64, aarch64 → linux-arm64). Override with RID=…
case "$(uname -m 2>/dev/null)" in
  aarch64|arm64) _default_rid=linux-arm64 ;;
  *)             _default_rid=linux-x64 ;;
esac
RID="${RID:-$_default_rid}"
OUT="${OUT:-$ROOT/out}"          # ready-to-run output folder

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
die() { printf '\n\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

SPIN=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏)
trap 'printf "\033[?25h" 2>/dev/null || true' EXIT   # always restore the cursor

# run_step "<label>" "<progress|empty>" <cmd...>
# Runs <cmd> with its output captured to a temp log and an animated spinner in
# its place. On success prints a green check (+ the live progress text); on
# failure prints the captured output and exits. <progress> is a shell snippet
# eval'd each tick for a short count — it may reference $log (the output file).
run_step() {
  local label="$1" progress="$2"; shift 2
  local tty=0; [ -t 1 ] && tty=1
  local log p=""; log="$(mktemp)"
  "$@" >"$log" 2>&1 &
  local pid=$!
  if [ "$tty" = 1 ]; then
    printf '\033[?25l'                                   # hide cursor
    local i=0 n=${#SPIN[@]}
    while kill -0 "$pid" 2>/dev/null; do
      p=""; if [ -n "$progress" ]; then p=" — $(eval "$progress" 2>/dev/null || true)"; fi
      printf '\r  \033[36m%s\033[0m %s%s\033[K' "${SPIN[i]}" "$label" "$p"
      i=$(( (i + 1) % n )); sleep 0.1
    done
    printf '\033[?25h'                                   # show cursor
  fi
  local rc=0; wait "$pid" || rc=$?
  p=""; if [ -n "$progress" ]; then p=" — $(eval "$progress" 2>/dev/null || true)"; fi
  if [ "$rc" -eq 0 ]; then
    if [ "$tty" = 1 ]; then printf '\r  \033[1;32m✓\033[0m %s%s\033[K\n' "$label" "$p"
    else                    printf '  ✓ %s%s\n' "$label" "$p"; fi
    rm -f "$log"
  else
    if [ "$tty" = 1 ]; then printf '\r  \033[1;31m✗\033[0m %s\033[K\n' "$label"
    else                    printf '  ✗ %s\n' "$label"; fi
    printf '\n\033[1;31m──── %s failed (exit %d) ────\033[0m\n' "$label" "$rc" >&2
    cat "$log" >&2; rm -f "$log"; exit "$rc"
  fi
}

# Verify every required tool is present before doing any work. Reports ALL
# missing tools at once (with where to get them) and exits, rather than failing
# halfway through.
preflight() {
  local -a miss=()
  if ! command -v dotnet >/dev/null 2>&1; then
    miss+=( "dotnet (.NET 10 SDK)        →  https://dotnet.microsoft.com/download" )
  elif ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    miss+=( ".NET 10 SDK (have: $(dotnet --version 2>/dev/null || echo none)) →  https://dotnet.microsoft.com/download" )
  fi
  command -v cargo  >/dev/null 2>&1 || miss+=( "cargo (Rust toolchain)      →  https://rustup.rs" )
  command -v git    >/dev/null 2>&1 || miss+=( "git                         →  https://git-scm.com  (or your package manager)" )
  command -v make   >/dev/null 2>&1 || miss+=( "make                        →  apt install build-essential" )
  command -v clang  >/dev/null 2>&1 || miss+=( "clang + zlib1g-dev (.NET AOT) →  apt install clang zlib1g-dev" )
  command -v icpx >/dev/null 2>&1 || miss+=( "icpx (Intel oneAPI DPC++ Compiler)  →  https://www.intel.com/content/www/us/en/developer/tools/oneapi/base-toolkit.html" )
  if [ "${#miss[@]}" -gt 0 ]; then
    printf '\n\033[1;31mMissing prerequisites:\033[0m\n' >&2
    printf '  • %s\n' "${miss[@]}" >&2
    printf '\nInstall the tools above, then re-run ./build.sh\n\n' >&2
    exit 1
  fi
}

say "Checking prerequisites (Intel Arc / SYCL)"
preflight

declare -a STAGE   # native libraries to place next to the miner

# ── 1. pearl-gemm — SYCL proof-of-work GEMM kernels ────────────────────
SYCL_DIR="$ROOT/native/pearl-gemm/csrc/sycl"
_sycl_make_args=()
if [ "$SYCL_ARCH" = "fat" ]; then
  # ONE fat binary with both A-series (sg8) and B-series (sg16) AOT kernels.
  _sycl_make_args+=( "ARCH=fat" )
  say "Building Intel Arc backend (FAT multi-arch AOT: A + B-series, if_architecture_is)"
elif [ -n "$SYCL_ARCH" ]; then
  _sycl_make_args+=( "SYCL_TARGETS=spir64_gen" "ARCH=$SYCL_ARCH" )
  say "Building Intel Arc backend (AOT, ARCH=$SYCL_ARCH)"
else
  say "Building Intel Arc backend (JIT — works on any Intel GPU)"
fi
# FOLD_VIA_MEM=1 → SLM transcript fold, the workaround for the IGC AOT bug that
# otherwise makes AOT unbuildable on Linux (docs/IGC-BUG-coop-matrix-aot.md).
# Bit-identical shares; A/B the throughput before making it the default.
if [ -n "${FOLD_VIA_MEM:-}" ]; then
  _sycl_make_args+=( "FOLD_VIA_MEM=$FOLD_VIA_MEM" )
  say "  transcript fold: SLM store path (PEARL_XMX_FOLD_VIA_MEM)"
fi
run_step "Building libpearl_gemm_capi.so + libcuda.so.1 (SYCL / Intel Arc)" "" \
  make -C "$SYCL_DIR" "${_sycl_make_args[@]}"
STAGE+=( "$SYCL_DIR/libpearl_gemm_capi.so"
         "$SYCL_DIR/libcuda.so.1" )   # CUDA→SYCL shim

# csd_capi — CSD sha256d PoW (--algo csd). Deliberately JIT-only:
# correctness-first untuned kernels that run on any Intel GPU via driver JIT.
# (Mirrors the build.ps1 step 1d.)
CSD_DIR="$ROOT/native/csd-sha256d"
run_step "Building libcsd_capi.so (SYCL, CSD algo — JIT)" "" \
  icpx -fsycl -fsycl-device-code-split=per_kernel -O3 -fPIC -shared \
    "$CSD_DIR/csd_capi.cpp" -o "$CSD_DIR/libcsd_capi.so"
STAGE+=( "$CSD_DIR/libcsd_capi.so" )

# sha3t_capi — BitcoinIII SHA3-256t PoW (--algo sha3t). JIT-only, same
# reasoning as csd above. (Mirrors the build.ps1 step 1e.)
SHA3T_DIR="$ROOT/native/sha3t-keccak"
run_step "Building libsha3t_capi.so (SYCL, BitcoinIII algo — JIT)" "" \
  icpx -fsycl -fsycl-device-code-split=per_kernel -O3 -fPIC -shared \
    "$SHA3T_DIR/sha3t_capi.cpp" -o "$SHA3T_DIR/libsha3t_capi.so"
STAGE+=( "$SHA3T_DIR/libsha3t_capi.so" )

# ── 2. pearl-mining-capi — BLAKE3 keyed-merkle C ABI (Rust) ──────────────────
run_step "Building libpearl_mining_capi.so (Rust)" \
  'echo "$(grep -c "Compiling " "$log" 2>/dev/null || true) crates compiled"' \
  cargo build --release --manifest-path "$ROOT/native/Cargo.toml"
STAGE+=( "$ROOT/native/target/release/libpearl_mining_capi.so" )

# ── 2b. randomx-capi — XMRig RandomX backend for --algo rx (x86-64 CPU) ──────
# CPU algo, independent of the GPU backend. Only x86-64: the JIT stub is x86 asm.
if [ "$RID" = "linux-x64" ]; then
  run_step "Building librandomx_capi.so (XMRig RandomX, --algo rx)" "" \
    bash "$ROOT/native/randomx-xmrig/build_capi.sh"
  STAGE+=( "$ROOT/native/randomx-xmrig/librandomx_capi.so" )

  # GhostRider (Raptoreum) — --algo gr. Same x86-64-only constraint (the sph +
  # CryptoNight intrinsics are SSE/AES). Separate library from randomx_capi.
  run_step "Building libghostrider_capi.so (XMRig GhostRider, --algo gr)" "" \
    bash "$ROOT/native/randomx-xmrig/build_gr_capi.sh"
  STAGE+=( "$ROOT/native/randomx-xmrig/libghostrider_capi.so" )

  # NeuroMorph (Cereblix) - --algo nm. Same x86-64-only constraint (AES-NI).
  run_step "Building libneuromorph_capi.so (NeuroMorph, --algo nm)" ""     bash "$ROOT/native/randomx-xmrig/build_nm_capi.sh"
  STAGE+=( "$ROOT/native/randomx-xmrig/libneuromorph_capi.so" )
fi

# ── 3. .NET miner — Native AOT publish into ./out ───────────────────────────
rm -rf "$OUT"

EMB_DIR="$ROOT/src/Akoya.Miner/EmbeddedLibs"
rm -rf "$EMB_DIR"
mkdir -p "$EMB_DIR"
for so in "${STAGE[@]}"; do
  [ -f "$so" ] || die "expected native library not found: $so"
  cp "$so" "$EMB_DIR/"
done

run_step "Publishing arc-miner (Native AOT, $RID) → ./out" "" \
  dotnet publish "$ROOT/src/Akoya.Miner/Akoya.Miner.csproj" \
    -c "$CONFIG" -r "$RID" --self-contained true -p:PublishAot=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$OUT"

# Clean up EmbeddedLibs
rm -rf "$EMB_DIR"

# Keep ./out clean: no managed PDBs, no Native AOT .dbg symbol files.
rm -f "$OUT"/*.pdb "$OUT"/*.dbg

# ── 4. Stage native libs into the ready-to-run folder ───────────────────────
for so in "${STAGE[@]}"; do
  [ -f "$so" ] || die "expected native library not found: $so"
  cp "$so" "$OUT/"
done
printf '  \033[1;32m✓\033[0m Staged %d native librar%s into ./out\n' "${#STAGE[@]}" "$([ "${#STAGE[@]}" -eq 1 ] && echo y || echo ies)"

# ── 5. Stage the Intel oneAPI runtime ───────────────────────────────────────
# The target is a HiveOS rig with no oneAPI installation, so the runtime has to
# travel with the binary. This used to be done by hand, which meant it was
# undone by hand every time: build-linux-wsl.sh finishes with
# `rsync -a --delete` onto the output folder, so anything not produced by this
# script was deleted on the next build. A folder missing libsycl does not
# degrade — it fails to start.
#
# The set is DERIVED, not listed, because a toolkit bump renames these
# (libsycl.so.9 → .so.10) and a hardcoded list would go stale silently: we walk
# ldd over everything in OUT and pull anything that resolves inside the oneAPI
# tree, repeating until the closure is complete.
#
# The search path is OUT *first*, then the oneAPI lib directories: OUT first is
# what terminates the walk (an already-copied library resolves locally, so its
# path no longer starts with ONEAPI_ROOT and it drops out), and the oneAPI dirs
# after it are what makes discovery work at all. Searching OUT alone finds
# nothing on the first pass — every oneAPI library reports "not found" instead
# of reporting a path to copy from. The dirs are enumerated rather than taken
# from the ambient LD_LIBRARY_PATH so this works in a shell where setvars.sh
# was never sourced.
ONEAPI_RUNTIME="${ONEAPI_RUNTIME:-1}"
ONEAPI_ROOT="${ONEAPI_ROOT:-/opt/intel/oneapi}"

if [ "$ONEAPI_RUNTIME" = "1" ]; then
  [ -d "$ONEAPI_ROOT" ] || die "ONEAPI_ROOT=$ONEAPI_ROOT not found — set it, or ONEAPI_RUNTIME=0 to skip bundling"

  # Seeds: the SYCL stack dlopen()s its Unified Runtime adapters and their
  # dependencies, so they appear in NO ldd output and cannot be discovered by
  # the walk below. Without the adapters the miner enumerates zero GPUs.
  ONEAPI_DLOPENED=(
    libur_adapter_level_zero.so.0
    libur_adapter_level_zero_v2.so.0
    libur_adapter_opencl.so.0
    libumf.so.1
    libhwloc.so.15
  )
  for f in "${ONEAPI_DLOPENED[@]}"; do
    p="$(find "$ONEAPI_ROOT" -name "$f" 2>/dev/null | head -1)"
    [ -n "$p" ] || die "oneAPI runtime library not found under $ONEAPI_ROOT: $f"
    cp -L "$p" "$OUT/$f"
  done

  # Every lib/ directory in the toolkit, in one search path.
  _oneapi_ldpath="$(find "$ONEAPI_ROOT" -maxdepth 3 -type d -name lib 2>/dev/null | tr '\n' ':')"
  _oneapi_search="$OUT:$_oneapi_ldpath"

  # Deliberately NOT bundled, even though the walk finds them in the toolkit.
  # libOpenCL.so.1 is the Khronos ICD *loader*: it dispatches to whatever the
  # box registered in /etc/OpenCL/vendors, so it belongs to the installed GPU
  # driver stack, not to us. The rig mines OpenCL-first, and the folder that
  # has been running there does not contain it — shipping our own loader would
  # swap out the dispatch layer underneath a known-good configuration. If a
  # target ever genuinely lacks it, that is a driver installation problem and
  # should be fixed as one.
  ONEAPI_SYSTEM_PROVIDED=( libOpenCL.so.1 )
  _is_system_provided() {
    for _s in "${ONEAPI_SYSTEM_PROVIDED[@]}"; do [ "$_s" = "$1" ] && return 0; done
    return 1
  }

  _oneapi_added=1
  while [ "$_oneapi_added" -eq 1 ]; do
    _oneapi_added=0
    for _bin in "$OUT"/*; do
      [ -f "$_bin" ] || continue
      while read -r _name _path; do
        case "$_path" in "$ONEAPI_ROOT"/*) ;; *) continue ;; esac
        [ -e "$OUT/$_name" ] && continue
        _is_system_provided "$_name" && continue
        cp -L "$_path" "$OUT/$_name"
        _oneapi_added=1
      done < <(LD_LIBRARY_PATH="$_oneapi_search" ldd "$_bin" 2>/dev/null \
                 | awk '$2 == "=>" && $3 ~ /^\// { print $1, $3 }')
    done
  done

  _oneapi_n=$(find "$OUT" -maxdepth 1 -name '*.so*' | wc -l)
  printf '  \033[1;32m✓\033[0m Bundled the oneAPI runtime (%d shared libraries total in ./out)\n' "$_oneapi_n"

  # Gate: an unresolved dependency here is a rig that will not start, and it is
  # far cheaper to fail the build than to find out over SSH. Searches OUT only,
  # deliberately — it asks the question the rig will ask ("does this folder
  # stand alone?"), so it must not see the build box's oneAPI directories.
  # Anything in ONEAPI_SYSTEM_PROVIDED is expected to be missing here — that is
  # the whole point of the list — so it is filtered out rather than tripping it.
  _expected_missing="$(printf '%s\n' "${ONEAPI_SYSTEM_PROVIDED[@]}" | paste -sd'|' -)"
  _missing="$(for _bin in "$OUT"/*; do
                [ -f "$_bin" ] || continue
                LD_LIBRARY_PATH="$OUT" ldd "$_bin" 2>/dev/null \
                  | awk -v b="$(basename "$_bin")" -v skip="$_expected_missing" \
                      '/not found/ && $1 !~ ("^(" skip ")$") { print "    " b ": " $1 }'
              done)"
  [ -z "$_missing" ] || die "unresolved shared-library dependencies in $OUT:
$_missing"
fi

# Must match <AssemblyName> in src/Akoya.Miner/Akoya.Miner.csproj.
BIN="$OUT/arc-miner"
cat <<EOF

✅ Build complete — ready-to-run folder:
   $OUT
   $(ls -1 "$OUT" 2>/dev/null | sed 's/^/     /')

Run it:
   ARC_POOL_WALLET=prl1youraddresshere "$BIN"
EOF
