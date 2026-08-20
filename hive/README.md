# ARC-miner — HiveOS custom miner

Native HiveOS integration for `arc-miner` (Intel Arc B-series). Wraps the Linux
build in the HiveOS custom-miner contract so it installs, runs, and reports to the
dashboard like a first-class miner.

## Files
| File | Role |
|------|------|
| `h-manifest.conf` | Package name/version + API port (`4067`). `CUSTOM_VERSION` is stamped from `version.txt` by `package.sh`. |
| `h-config.sh` | Flight-sheet fields → `arc-miner.conf` (`--algo/--pool/--wallet/--worker`), plus `ARC_SELECTOR` from the `ADAPTER=` token. |
| `h-run.sh` | Launcher: `LD_LIBRARY_PATH`, device selector (OpenCL by default), `ARC_API_RESTART_MODE=exit`, `exec arc-miner`. |
| `h-stats.sh` | Scrapes `/api/stats` → HiveOS `khs` + `stats` JSON; temps/bus from Intel Arc `xe` hwmon sysfs. |
| `package.sh` | Builds `arc-miner-<ver>.tar.gz` from `out-linux/` + these scripts. |

## Build the package
```bash
./hive/package.sh                 # bundles out-linux/ (prl/csd/rx build)
BIN_DIR=out-btx-linux ./hive/package.sh   # BTX build
```
Produces `arc-miner-<ver>.tar.gz`, where `<ver>` is the contents of `version.txt`.

`package.sh` refuses to build a tarball whose `BIN_DIR` is missing the bundled
Intel oneAPI runtime — `build.sh` does not stage it and the WSL copy-back is
`rsync --delete`, so a fresh build leaves `out-linux/` with only our own `.so`s,
and a rig without oneAPI would fail at `dlopen` with
`UR adapter initialization failed: 43`.

## Install on a rig
```bash
cd /hive/miners/custom && tar xzf arc-miner-<ver>.tar.gz
```
or host the tarball and paste its URL into the flight sheet's **Installation URL**.

## Flight sheet
- **Miner**: `Custom` → **Miner name** `arc-miner`
- **Installation URL**: URL to the tarball (or pre-install manually as above)
- **Hash algorithm**: `prl` (default), `csd`, `rx`, `gr`, `nm`, or dual e.g. `prl+rx`
- **Wallet and worker template**: your wallet; optional `.worker` suffix (else the rig's `WORKER_NAME` is used)
- **Pool URL**: `stratum+tls://host:port` (a bare `host:port` is assumed TLS stratum)
- **Extra config arguments**: appended verbatim to the arc-miner command line, except
  an `ADAPTER=opencl|level_zero` token, which is lifted out and becomes the SYCL device
  selector (`ONEAPI_DEVICE_SELECTOR`) instead of being passed to the miner as an arg.

## Notes / known gaps
- **Device selector**: OpenCL by default — every box has an OpenCL ICD, whereas Level
  Zero needs `libze` plus render-group access. Set `ADAPTER=level_zero` in *Extra config
  arguments* to switch; `h-run.sh` echoes the resolved selector at startup. (Before
  0.3.1 this token was parsed and then discarded, so it never did anything.)
- **Temps & bus**: read from Intel Arc (`vendor 0x8086`, display-class) hwmon sysfs.
  Order is sysfs PCI order — matches the miner's Level-Zero order on single-GPU and
  homogeneous Arc rigs. Mixed-GPU rigs may need explicit ordering.
- **Fans**: Arc dGPU fan RPM is rarely exposed via hwmon; reported as `0` when absent.
- **Restart**: runs with `ARC_API_RESTART_MODE=exit` so the HiveOS agent respawns on
  fatal fault instead of the miner self-relaunching inside the screen session.
