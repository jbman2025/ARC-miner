#!/usr/bin/env bash
# rebar-check.sh — non-blocking Resizable BAR preflight for Intel Arc.
#
# Arc needs a VRAM-sized prefetchable BAR to perform. On older platforms (X99,
# etc.) that requires a UEFI ReBAR patch + "Above 4G Decoding" enabled. Without
# it the card exposes only a 256 MB stub BAR and hashrate collapses (or the GPU
# won't init). This warns; it never blocks — a correct BIOS is the user's job.
#
# Usage:  source rebar-check.sh && arc_rebar_check
# Returns 0 always; prints a warning per Arc GPU whose largest BAR < ~1 GB.

arc_rebar_check() {
  local min_bytes=$((1024*1024*1024))   # 1 GiB threshold (stub is 256 MiB)
  local warned=0 dev bdf vend cls big line start end sz

  for dev in /sys/bus/pci/devices/*; do
    [[ -r "$dev/vendor" && -r "$dev/class" && -r "$dev/resource" ]] || continue
    [[ "$(<"$dev/vendor")" == 0x8086 ]] || continue          # Intel
    [[ "$(<"$dev/class")"  == 0x0300* ]] || continue          # display controller
    bdf=$(basename "$dev")

    # Largest BAR region from the sysfs 'resource' table (start end flags per line).
    big=0
    while read -r start end _; do
      [[ "$start" =~ ^0x ]] || continue
      (( start == 0 && end == 0 )) && continue
      sz=$(( end - start + 1 ))
      (( sz > big )) && big=$sz
    done < "$dev/resource"

    if (( big < min_bytes )); then
      local gib; gib=$(awk -v b="$big" 'BEGIN{printf "%.0f", b/1048576}')
      echo "[rebar] WARNING: Arc GPU $bdf largest BAR is ${gib} MiB (< 1 GiB)." >&2
      echo "[rebar]   Resizable BAR looks DISABLED — expect severe hashrate loss." >&2
      echo "[rebar]   Enable 'Above 4G Decoding' + 'Resizable BAR' in UEFI" >&2
      echo "[rebar]   (older boards may need a ReBarUEFI firmware patch first)." >&2
      warned=1
    else
      local gib; gib=$(awk -v b="$big" 'BEGIN{printf "%.1f", b/1073741824}')
      echo "[rebar] OK: Arc GPU $bdf BAR ${gib} GiB — Resizable BAR active." >&2
    fi
  done
  return 0
}
