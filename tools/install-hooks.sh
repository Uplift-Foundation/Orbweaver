#!/usr/bin/env bash
# Wires Orbweaver into every agent CLI on this machine.
#
# The one thing an install should run. Orbs appear because a CLI calls the hook,
# so an app with no hooks wired doesn't error — it sits there showing nothing —
# and asking someone to know which of two installers to run for which CLI is a
# way of arranging for that to happen.
#
#   install-hooks.sh              # install / repair everything found
#   install-hooks.sh --uninstall  # remove just our entries, everywhere
#
# Both sub-installers converge rather than duplicating, so re-running is how you
# repair a broken setup, and how you pick up a CLI you installed later.
#
# A CLI that isn't here is skipped and said so, not treated as a failure. Most
# people have one of the two, and "Codex: not installed" is information; an
# error would be a lie.
#
# CB-49: this script is also where the crash keep-alive LaunchAgent gets
# written and torn down, on macOS only. It has nothing to do with hooks, but
# it belongs here rather than in its own script or in a Windows-style
# installer step, because this is the one thing every macOS install path
# already runs: build-macos-app.sh's --install flag and the DMG's
# "Install Hooks.command" both exec this file (see either one's header), and
# a bare `.app` dragged out of a DMG has no other installer step to hang this
# on. Windows takes a different route — see tools/Orbweaver.iss — because
# it already has a real installer with a real install/uninstall lifecycle,
# which is a better home for this than install-hooks.ps1's own repair-anytime
# entry point.
#
#   install-hooks.sh --print-keepalive-plist <exe-path>  # print, don't write
#   install-hooks.sh --keepalive-only                    # reconcile only this
#
# Gated behind the existing "Serve on launch" (Remote Control) setting rather
# than on by default: KeepAlive relaunches the app after *any* exit, including
# a deliberate Quit from the menu bar, which is exactly the "app that will not
# stay quit" behaviour nobody wants on a laptop. A machine already told to
# serve is a machine someone wants to stay up, so that setting is what turns
# this on. The LaunchAgent itself narrows this further with
# KeepAlive/SuccessfulExit=false, so launchd only restarts it after a crash
# (nonzero exit) and not after App.axaml.cs's normal Shutdown() (exit 0) —
# belt and suspenders, since the setting alone can't tell "you quit it" from
# "it died" and the plist can.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

# --- CB-49 crash keep-alive (macOS LaunchAgent) -----------------------------
#
# Must match build-macos-app.sh's BUNDLE_ID -- launchd keys an agent by this
# Label/filename, so a mismatch here would leave two agents rather than
# reconciling one.
KEEPALIVE_BUNDLE_ID="io.github.wtvamp.claudebuddy"
KEEPALIVE_THROTTLE_SECONDS=60

# CB-256: ORBWEAVER_<name>, falling back to the pre-rename CLAUDE_BUDDY_<name>.
brand_env() { local n="ORBWEAVER_$1" l="CLAUDE_BUDDY_$1"; printf '%s' "${!n:-${!l:-}}"; }

# Test seams, same pattern as ORBWEAVER_SETTINGS_DIR elsewhere in this repo:
# without them, exercising this from tests/IntegrationTests would mean writing
# into a developer's real ~/Library/LaunchAgents and calling real launchctl.
# Each is read through brand_env, so the CLAUDE_BUDDY_ spelling a developer
# may still have exported keeps working, and the new one wins when both are.
keepalive_launchagents_dir() {
    local dir
    dir="$(brand_env LAUNCHAGENTS_DIR)"
    printf '%s' "${dir:-$HOME/Library/LaunchAgents}"
}

# Whether launchctl is to be left alone (the tests' dry run).
keepalive_dry_run() { [[ "$(brand_env KEEPALIVE_DRY_RUN)" == 1 ]]; }

# The settings.json the app will read. CB-255 moved the data folder from
# Application Support/ClaudeBuddy to Application Support/Orbweaver, and the app
# moves it on its first launch — so on upgrade day this script can run before
# that launch ever has, with the user's settings still under the old name.
# Reading only the new path then would read "no settings", take Serve on
# launch as off, and tear down a keep-alive the user had opted in to. So: the
# new folder's file if it exists (it wins, as it does in the app's own
# migration), the legacy folder's otherwise. The override, when set, is the
# whole answer — it is a test seam and must never fall through to a real
# folder.
keepalive_settings_path() {
    local override
    override="$(brand_env SETTINGS_DIR)"
    if [[ -n "$override" ]]; then
        printf '%s' "$override/settings.json"
        return
    fi
    local support="$HOME/Library/Application Support"
    if [[ ! -f "$support/Orbweaver/settings.json" && -f "$support/ClaudeBuddy/settings.json" ]]; then
        printf '%s' "$support/ClaudeBuddy/settings.json"
    else
        printf '%s' "$support/Orbweaver/settings.json"
    fi
}

# Colon-separated .app bundles to look for an installed executable in,
# checked in order. Overridable so tests can point this at a fabricated
# bundle (or a directory guaranteed not to exist) instead of depending on
# whatever happens to be installed on the machine running the suite.
#
# Orbweaver.app first, then the pre-CB-255 Claude Buddy.app, so a machine
# that has not been upgraded yet still gets its keep-alive — and one that has
# both (a DMG drag that left the old bundle behind) points launchd at the new
# one.
keepalive_app_candidates() {
    local candidates
    candidates="$(brand_env KEEPALIVE_APP_CANDIDATES)"
    printf '%s' "${candidates:-/Applications/Orbweaver.app:$HOME/Applications/Orbweaver.app:/Applications/Claude Buddy.app:$HOME/Applications/Claude Buddy.app}"
}

# True (via grep's exit code) only when settings.json exists and its
# remoteControlServeOnLaunch key is true. A missing file (fresh install, no
# settings written yet) or an explicit false both read as "not enabled" --
# the same value the app itself would read via OrbweaverSettings.
serve_on_launch_enabled() {
    local settings
    settings="$(keepalive_settings_path)"
    [[ -f "$settings" ]] || return 1
    grep -Eo '"remoteControlServeOnLaunch"[[:space:]]*:[[:space:]]*true' "$settings" >/dev/null 2>&1
}

# Finds the executable this LaunchAgent should point at. Prefers the bundle
# this very script is already running from -- build-macos-app.sh copies this
# script to Contents/Resources, so a script running from there is running
# inside the exact app it should keep alive, which beats guessing at
# /Applications and matches when someone has the app installed somewhere
# unusual. Falls back to the well-known install locations otherwise (the
# case when this runs from tools/ in a repo checkout, e.g. a manual
# `install-hooks.sh --keepalive-only`).
#
# CB-256 renamed the executable from ClaudeBuddy to Orbweaver. The bundled arm
# only ever names the new one: a copy of this script sits in the bundle it
# ships with, which is a phase-3 bundle by construction. The candidate loop
# tries both per bundle, new name first, because a checkout's copy of this
# script can meet an installed phase-2 Orbweaver.app (Contents/MacOS/ClaudeBuddy)
# or a pre-CB-255 Claude Buddy.app, and finding nothing there would skip a
# keep-alive the user opted in to.
resolve_app_executable() {
    if [[ "$(basename "$HERE")" == "Resources" && "$(basename "$(dirname "$HERE")")" == "Contents" ]]; then
        local bundled_exe="$(dirname "$HERE")/MacOS/Orbweaver"
        [[ -x "$bundled_exe" ]] && { printf '%s' "$bundled_exe"; return 0; }
    fi

    local IFS=':'
    local app name
    for app in $(keepalive_app_candidates); do
        for name in Orbweaver ClaudeBuddy; do
            local exe="$app/Contents/MacOS/$name"
            [[ -x "$exe" ]] && { printf '%s' "$exe"; return 0; }
        done
    done
    return 1
}

# Pure -- no filesystem or launchctl side effects -- so `--print-keepalive-plist`
# can be asserted on directly in tests without touching this machine's real
# LaunchAgents.
keepalive_plist_content() {
    local exe="$1"
    cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$KEEPALIVE_BUNDLE_ID</string>
    <key>ProgramArguments</key>
    <array>
        <string>$exe</string>
    </array>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>ThrottleInterval</key>
    <integer>$KEEPALIVE_THROTTLE_SECONDS</integer>
    <key>ProcessType</key>
    <string>Interactive</string>
    <!-- CB-308: without this, launchd kills every process still in the app's
         process group when the app exits, which would take the updater's
         finisher down with the app it is replacing. -->
    <key>AbandonProcessGroup</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$HOME/Library/Logs/Orbweaver/keepalive.log</string>
    <key>StandardErrorPath</key>
    <string>$HOME/Library/Logs/Orbweaver/keepalive.log</string>
</dict>
</plist>
PLIST
}

remove_keepalive_agent() {
    local plist="$1"
    [[ -f "$plist" ]] || return 0
    if ! keepalive_dry_run; then
        launchctl unload "$plist" >/dev/null 2>&1 || true
    fi
    rm -f "$plist"
    echo "=== Crash keep-alive: removed $plist"
}

# The single entry point: writes/loads, removes, or leaves alone, whichever
# the current setting (or an in-progress uninstall) calls for. Never fatal to
# the caller -- a keep-alive agent is a floor under the app, not the app
# itself, so a failure here is reported and swallowed rather than turned into
# a failed hook install.
reconcile_keepalive() {
    local uninstalling="$1"
    local dir plist
    dir="$(keepalive_launchagents_dir)"
    plist="$dir/$KEEPALIVE_BUNDLE_ID.plist"

    if [[ "$uninstalling" -eq 1 ]]; then
        remove_keepalive_agent "$plist"
        return 0
    fi

    if ! serve_on_launch_enabled; then
        # Not opted in -- see the header comment for why this stays off by
        # default. Still idempotent: a stale agent from a previous opt-in
        # that has since been turned off comes out here too, rather than
        # being left running against the setting's current value.
        remove_keepalive_agent "$plist"
        return 0
    fi

    local exe
    if ! exe="$(resolve_app_executable)"; then
        echo "=== Crash keep-alive: couldn't find an installed Orbweaver.app, skipping."
        return 0
    fi

    mkdir -p "$dir"
    keepalive_plist_content "$exe" > "$plist"

    if keepalive_dry_run; then
        echo "=== Crash keep-alive: wrote $plist (dry run, not loaded)"
        return 0
    fi

    launchctl unload "$plist" >/dev/null 2>&1 || true
    if launchctl load "$plist" >/dev/null 2>&1; then
        echo "=== Crash keep-alive: registered ($plist)"
    else
        echo "=== Crash keep-alive: wrote $plist but launchctl load failed" >&2
    fi
}

# Only the flag both sub-installers understand is accepted, and it is the only
# one forwarded. Passing everything through looked tidier and was wrong: the
# Codex installer takes --codex-home, the Claude Code one takes --settings, and
# forwarding either to the other makes it exit 2 in the middle of a run that has
# already changed something. An entry point whose failure mode is a half-done
# install is worse than one that doesn't take the option.
#
# Anything more specific than --uninstall is a job for the sub-installer
# directly, which is what they are still there for.
FORWARD=()
UNINSTALL=0
KEEPALIVE_ONLY=0
PRINT_PLIST_EXE=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) FORWARD+=(--uninstall); UNINSTALL=1; shift ;;
    # CB-49: --keepalive-only skips hook wiring entirely and just reconciles
    # the LaunchAgent, which is what build-macos-app.sh --install calls after
    # copying the bundle to /Applications -- running the full hook wiring
    # again there would be surprising for a step that's only supposed to
    # register a keep-alive.
    --keepalive-only) KEEPALIVE_ONLY=1; shift ;;
    # Prints the plist this script would write, for a given executable path,
    # and exits -- no filesystem write, no launchctl call. This is the seam
    # tests/IntegrationTests uses to check the generated XML's shape without
    # touching a real LaunchAgents directory.
    --print-keepalive-plist) PRINT_PLIST_EXE="${2:?--print-keepalive-plist needs an executable path}"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *)
      echo "unknown option: $1" >&2
      echo "This wires every agent CLI it finds. It takes --uninstall and nothing else;" >&2
      echo "for per-CLI options run tools/install-macos-hooks.sh or" >&2
      echo "tools/install-codex-hooks.sh directly." >&2
      exit 2
      ;;
  esac
done

if [[ -n "$PRINT_PLIST_EXE" ]]; then
    keepalive_plist_content "$PRINT_PLIST_EXE"
    exit 0
fi

if [[ $KEEPALIVE_ONLY -eq 1 ]]; then
    reconcile_keepalive "$UNINSTALL"
    exit 0
fi

# Alongside (installed app bundle) wins over the repo's tools/ directory, the
# same resolution both sub-installers use for the hook script itself.
find_installer() {
    for candidate in "$HERE/$1" "$HERE/tools/$1"; do
        [[ -x "$candidate" ]] && { printf '%s' "$candidate"; return 0; }
    done
    return 1
}

have_claude_code() {
    [[ -d "$HOME/.claude" ]] || command -v claude >/dev/null 2>&1
}

have_codex() {
    [[ -d "${CODEX_HOME:-$HOME/.codex}" ]] || command -v codex >/dev/null 2>&1
}

have_grok() {
    [[ -d "${GROK_HOME:-$HOME/.grok}" ]] || command -v grok >/dev/null 2>&1
}

wired=0
skipped=()
failed=()

run_one() {
    local label="$1" script="$2"
    local path
    if ! path="$(find_installer "$script")"; then
        failed+=("$label (couldn't find $script)")
        return
    fi

    echo "=== $label"
    if "$path" "${FORWARD[@]+"${FORWARD[@]}"}"; then
        wired=$((wired + 1))
    else
        failed+=("$label")
    fi
    echo
}

if have_claude_code; then
    run_one "Claude Code" install-macos-hooks.sh
else
    skipped+=("Claude Code")
fi

if have_codex; then
    run_one "Codex" install-codex-hooks.sh
else
    skipped+=("Codex")
fi

if have_grok; then
    run_one "Grok Build" install-grok-hooks.sh
else
    skipped+=("Grok Build")
fi

for one in "${skipped[@]+"${skipped[@]}"}"; do
    echo "=== $one: not installed on this machine, nothing to wire."
    echo "    Install it and run this again — that is all it takes."
    echo
done

# Independent of whether hook wiring above succeeded -- a hook failure and a
# keep-alive registration are unrelated outcomes, and one shouldn't be
# skipped because of the other.
reconcile_keepalive "$UNINSTALL"

if [[ ${#failed[@]} -gt 0 ]]; then
    echo "Finished with problems:"
    for one in "${failed[@]}"; do echo "  - $one"; done
    exit 1
fi

if [[ $wired -eq 0 ]]; then
    echo "Neither Claude Code, Codex, nor Grok Build was found, so nothing was wired."
    echo "Orbweaver will show no orbs until one of them is installed."
    exit 0
fi

echo "Done."
