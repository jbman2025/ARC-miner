#!/usr/bin/env bash
# WSL entry point for the Linux BLUE build. Invoked from Windows as:
#   wsl -d Ubuntu-26.04 -- bash "/mnt/d/ARC-miner kenel test/build-linux-wsl.sh"
#
# Why the copy dance: the repo path contains a SPACE, which GNU make cannot
# handle in prerequisite paths (pearl Makefile: "No rule to make target
# '/mnt/d/ARC-miner'"). We rsync the tree into the WSL filesystem (~/arc-miner,
# no spaces, and ext4 is far faster than /mnt/d 9p anyway), build there, and
# copy out-btx-linux back next to this script.
#
# setvars.sh is incompatible with `set -u` and may `exit` when sourced —
# bring the env up FIRST, then turn on strict mode.
#
# JIT by DEFAULT on Linux: sg16 (Xe2/BMG) AOT is broken in the public Linux
# IGC/ocloc gen backend — see docs/IGC-BUG-coop-matrix-aot.md (the
# joint_matrix_apply AccessChain lowering bug; acm passes, bmg fails; Windows
# unaffected because oneAPI bundles its own IGC). JIT of the SAME kernels is
# proven fine on the rig (the bug doc's own testing), so build with the
# LATEST toolkit so libsycl.so.<N> matches the rig's installed runtime
# (rig has 2026.x -> libsycl.so.9). ONEAPI_VER overrides if ever needed.
#
# AOT is now UNBLOCKED via the code-side workaround, verified 2026-07-29 in this
# same WSL (icpx 2026.1.0, ocloc 26.22.38646.4):
#
#   SYCL_ARCH=fat FOLD_VIA_MEM=1 ./build-linux-wsl.sh
#
# FOLD_VIA_MEM=1 routes the transcript XOR fold through SLM joint_matrix_store
# instead of joint_matrix_apply, dodging the AccessChain lowering the AOT gen
# backend can't handle. A/B on that same run: SYCL_ARCH=fat alone still fails
# with the documented AccessChain error; adding FOLD_VIA_MEM=1 builds all five
# dies clean (9.2 MB, ~79 s).
#
# IT IS STILL NOT THE DEFAULT, AND WILL NOT BE. The SLM round-trip costs two
# sub-group barriers per tile per R-block, and that was MEASURED on real
# hardware at **-28% overall** — far more than the ~5% AOT gain it buys. This
# comment used to say "nobody has measured that", which is how the idea keeps
# coming back; it has been measured, it loses, and Linux therefore ships JIT.
# See also the fleet consequence in the sha3t notes: JIT means the END USER's
# IGC version compiles our kernels, and that is a deliberate trade, not an
# oversight.
if [ -n "${ONEAPI_VER:-}" ] && [ -f "/opt/intel/oneapi/compiler/$ONEAPI_VER/env/vars.sh" ]; then
  source "/opt/intel/oneapi/compiler/$ONEAPI_VER/env/vars.sh" > /dev/null 2>&1
else
  source /opt/intel/oneapi/setvars.sh > /dev/null 2>&1
fi
set -euo pipefail
export PATH="$HOME/.cargo/bin:$PATH"

# Robustness: setvars.sh occasionally does not put the DPC++ compiler on PATH
# (seen when invoked from a non-login / env-polluted shell). Fall back to adding
# the newest oneAPI compiler bin directly so build.sh's `command -v icpx` passes.
if ! command -v icpx >/dev/null 2>&1; then
  for _c in /opt/intel/oneapi/compiler/latest/bin /opt/intel/oneapi/compiler/*/bin; do
    if [ -x "$_c/icpx" ]; then export PATH="$_c:$PATH"; break; fi
  done
fi

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$HOME/arc-miner"
# Output folder name is overridable (OUT_NAME=out-linux ./build-linux-wsl.sh).
OUT_NAME="${OUT_NAME:-out-btx-linux}"

echo "==> Syncing tree to $WORK (excluding build outputs)"
mkdir -p "$WORK"
rsync -a --delete \
  --exclude 'out*/' --exclude 'bin/' --exclude 'obj/' --exclude 'target/' \
  --exclude '_prebuild-bak*/' --exclude '_step1-host/' --exclude '*.tar.gz' \
  --exclude '*.bin' --exclude 'ARC-miner-GREEN-windows/' --exclude 'rig-dashboard/' \
  --exclude '*.dll' --exclude '*.exe' --exclude '*.zip' --exclude '*.lib' --exclude '*.exp' \
  "$SRC/" "$WORK/"

cd "$WORK"
# SYCL_ARCH empty = JIT by default (see the IGC-bug note above).
# SYCL_ARCH=fat FOLD_VIA_MEM=1 builds AOT via the SLM-fold workaround.
SYCL_ARCH="${SYCL_ARCH:-}" FOLD_VIA_MEM="${FOLD_VIA_MEM:-}" \
  OUT="$WORK/$OUT_NAME" ./build.sh

echo "==> Copying $OUT_NAME back to the Windows tree"
mkdir -p "$SRC/$OUT_NAME"
rsync -a --delete "$WORK/$OUT_NAME/" "$SRC/$OUT_NAME/"
echo "==> DONE: $SRC/$OUT_NAME"
