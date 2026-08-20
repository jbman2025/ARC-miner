#!/usr/bin/env bash
# ARC-miner — HiveOS stats scraper (dual prl+rx aware).
# Sets the two globals HiveOS reads after sourcing this file:
#   khs   -> total GPU/Pearl hashrate in kH/s (CPU-RandomX leg excluded;
#            that is reported separately by the arc-rx-shim pseudo-miner)
#   stats -> JSON string (hs[], hs_units, temp[], fan[], uptime, ar[], algo,
#            bus_numbers, ver)
#
# Source of truth is arc-miner's local JSON API: http://127.0.0.1:$PORT/api/stats
# In prl+rx dual mode the API lists the CPU as an extra .gpus[] entry whose name
# starts with "CPU" — we split that out so it is NOT rendered as a 3rd GPU box.
# Temps/fans/bus come from the Intel Arc xe/i915 hwmon sysfs, aligned to GPU order.

cd "$(dirname "$BASH_SOURCE")" 2>/dev/null || cd /hive/miners/custom/ARC-miner-BLUE
. h-manifest.conf 2>/dev/null
PORT="${CUSTOM_API_PORT:-4067}"

khs=0
stats=""

api=$(curl -fs --max-time 5 "http://127.0.0.1:${PORT}/api/stats" 2>/dev/null)
[[ -z "$api" ]] && return 0     # miner not up yet -> report offline (khs=0, empty stats)

# ---- split real GPUs from the CPU-RandomX pseudo-device --------------------
gpu_json=$(jq -c '[.gpus[]? | select((.name // "" | startswith("CPU")) | not)]' <<<"$api")
cpu_json=$(jq -c '[.gpus[]? | select( .name // "" | startswith("CPU"))]'        <<<"$api")

uptime=$(jq -r '.uptime_seconds // 0 | floor' <<<"$api")
ver=$(   jq -r '.version // "0"'              <<<"$api")

# ---- primary: GPU Pearl ----------------------------------------------------
# Per-GPU hashrate array, scaled to GH/s (divided by 10^9). CPU excluded.
mapfile -t hs_arr < <(jq -r '.[].hashrate_hs // 0' <<<"$gpu_json" | awk '{printf "%.6f\n", $1/1000000000}')
ngpu=${#hs_arr[@]}
(( ngpu == 0 )) && ngpu=1

# HiveOS khs is always kH/s. Primary = GPU total only (total_hs/1000).
gpu_total_hs=$(jq -r '[.[].hashrate_hs // 0] | add // 0' <<<"$gpu_json")
khs=$(awk -v v="$gpu_total_hs" 'BEGIN{printf "%.6f", v/1000}')

# Primary shares: sum the GPU entries only. The top-level .shares counters are
# rig-wide (they include the CPU-RandomX leg in prl+rx mode), so using them
# here would attribute RandomX shares to Pearl.
acc=$(jq -r '[.[].accepted // 0] | add // 0' <<<"$gpu_json")
rej=$(jq -r '[.[].rejected // 0] | add // 0' <<<"$gpu_json")

# NOTE: the CPU-RandomX leg ($cpu_json) is intentionally NOT reported here.
# The arc-rx-shim pseudo-miner (second flight-sheet slot) reads the same API
# and reports it as its own miner line, which HiveOS renders properly.

# ---- temps / fans / bus from Intel Arc sysfs ------------------------------
temp_arr=(); fan_arr=(); bus_arr=()
for dev in /sys/bus/pci/devices/*; do
  [[ -r "$dev/vendor" && -r "$dev/class" ]] || continue
  [[ "$(<"$dev/vendor")" == 0x8086 ]] || continue
  [[ "$(<"$dev/class")"  == 0x0300* ]] || continue     # VGA/display controller
  bdf=$(basename "$dev")
  bus_dec=$(( 16#${bdf:5:2} ))
  bus_arr+=("$bus_dec")
  # temp: first hwmon temp*_input under the device (millidegrees C)
  t=0
  for ti in "$dev"/hwmon/hwmon*/temp1_input "$dev"/hwmon/hwmon*/temp2_input; do
    [[ -r "$ti" ]] || continue
    t=$(awk -v m="$(<"$ti")" 'BEGIN{printf "%d", m/1000}')
    break
  done
  temp_arr+=("$t")
  # fan: raw RPM -> percentage (1800 RPM = 100%, matching intel-info)
  f=0
  for fi in "$dev"/hwmon/hwmon*/fan1_input; do
    if [[ -r "$fi" ]]; then
      rpm=$(<"$fi")
      f=$(( rpm * 100 / 1800 ))
      (( f > 100 )) && f=100
      break
    fi
  done
  fan_arr+=("$f")
done

# Pad telemetry arrays to match the GPU count the miner reported.
while (( ${#temp_arr[@]} < ngpu )); do temp_arr+=(0); done
while (( ${#fan_arr[@]}  < ngpu )); do fan_arr+=(0);  done
while (( ${#bus_arr[@]}  < ngpu )); do bus_arr+=(0);  done

# ---- assemble the HiveOS stats JSON ---------------------------------------
hs_json=$(printf '%s\n'   "${hs_arr[@]:0:$ngpu}" | jq -cs '.')
temp_json=$(printf '%s\n' "${temp_arr[@]:0:$ngpu}" | jq -cs '.')
fan_json=$(printf '%s\n'  "${fan_arr[@]:0:$ngpu}"  | jq -cs '.')
bus_json=$(printf '%s\n'  "${bus_arr[@]:0:$ngpu}"  | jq -cs '.')

stats=$(jq -nc \
  --argjson hs   "$hs_json" \
  --argjson temp "$temp_json" \
  --argjson fan  "$fan_json" \
  --argjson bus  "$bus_json" \
  --argjson uptime "${uptime:-0}" \
  --argjson acc "${acc:-0}" \
  --argjson rej "${rej:-0}" \
  --arg ver  "$ver" \
  '{hs:$hs, hs_units:"ghs", temp:$temp, fan:$fan, uptime:$uptime,
    ar:[$acc,$rej], algo:"prl", bus_numbers:$bus, ver:$ver}')
