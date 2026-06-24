#!/bin/bash
# SessionStart hook: make Claude Code on the web sessions ready to build the
# KeithVision solution. Installs the .NET 10 SDK (the projects target net10.0)
# and compiles the solution so the session starts from a known-good state.
#
# Idempotent and non-interactive. Web-only: local machines are expected to
# already have the SDK, so we skip there.
set -euo pipefail

# Only run in the remote (ephemeral) container — Claude Code on the web.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DEBIAN_FRONTEND=noninteractive

# Install the .NET 10 SDK only if it isn't already present (the container caches
# its state after the hook completes, so this cost is paid once on a cold start).
# The Microsoft installer CDN is blocked by egress policy here, but Ubuntu's own
# archive.ubuntu.com universe repo ships dotnet-sdk-10.0 — use that.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "session-start: installing dotnet-sdk-10.0 from Ubuntu repos…"
  sudo apt-get update -y
  sudo apt-get install -y dotnet-sdk-10.0
fi

echo "session-start: dotnet $(dotnet --version)"

# Restore packages (the dependency step) — must succeed.
dotnet restore "$CLAUDE_PROJECT_DIR/KeithVision.slnx"

# Try to compile so the session starts with a verified build. Don't abort the
# hook on a compile error — report it but still let the session start.
if dotnet build "$CLAUDE_PROJECT_DIR/KeithVision.slnx" --no-restore -nologo; then
  echo "session-start: build succeeded"
else
  echo "session-start: build FAILED — see output above (session will still start)"
fi
