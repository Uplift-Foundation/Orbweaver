#!/usr/bin/env bash
# Point the Paperclip service's git at an empty global config (CB-455, phase 0).
#
#   host/setup-git-config.sh
#
# Without GIT_CONFIG_GLOBAL the server's managed `git clone` for a git_repo
# workspace fails under the sandbox profile ("unable to access ~/.gitconfig:
# Operation not permitted"). The fix is NOT a profile read of ~/.gitconfig,
# which can carry credential helpers; it is an empty file under the instance
# directory that the service's environment names.
#
# Idempotent. It creates the empty file if absent, and if the launchd plist does
# not already set GIT_CONFIG_GLOBAL to it, adds the key with PlistBuddy and
# reloads the service (bootout then bootstrap: kickstart does not reread a
# plist). When the plist is already right it changes nothing and reloads
# nothing, which is the case on the mini today (QA applied it; backup
# launchagent.plist.bak-cb455).
#
# PAPERCLIP_DIR, SERVICE_PLIST and NO_RELOAD=1 can be set (the test suite does).
set -euo pipefail

DIR="${PAPERCLIP_DIR:-$HOME/.paperclip/instances/default}"
PLIST="${SERVICE_PLIST:-$HOME/Library/LaunchAgents/ing.paperclip.paperclipai.plist}"
EMPTY="$DIR/gitconfig"

mkdir -p "$DIR"
[ -e "$EMPTY" ] || : > "$EMPTY"
if [ -s "$EMPTY" ]; then
  echo "refusing: $EMPTY exists and is not empty" >&2
  exit 1
fi
echo "empty git config: $EMPTY"

current() {
  python3 - "$PLIST" <<'PY'
import re, sys
m = re.search(r"<key>GIT_CONFIG_GLOBAL</key>\s*<string>([^<]*)</string>", open(sys.argv[1]).read())
print(m.group(1) if m else "")
PY
}

[ -f "$PLIST" ] || { echo "no plist at $PLIST" >&2; exit 1; }
if [ "$(current)" = "$EMPTY" ]; then
  echo "ok: $PLIST already sets GIT_CONFIG_GLOBAL; nothing to do"
  exit 0
fi

PB=/usr/libexec/PlistBuddy
"$PB" -c "Print :EnvironmentVariables" "$PLIST" >/dev/null 2>&1 || "$PB" -c "Add :EnvironmentVariables dict" "$PLIST"
if "$PB" -c "Print :EnvironmentVariables:GIT_CONFIG_GLOBAL" "$PLIST" >/dev/null 2>&1; then
  "$PB" -c "Set :EnvironmentVariables:GIT_CONFIG_GLOBAL $EMPTY" "$PLIST"
else
  "$PB" -c "Add :EnvironmentVariables:GIT_CONFIG_GLOBAL string $EMPTY" "$PLIST"
fi
[ "$(current)" = "$EMPTY" ] || { echo "plist edit did not take" >&2; exit 1; }
echo "set GIT_CONFIG_GLOBAL in $PLIST"
if [ "${NO_RELOAD:-0}" = 1 ]; then
  echo "NO_RELOAD set: reload the service yourself (bootout, then bootstrap)"
else
  launchctl bootout "gui/$(id -u)" "$PLIST" || true
  launchctl bootstrap "gui/$(id -u)" "$PLIST"
  echo "service reloaded"
fi
