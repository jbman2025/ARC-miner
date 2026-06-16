# Contributing to ARC-miner

Thanks for your interest! ARC-miner is GPL-3.0; contributions are accepted under
the same license.

## Getting set up

1. Install the prerequisites for your backend (see the [README](README.md#build-from-source)).
2. Build: `./build.sh` (Linux) or `.\build.ps1` (Windows). For Intel Arc, add
   `BACKEND=sycl` / `-Backend sycl`.
3. Verify: `arc-miner selftest` (exit `0` = config, native libs, and pool
   reachability all OK — no GPU mining required).

## Guidelines

- **Match the surrounding code.** Keep the existing naming, comment density, and
  idioms in each file; don't reformat unrelated code.
- **Keep changes focused.** One logical change per PR; describe what and why.
- **Don't regress the hot path.** The mining loop and kernel are performance- and
  correctness-critical. If you touch share construction, the target comparison,
  or the kernel, validate that real shares are still **pool-accepted** (not just
  that it builds). The `AKOYA_SHARE_TRACE=1` diagnostic dumps a submitted share's
  difficulty math, which is handy for this.
- **No fees, no telemetry.** ARC-miner takes a 0% dev fee and phones home to
  nothing. PRs that add either will be declined.
- **Kernel/protocol changes** should preserve bit-exact, pool-accepted output.
  When in doubt, open an issue to discuss before a large change.

## Reporting issues

Include your GPU + driver version, OS, the exact command line, and the relevant
log lines (a `worker[…] hashrate=…` line and any `✗ share rejected reason=…`
lines are especially useful). For pool-specific problems, name the pool and port.

## Security

If you find a vulnerability, please report it privately to the maintainers rather
than opening a public issue.
