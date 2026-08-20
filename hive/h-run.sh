#!/usr/bin/env bash
# ARC-miner — HiveOS launcher. Called by the HiveOS agent (screen-wrapped).
cd "$(dirname "$BASH_SOURCE")" || exit 1
. h-manifest.conf
. "$CUSTOM_CONFIG_FILENAME" 2>/dev/null   # provides $ARC_ARGS and $ARC_SELECTOR

MINER_DIR="/hive/miners/custom/$CUSTOM_NAME"
cd "$MINER_DIR" || exit 1

# Native libs (.so) ship alongside the binary.
export LD_LIBRARY_PATH="$MINER_DIR:${LD_LIBRARY_PATH:-}"

# .NET / locale hardening (matches out-linux/run.sh).
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export LANG=C.UTF-8 LC_ALL=C.UTF-8

# Intel GPU compute runtime. Defaults to OpenCL here ON PURPOSE — under HiveOS we
# cannot assume the Level Zero packages were ever installed, and every box has an
# OpenCL ICD. OpenCL is the compatibility fallback, not a tuning choice.
# (The old comment claimed "prefer Level Zero" while this line said opencl:gpu —
# the line was right; the comment was wrong.)
#
# Cost of that default: ~0.1% on prl, i.e. nothing. btx is actually FASTER on
# OpenCL (it leans on shared USM, which L0 migrates aggressively), so the default
# suits it too.
#
# The historical reason to prefer L0 — NEO OpenCL aborting on 2-GPU concurrent
# enqueue_svm — is FIXED as of 2026-07-30 (2× B580, Ubuntu 24.04,
# intel-opencl-icd 25.18.33578.6), so this default is no longer a corruption risk.
#
# Precedence: a real ONEAPI_DEVICE_SELECTOR in the environment wins, then the
# flight sheet's ADAPTER= token (h-config.sh resolves it into $ARC_SELECTOR),
# then the OpenCL default. $ARC_SELECTOR used to be written into the config and
# read by NOBODY, so ADAPTER= was silently a no-op on every rig.
export ONEAPI_DEVICE_SELECTOR="${ONEAPI_DEVICE_SELECTOR:-${ARC_SELECTOR:-opencl:gpu}}"
echo "[h-run] ONEAPI_DEVICE_SELECTOR=$ONEAPI_DEVICE_SELECTOR"
[ -f /opt/intel/oneapi/setvars.sh ] && . /opt/intel/oneapi/setvars.sh >/dev/null 2>&1

# Under a supervisor, exit(75) on fatal fault so HiveOS respawns us cleanly
# instead of the miner self-relaunching inside the screen session.
export ARC_API_RESTART_MODE=exit

# Resizable BAR preflight (warn only). Arc perf tanks without a VRAM-sized BAR;
# on older platforms (X99, etc.) that needs a UEFI ReBAR patch + Above 4G Decoding.
[ -f "$MINER_DIR/rebar-check.sh" ] && { . "$MINER_DIR/rebar-check.sh"; arc_rebar_check || true; }

# ---- optional Arc OC: core-clock lock + power cap ---------------------------
# HiveOS runs h-run.sh as root, so we can write the xe/i915 clock + power sysfs
# here (the standalone run.sh does this too; it was missing from the HiveOS path).
# Settings live in oc.conf next to this script so they SURVIVE flight-sheet
# pushes (which only regenerate $CUSTOM_CONFIG_FILENAME). All unset => stock,
# nothing is written.
#   LOCK_MHZ=<n>  pin core clock to n MHz (sets both min & max)
#   MIN_MHZ=<n>   clock floor only (raise idle clock); ignored if LOCK_MHZ set
#   MAX_MHZ=<n>   clock ceiling only;                  ignored if LOCK_MHZ set
#   POWER_W=<n>   per-GPU power cap in watts
[ -f "$MINER_DIR/oc.conf" ] && . "$MINER_DIR/oc.conf"
arc_apply_oc() {
  local GT HW n
  if [ -n "$LOCK_MHZ$MIN_MHZ$MAX_MHZ" ]; then
    for GT in $(find /sys/devices -path '*tile0/gt0/freq0' 2>/dev/null); do
      [ -w "$GT/max_freq" ] || { echo "[h-run][oc] ${GT} not writable (need root) — skipped"; continue; }
      if [ -n "$LOCK_MHZ" ]; then
        echo "$LOCK_MHZ" > "$GT/max_freq"; echo "$LOCK_MHZ" > "$GT/min_freq"
      else
        [ -n "$MAX_MHZ" ] && echo "$MAX_MHZ" > "$GT/max_freq"
        [ -n "$MIN_MHZ" ] && echo "$MIN_MHZ" > "$GT/min_freq"
      fi
      echo "[h-run][oc] ${GT%/tile0/gt0/freq0}: min=$(cat "$GT/min_freq") max=$(cat "$GT/max_freq") act=$(cat "$GT/act_freq")"
    done
  fi
  if [ -n "$POWER_W" ]; then
    for HW in /sys/class/drm/card*/device/hwmon/hwmon*/power1_cap; do
      [ -w "$HW" ] || { echo "[h-run][oc] ${HW} not writable (need root) — skipped"; continue; }
      n=$(( POWER_W * 1000000 ))
      echo "$n" > "$HW" && echo "[h-run][oc] $(dirname "$HW"): cap=${POWER_W}W"
    done
  fi
}
arc_apply_oc

echo "[h-run] $MINER_DIR/arc-miner $ARC_ARGS"
exec ./arc-miner $ARC_ARGS
