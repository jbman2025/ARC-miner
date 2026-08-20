#!/usr/bin/env bash
# Build the HiveOS custom-miner tarball: arc-miner-<ver>.tar.gz
# Layout inside the tarball (HiveOS unpacks into /hive/miners/custom/):
#   arc-miner/
#     arc-miner                (binary)
#     lib*.so                  (native deps)
#     h-manifest.conf h-config.sh h-run.sh h-stats.sh
#
# Usage:  ./hive/package.sh            (uses ../out-linux as the binary source)
#         BIN_DIR=../out-btx-linux ./hive/package.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
BIN_DIR="${BIN_DIR:-$ROOT/out-linux}"
NAME=arc-miner
VER="$(cat "$ROOT/version.txt" 2>/dev/null | tr -d '[:space:]')"
VER="${VER:-0.0.0}"

# The bundle must be SELF-CONTAINED: a HiveOS rig is not guaranteed to have
# oneAPI installed, and the SYCL .so's fail to dlopen without the Intel runtime
# ("UR adapter initialization failed: 43"). build.sh does NOT stage that runtime
# and build-linux-wsl.sh's copy-back is `rsync --delete`, so a fresh build leaves
# $BIN_DIR with only our own libs. Fail loudly here rather than ship a tarball
# that dies on the rig — the symptom on a rig looks like a driver problem.
missing=()
for lib in libsycl.so libur_loader.so libsvml.so libimf.so libintlc.so; do
  compgen -G "$BIN_DIR/$lib*" >/dev/null || missing+=( "$lib" )
done
if (( ${#missing[@]} )); then
  echo "ERROR: $BIN_DIR is missing the Intel oneAPI runtime: ${missing[*]}" >&2
  echo "       Copy it in from a known-good bundle, e.g.:" >&2
  echo "         cp -n $ROOT/out-linux-clean/lib{sycl,ur_*,svml,imf,intlc,irng,umf,hwloc}* $BIN_DIR/" >&2
  echo "       (or set BIN_DIR to a folder that already has it)." >&2
  exit 1
fi

stage="$(mktemp -d)"
dest="$stage/$NAME"
mkdir -p "$dest"

# 1. miner binary + native libs + the bundled Intel runtime
cp -v "$BIN_DIR/$NAME" "$dest/"
cp -v "$BIN_DIR"/*.so* "$dest/" 2>/dev/null || true

# 2. HiveOS integration scripts (+ install-deps.sh as the recovery path if a rig
#    ever does need the system GPU stack refreshed).
cp -v "$HERE"/h-manifest.conf "$HERE"/h-config.sh "$HERE"/h-run.sh "$HERE"/h-stats.sh "$HERE"/rebar-check.sh "$HERE"/oc.conf "$dest/"
cp -v "$ROOT"/install-deps.sh "$dest/" 2>/dev/null || true

# 3. Stamp the manifest version from version.txt so CUSTOM_VERSION can never
#    drift from the tarball name (it sat at 0.3.0 through the 0.3.1 bump).
sed -i -E "s/^CUSTOM_VERSION=.*/CUSTOM_VERSION=$VER/" "$dest/h-manifest.conf"

chmod +x "$dest"/h-*.sh "$dest/$NAME"
# Not `[ -f ] && chmod` — under `set -e` a false test would abort the package.
if [ -f "$dest/install-deps.sh" ]; then chmod +x "$dest/install-deps.sh"; fi

out="$ROOT/$NAME-$VER.tar.gz"
tar -C "$stage" -czf "$out" "$NAME"
rm -rf "$stage"
echo
echo "built: $out"
echo "install on a rig with:"
echo "  cd /hive/miners/custom && tar xzf $NAME-$VER.tar.gz"
echo "or host it and paste the URL into the flight sheet's 'Installation URL'."
