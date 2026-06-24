#!/bin/bash
#
# protoc-arm64-wrapper.sh — work around a protoc segfault on aarch64 (ARM64)
# during the .NET / Native-AOT build of the miner.
#
# Problem
#   The protoc bundled in Grpc.Tools (tools/linux_arm64/protoc) segfaults
#   (exit 139) on aarch64 when it is given its arguments via an MSBuild
#   response file, i.e. invoked as `protoc @/tmp/MSBuild*.rsp`. The exact
#   same arguments passed directly on the command line work fine — only the
#   `@responsefile` code path crashes. This breaks `dotnet publish` /
#   `dotnet build` of the gRPC/proto project on ARM64 hosts (e.g. NVIDIA
#   DGX Spark / GB10, Ampere Altra, Graviton, Apple-silicon Linux VMs).
#
# Fix
#   Drop in this wrapper as `protoc`, with the real binary renamed to
#   `protoc.real` next to it. The wrapper expands any `@file` argument into
#   direct argv and execs the real protoc, sidestepping the crashing path.
#
# Install (run once; re-run if `dotnet restore` re-extracts Grpc.Tools)
#   T="$(dirname "$(find "$HOME/.nuget/packages/grpc.tools" \
#         -path '*/tools/linux_arm64/protoc' | sort -V | tail -1)")"
#   [ -f "$T/protoc.real" ] || cp "$T/protoc" "$T/protoc.real"
#   cp scripts/protoc-arm64-wrapper.sh "$T/protoc"
#   chmod +x "$T/protoc"
#
# The wrapper resolves the real binary relative to its own location, so it
# carries no host-specific paths.

set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REAL="$HERE/protoc.real"

if [ ! -x "$REAL" ]; then
  echo "protoc-arm64-wrapper: real binary not found at $REAL" >&2
  echo "  (rename the original protoc to protoc.real next to this wrapper)" >&2
  exit 127
fi

args=()
for a in "$@"; do
  if [ "${a#@}" != "$a" ]; then
    # @responsefile — expand one argument per non-empty line.
    rsp="${a#@}"
    while IFS= read -r line || [ -n "$line" ]; do
      [ -z "$line" ] && continue
      args+=("$line")
    done < "$rsp"
  else
    args+=("$a")
  fi
done

exec "$REAL" "${args[@]}"
