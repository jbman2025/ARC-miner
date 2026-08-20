#!/usr/bin/env bash
# ============================================================================
#  ARC-miner — Intel Arc GPU runtime installer (Debian / Ubuntu / HiveOS)
#
#  All the oneAPI/SYCL libs are BUNDLED next to the miner. Only the GPU *driver
#  userspace* must come from your distro, because it has to match your kernel:
#
#    OpenCL      : intel-opencl-icd            -> libOpenCL + NEO compute runtime
#    Level Zero  : <loader> + <L0 GPU driver>  -> libze_loader.so.1
#                                                 libze_intel_gpu.so.1
#
#  Install both. Level Zero is the intended path; OpenCL is the fallback that
#  keeps the miner running on a box where these deps were never installed (every
#  system has an OpenCL ICD — that is the HiveOS compatibility story), and the
#  launcher selects it automatically when Level Zero is absent.
#
#  Running on the fallback costs ~0.1% on prl, so this is about robustness, not
#  speed. (btx is actually faster on OpenCL.) Historically a missing Level Zero
#  was worse than a slowdown because NEO OpenCL aborted on 2-GPU concurrent USM
#  (enqueue_svm) — that is FIXED as of 2026-07-30 (2x B580, intel-opencl-icd
#  25.18.33578.6 runs multi-GPU clean), so OpenCL-only is now merely a fallback
#  rather than a corruption risk.
#
#  HISTORY — why this script was rewritten (2026-07-30): it used to run
#      apt-get install intel-opencl-icd clinfo libze1 libze-intel-gpu1 \
#        || apt-get install intel-opencl-icd clinfo
#  so if EITHER Level Zero package name failed to resolve, the whole first
#  command failed and the fallback installed OpenCL ONLY. It then "verified"
#  with `clinfo -l` — which passes on an OpenCL-only box — and printed
#  "you're ready". Result: OpenCL works, level_zero silently doesn't.
#  The package names differ across suites, which is exactly what tripped it:
#      newer Debian/Ubuntu : libze1        libze-intel-gpu1
#      older Ubuntu/HiveOS : level-zero    intel-level-zero-gpu
#  So now: each component installs INDEPENDENTLY, every known spelling is
#  tried, and the script VERIFIES Level Zero instead of assuming it.
#
#  That `||` fallback is a real latent bug, but note it was NOT the cause of the
#  2026-07-30 field report ("OpenCL works, level_zero doesn't") on the 2x B580
#  HiveOS rig. There the packages were all present and correct (Ubuntu 24.04:
#  libze1 1.32.0 + libze-intel-gpu1 25.18, both dlopening fine). The actual cause
#  was GROUP MEMBERSHIP: the login user was in no supplementary groups, while
#  /dev/dri/renderD* is root:render and card* is root:video — so NEITHER Intel
#  backend worked unprivileged. It only looked like an L0-specific fault because
#  Level Zero fails loudly (zeInit -> 0x78000001 ERROR_UNINITIALIZED) when it
#  cannot open a render node, whereas the OpenCL ICD loader still enumerates
#  other vendors' platforms and merely omits the Intel device — so `clinfo`
#  prints something and OpenCL looks alive. HiveOS also runs miners as root while
#  a manual ./run.sh runs as the login user, which completes the illusion.
#  => The render/video group check below is the FIRST thing to look at.
# ============================================================================
set -uo pipefail   # NOT -e: a failed probe must not abort the run

SUDO=$([ "$(id -u)" = 0 ] || echo sudo)
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
bad()  { printf '\033[1;31m[FAIL]\033[0m %s\n' "$*"; }
say()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }

# Install the first candidate set that apt can actually resolve.
# Usage: try_install "<label>" "pkg-a pkg-b" "alt-a alt-b" ...
try_install() {
  local label="$1"; shift
  local set
  for set in "$@"; do
    if $SUDO apt-get install -y $set 2>/dev/null; then
      ok "$label: installed [$set]"
      return 0
    fi
    warn "$label: [$set] did not resolve in this suite, trying next spelling"
  done
  bad "$label: no candidate package set resolved"
  return 1
}

say "apt-get update"
$SUDO apt-get update || warn "apt-get update failed — package lists may be stale"

say "OpenCL runtime (NEO)"
try_install "OpenCL" "intel-opencl-icd" "intel-opencl-icd ocl-icd-libopencl1"
OCL_RC=$?

say "Level Zero loader + Intel L0 GPU driver"
# Loader and driver are installed as one set per spelling generation, but the
# two GENERATIONS are independent attempts — and this whole step is independent
# of the OpenCL step above, which is the bug that used to hide L0 failures.
try_install "Level Zero" \
  "libze1 libze-intel-gpu1" \
  "level-zero intel-level-zero-gpu" \
  "libze-loader1 intel-level-zero-gpu" \
  "level-zero"
L0_RC=$?

say "Verification tools"
$SUDO apt-get install -y clinfo >/dev/null 2>&1 || warn "clinfo unavailable (not fatal)"

# ── Verify, per backend, from the library the miner actually dlopens ─────────
say "Verifying the compute stack"
echo "kernel: $(uname -r)"
echo "  Arc A-series (Alchemist) needs ~5.19+; Arc B-series (Battlemage) needs"
echo "  the 'xe' driver — kernel ~6.12+, ideally 6.17+ for stable compute."
echo

echo "--- /dev/dri nodes + your groups ---"
ls -l /dev/dri/ 2>/dev/null || warn "no /dev/dri — the kernel is not exposing the GPU at all"
echo "groups: $(id -nG)"
# THE most common cause of "level_zero doesn't work": no render-node access.
# Checked by actually opening the node rather than by guessing from group names,
# since ACLs or a permissive umask can grant access without group membership.
PERM_OK=1
if [ "$(id -u)" != 0 ]; then
  for n in /dev/dri/renderD*; do
    [ -e "$n" ] || continue
    if [ -r "$n" ] && [ -w "$n" ]; then
      ok "$n: read/write OK"
    else
      bad "$n: NO ACCESS (owned by group '$(stat -c%G "$n")')"
      PERM_OK=0
    fi
  done
  if [ "$PERM_OK" = 0 ]; then
    echo "    FIX:  sudo usermod -aG render,video $(id -un)"
    echo "    then LOG OUT AND BACK IN (new groups only apply to new sessions),"
    echo "    or run the miner with sudo (which is what HiveOS already does)."
    echo "    Without this, Level Zero fails at zeInit with 0x78000001 and OpenCL"
    echo "    silently omits the Intel GPU while still listing other vendors."
  fi
else
  ok "running as root — render-node permissions are not a factor"
fi
echo

echo "--- OpenCL ---"
if command -v clinfo >/dev/null 2>&1 && clinfo -l 2>/dev/null | grep -qi intel; then
  clinfo -l 2>/dev/null | sed 's/^/  /'
  ok "OpenCL sees an Intel platform"
else
  bad "OpenCL does not see an Intel GPU"
fi
echo

echo "--- Level Zero ---"
L0_OK=1
ZE_LOADER="$(ldconfig -p 2>/dev/null | grep -m1 'libze_loader\.so\.1' || true)"
ZE_DRIVER="$(ldconfig -p 2>/dev/null | grep -m1 'libze_intel_gpu\.so' || true)"
if [ -n "$ZE_LOADER" ]; then ok "loader: ${ZE_LOADER##*=> }"
else bad "libze_loader.so.1 MISSING — the L0 UR adapter cannot load"; L0_OK=0; fi
if [ -n "$ZE_DRIVER" ]; then ok "driver: ${ZE_DRIVER##*=> }"
else bad "libze_intel_gpu.so MISSING — L0 will find zero GPU devices"; L0_OK=0; fi

# The real test: can libze_loader.so.1 actually be dlopen'd?
#
# NOTE: `ldd libur_adapter_level_zero.so.0` is USELESS here and always passes —
# libze_loader.so.1 is NOT a DT_NEEDED of the adapter, it is dlopen()ed lazily
# at init (verified: the soname appears only as a plain string plus a dlopen
# reference). That is exactly why this failure is silent: the adapter loads fine,
# fails to find its loader, and reports ZERO devices instead of erroring. So we
# reproduce the dlopen the adapter itself performs.
if command -v python3 >/dev/null 2>&1; then
  if python3 -c "import ctypes,sys; ctypes.CDLL('libze_loader.so.1')" 2>/dev/null; then
    ok "dlopen('libze_loader.so.1') succeeds — same call the UR adapter makes"
  else
    bad "dlopen('libze_loader.so.1') FAILED — the L0 adapter will report 0 devices"
    L0_OK=0
  fi
fi

echo
# Report ONE cause, the actual one. A permissions failure and a missing-package
# failure look similar from the outside but have opposite fixes — printing both
# is how you end up reinstalling packages that were never missing.
if [ "$L0_OK" = 1 ] && [ "$L0_RC" = 0 ] && [ "${PERM_OK:-1}" = 1 ]; then
  ok "Level Zero ready — run:  ./run.sh          (ADAPTER=level_zero is the default)"
elif [ "${PERM_OK:-1}" = 0 ]; then
  bad "Level Zero NOT working — CAUSE: render-node permissions, NOT missing packages."
  echo "  The L0 libraries above are present and load fine; this user just cannot"
  echo "  open /dev/dri/renderD*. Do NOT reinstall anything. Apply the FIX above."
  echo "  Immediate workaround: sudo ./run.sh"
else
  bad "Level Zero NOT working — CAUSE: the L0 runtime is missing or broken."
  echo "  Workaround for right now (single-GPU only — see the USM warning above):"
  echo "      ADAPTER=opencl ./run.sh"
  echo "  To fix Level Zero, add Intel's repo and install the loader explicitly:"
  echo "      curl -fsSL https://repositories.intel.com/gpu/intel-graphics.key \\"
  echo "        | sudo gpg --dearmor -o /usr/share/keyrings/intel-graphics.gpg"
  echo "      echo \"deb [signed-by=/usr/share/keyrings/intel-graphics.gpg] \\"
  echo "        https://repositories.intel.com/gpu/ubuntu \$(. /etc/os-release; echo \$VERSION_CODENAME)/lts/2350 unified\" \\"
  echo "        | sudo tee /etc/apt/sources.list.d/intel-gpu.list"
  echo "      sudo apt-get update && sudo apt-get install -y libze1 libze-intel-gpu1 || \\"
  echo "        sudo apt-get install -y level-zero intel-level-zero-gpu"
  echo "  To watch the adapter actually fail (shows which backend loaded):"
  echo "      SYCL_UR_TRACE=1 ONEAPI_DEVICE_SELECTOR=level_zero:gpu ./arc-miner selftest"
fi

[ "$OCL_RC" = 0 ] || warn "OpenCL install also failed — check your suite/repo setup"
