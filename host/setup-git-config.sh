#!/usr/bin/env bash
# Point the Paperclip service's git at an empty global config (CB-455, phase 0).
#
#   host/setup-git-config.sh
#
# The org's agents run git as the service. With no GIT_CONFIG_GLOBAL, git reads
# the logged-in user's ~/.gitconfig, which carries credential helpers and
# identity that the bug-fix org must not inherit. Phase 0 found the fix: the
# service environment gets GIT_CONFIG_GLOBAL pointing at an empty file under its
# own instance directory.
#
# This script creates that empty file (idempotent) and CHECKS that the service's
# launchd plist names it; it does not edit the plist, because changing a live
# service's plist needs bootout/bootstrap and is the lead's step. If the check
# fails it prints the commands.
#
# PAPERCLIP_DIR and SERVICE_PLIST can be overridden (the test suite does).
set -euo pipefail

DIR="${PAPERCLIP_DIR:-$HOME/.paperclip/instances/default}"
PLIST="${SERVICE_PLIST:-$HOME/Library/LaunchAgents/ing.paperclip.paperclipai.plist}"
EMPTY="$DIR/empty-gitconfig"

mkdir -p "$DIR"
: > "$EMPTY"
chmod 644 "$EMPTY"
echo "empty git config: $EMPTY"

if [ -f "$PLIST" ] && python3 - "$PLIST" "$EMPTY" <<'PY'
import re, sys
text = open(sys.argv[1]).read()
m = re.search(r"<key>GIT_CONFIG_GLOBAL</key>\s*<string>([^<]*)</string>", text)
sys.exit(0 if m and m.group(1) == sys.argv[2] else 1)
PY
then
  echo "ok: $PLIST sets GIT_CONFIG_GLOBAL to it"
else
  {
    echo "GIT_CONFIG_GLOBAL in $PLIST is missing or points elsewhere. Add it, then reload:"
    echo "  /usr/libexec/PlistBuddy -c 'Add :EnvironmentVariables:GIT_CONFIG_GLOBAL string $EMPTY' '$PLIST'"
    echo "  launchctl bootout gui/\$(id -u) '$PLIST'; launchctl bootstrap gui/\$(id -u) '$PLIST'"
  } >&2
  exit 1
fi
