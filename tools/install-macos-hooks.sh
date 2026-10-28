#!/usr/bin/env bash
# Installs the Orbweaver hook into Claude Code's macOS settings.
#
# The bash twin of tools/install-windows-hooks.ps1, and the reason it exists is
# the same: the hook is what makes orbs appear, and an app with no hook wired up
# doesn't error, it just sits there showing nothing. Hand-merging JSON into a
# settings.json that already holds your model, permissions and status line is
# both fiddly and easy to get wrong, so do it mechanically.
#
#   install-macos-hooks.sh              # install / repair
#   install-macos-hooks.sh --uninstall  # remove just our entries
#
# Extra Claude Code accounts — a second one run as
# `CLAUDE_CONFIG_DIR=~/.claude-work claude` — are separate settings.json files
# and invisible to the default wiring. Every directory name saved in the app's
# own settings ("Claude Code profiles" in the Settings window) is wired too, in
# addition to ~/.claude and never instead of it. That mirrors what
# install-windows-hooks.ps1 has always done; the two had drifted, and the macOS
# side was the half that silently left a second account unwired.
#
# Safe to re-run: it strips any existing entries of ours before adding fresh
# ones, so it converges rather than accumulating duplicates.
#
# "Ours" is either hook filename: OrbweaverHook.sh, or the ClaudeBuddyHook.sh
# every install before the rename wrote (CB-255). Stripping both in one pass is
# what moves an upgraded machine off the old script without a separate
# migration step, and what keeps a re-run convergent either way. The old
# ~/.claude/claude-buddy folder is deliberately left on disk: a Claude Code
# session already running read its hooks at start and keeps calling the old
# path until it is restarted. The folder gets a .superseded marker instead,
# which is what the app's LegacyHookCleanup reads to retire it 14 days later.
#
# Runs from either the repo (tools/install-macos-hooks.sh, hook script one level
# up) or from inside the installed app bundle (Contents/Resources, hook script
# right alongside), so the shipped .app can offer hook setup without the user
# needing a clone.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
HOOK_DIR="$HOME/.claude/orbweaver"
SETTINGS="$HOME/.claude/settings.json"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --settings) SETTINGS="$2"; shift 2 ;;
    # One extra profile, explicitly. The saved list is used when this is absent.
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    # Don't recurse into the saved profiles. Set when this script re-invokes
    # itself for one of them, which is how each gets its own settings path
    # without the merge below having to loop.
    --no-profiles) NO_PROFILES=1; shift ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

HERE="$(cd "$(dirname "$0")" && pwd)"

# Alongside (installed app bundle) wins over one level up (repo checkout).
if [[ -f "$HERE/OrbweaverHook.sh" ]]; then
  SOURCE="$HERE/OrbweaverHook.sh"
elif [[ -f "$HERE/../OrbweaverHook.sh" ]]; then
  SOURCE="$HERE/../OrbweaverHook.sh"
else
  SOURCE=""
fi

INSTALLED="$HOOK_DIR/OrbweaverHook.sh"
# Where every install before the rename put the script: the sibling named
# claude-buddy, so a custom --hook-dir finds its own legacy twin.
LEGACY_HOOK_DIR="$(dirname "$HOOK_DIR")/claude-buddy"

if [[ $UNINSTALL -eq 0 ]]; then
  if [[ -z "$SOURCE" ]]; then
    echo "Can't find OrbweaverHook.sh next to $HERE or one level up." >&2
    exit 1
  fi
  mkdir -p "$HOOK_DIR"
  cp "$SOURCE" "$INSTALLED"
  chmod +x "$INSTALLED"
  echo "Hook installed: $INSTALLED"

  # Mark, never delete, and only when absent: refreshing the mtime on every
  # re-run (the Settings window reapplies hooks on every colour toggle) would
  # keep the 14-day clock from ever running out. Only where the legacy folder
  # exists, so a fresh machine never grows one.
  if [[ -d "$LEGACY_HOOK_DIR" && ! -e "$LEGACY_HOOK_DIR/.superseded" ]]; then
    : > "$LEGACY_HOOK_DIR/.superseded"
    echo "Marked $LEGACY_HOOK_DIR as superseded; Orbweaver retires it once nothing calls it."
  fi
fi

# The orbweaver-team skill (CB-291), into this account's own skills folder.
# Done per settings file, here, so each extra profile this script re-invokes
# itself for gets its copy by the same path the default account does. It is its
# own script because this one needs osascript and can only run on a Mac, and that
# copy is worth being able to test everywhere. A failure to find the skill is a
# warning, not an error: the hooks are what this script is for.
SKILL_INSTALLER="$HERE/install-skill.sh"
[[ -f "$SKILL_INSTALLER" ]] || SKILL_INSTALLER="$HERE/../tools/install-skill.sh"
if [[ -f "$SKILL_INSTALLER" ]]; then
  skill_mode=()
  [[ $UNINSTALL -eq 1 ]] && skill_mode=(--uninstall)
  bash "$SKILL_INSTALLER" --settings "$SETTINGS" "${skill_mode[@]+"${skill_mode[@]}"}" \
    || echo "warning: the orbweaver-team skill was not installed for $(dirname "$SETTINGS")." >&2
else
  echo "warning: can't find install-skill.sh next to $HERE; the team skill was not installed." >&2
fi

if [[ ! -f "$SETTINGS" ]]; then
  # An absent settings file is normal on a fresh install; start one rather than
  # refusing, but never invent anything beyond the hooks themselves.
  mkdir -p "$(dirname "$SETTINGS")"
  printf '{}' > "$SETTINGS"
  echo "Created $SETTINGS"
fi

BACKUP="$SETTINGS.orbweaver-backup"
cp "$SETTINGS" "$BACKUP"
echo "Backed up settings to $BACKUP"

# The JSON surgery runs in JavaScript for Automation. Stock macOS has no
# guaranteed jq (only 15+ ships one) and /usr/bin/python3 is a stub that
# prompts to install the Command Line Tools, but osascript has been present
# since 10.10 and brings a real JSON parser with it — so this works on a clean
# machine with nothing installed, which is the case that matters for an
# installer. Foundation is imported for the file read so UTF-8 is decoded
# exactly rather than through osascript's text coercion.
JXA=$(cat <<'JAVASCRIPT'
ObjC.import('Foundation');

function readUtf8(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? '' : ObjC.unwrap(s);
}

function run(argv) {
  const settingsPath = argv[0];
  const uninstall = argv[1] === 'uninstall';
  // The literal string $HOME, not this shell's expansion of it: hook commands
  // run through a shell, so keeping it unexpanded makes settings.json portable
  // across machines and usernames. This matches claude-hooks-snippet-macos.json.
  const script = '"$HOME/.claude/orbweaver/OrbweaverHook.sh"';

  const raw = readUtf8(settingsPath).trim();
  const settings = raw === '' ? {} : JSON.parse(raw);

  let hooks = (settings.hooks && typeof settings.hooks === 'object') ? settings.hooks : {};

  // Which Claude Code events drive which orb state. Notification carries a
  // matcher because only some notifications mean "Claude needs you"; the rest
  // would turn every orb amber, which would make amber meaningless.
  const wanted = [
    { event: 'SessionStart',     matcher: null,                  state: 'idle' },
    { event: 'UserPromptSubmit', matcher: null,                  state: 'generating' },
    { event: 'PreToolUse',       matcher: '.*',                  state: 'generating' },
    { event: 'Stop',             matcher: null,                  state: 'idle' },
    { event: 'SessionEnd',       matcher: null,                  state: 'ended' },
    { event: 'Notification',     matcher: 'permission_prompt',    state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_dialog',   state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_complete', state: 'generating' }
  ];

  // Strip our own entries wherever they appear, so re-running repairs rather
  // than duplicating and --uninstall leaves other tools' hooks untouched.
  // Either filename is ours, so the pre-rename ClaudeBuddyHook.sh entries go
  // in the same pass: that is the whole of the re-wire on an upgraded machine.
  for (const name of Object.keys(hooks)) {
    const groups = [].concat(hooks[name] || []);
    const kept = [];

    for (const group of groups) {
      if (!group || typeof group !== 'object') continue;
      const inner = [].concat(group.hooks || []).filter(function (h) {
        return h && typeof h.command === 'string' &&
               h.command.indexOf('ClaudeBuddyHook.') === -1 &&
               h.command.indexOf('OrbweaverHook.') === -1;
      });
      if (inner.length > 0) { group.hooks = inner; kept.push(group); }
    }

    if (kept.length > 0) { hooks[name] = kept; } else { delete hooks[name]; }
  }

  if (!uninstall) {
    for (const entry of wanted) {
      const group = { hooks: [{ type: 'command', command: 'bash ' + script + ' ' + entry.state }] };
      if (entry.matcher) { group.matcher = entry.matcher; }
      hooks[entry.event] = [].concat(hooks[entry.event] || []).concat([group]);
    }
  }

  settings.hooks = hooks;
  // An empty hooks object is noise in a settings file; drop the key entirely.
  if (Object.keys(settings.hooks).length === 0) { delete settings.hooks; }

  return JSON.stringify(settings, null, 2);
}
JAVASCRIPT
)

MODE=$([[ $UNINSTALL -eq 1 ]] && echo uninstall || echo install)

# Write via a temp file and mv, so an interrupted run can't leave a truncated
# settings.json behind — losing the user's model and permissions to a failed
# hook install would be a bad trade.
TMP="$(mktemp "${TMPDIR:-/tmp}/orbweaver-settings.XXXXXX")"
trap 'rm -f "$TMP"' EXIT

# No BOM and no trailing newline games: System.Text.Json, which both Claude Code
# and this app use, rejects a leading BOM as an invalid start of value.
osascript -l JavaScript -e "$JXA" "$SETTINGS" "$MODE" > "$TMP"

# Refuse to install anything that isn't valid JSON — better to keep the old file.
if ! osascript -l JavaScript -e 'ObjC.import("Foundation"); function run(a){ JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null))); return "ok" }' "$TMP" >/dev/null 2>&1; then
  echo "Refusing to write: generated settings.json did not parse. Left $SETTINGS untouched." >&2
  echo "Your backup is at $BACKUP" >&2
  exit 1
fi

mv "$TMP" "$SETTINGS"
trap - EXIT

if [[ $UNINSTALL -eq 1 ]]; then
  echo "Removed Orbweaver hooks from $SETTINGS."
  echo "The installed hook script was left in place; delete $HOOK_DIR (and $LEGACY_HOOK_DIR, if present) if you want it gone."
else
  echo "Wired 8 hook entries into $SETTINGS"
  echo
  echo "Restart any running Claude Code sessions — hooks are read at session start,"
  echo "so existing sessions will not produce orbs until they are restarted."
fi

# --- extra accounts -----------------------------------------------------------

# The app's environment overrides are ORBWEAVER_* since the rename, and the old
# CLAUDE_BUDDY_* spelling still works for a shell that exports it. A copy per
# script rather than a sourced file, because each script is installed on its own.
# CB-256: ORBWEAVER_<name>, falling back to the pre-rename CLAUDE_BUDDY_<name>.
brand_env() { local n="ORBWEAVER_$1" l="CLAUDE_BUDDY_$1"; printf '%s' "${!n:-${!l:-}}"; }

# The directory names saved in the app's own settings, one per line.
#
# Read with JXA for the reason the merge above uses it: a clean Mac has no jq,
# and /usr/bin/python3 is a stub that prompts to install the Command Line Tools.
# A missing or unreadable file is not an error — it just means no extra
# profiles, which is the common case.
#
# The path is the app's own settings directory, so ORBWEAVER_SETTINGS_DIR —
# which the app and install-hooks.sh both honour — moves it here too; without
# that a test instance pointed at a scratch directory wired the *real* saved
# list instead (CB-258). It does follow HOME when the variable is unset, which
# is why a scratch HOME needs its own copy of the file.
#
# A read that fails says so on stderr and carries on with no extra profiles,
# rather than the old `2>/dev/null`, which made "osascript could not read the
# list" indistinguishable from "nobody added a profile".
# With no override, Orbweaver's folder first, then the pre-rename ClaudeBuddy
# one: on upgrade day this runs before the new app has started and moved the
# folder, and reading only the new path then would quietly wire zero extra
# profiles. When ORBWEAVER_SETTINGS_DIR (or its old spelling) is set it is the
# only path read, so a test instance never falls through to the real list.
saved_profiles() {
  local settings override
  override="$(brand_env SETTINGS_DIR)"
  if [[ -n "$override" ]]; then
    settings="$override/settings.json"
  else
    local support="$HOME/Library/Application Support"
    settings="$support/Orbweaver/settings.json"
    [[ -f "$settings" ]] || settings="$support/ClaudeBuddy/settings.json"
  fi
  [[ -f "$settings" ]] || return 0

  osascript -l JavaScript -e '
    ObjC.import("Foundation");
    function run(a) {
      const s = $.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null);
      if (s.isNil()) return "";
      let parsed;
      try { parsed = JSON.parse(ObjC.unwrap(s)); } catch (e) { throw new Error("settings.json is not valid JSON"); }
      const dirs = parsed.claudeCodeProfileDirs;
      if (!Array.isArray(dirs)) return "";
      return dirs.filter(function (d) { return typeof d === "string" && d.length > 0; }).join("\n");
    }' "$settings" || {
    echo "warning: could not read the saved profile list from $settings (exit $?); no extra profiles were wired." >&2
    return 1
  }
}

LIST_UNREADABLE=0

if [[ $NO_PROFILES -eq 0 ]]; then
  profiles=()
  if [[ ${#EXTRA_PROFILES[@]} -gt 0 ]]; then
    profiles=("${EXTRA_PROFILES[@]}")
  else
    # Captured rather than read through process substitution, which would
    # throw the function's exit status away.
    saved=$(saved_profiles) || LIST_UNREADABLE=1
    while IFS= read -r line; do
      [[ -n "$line" ]] && profiles+=("$line")
    done <<< "$saved"
  fi

  for profile in "${profiles[@]+"${profiles[@]}"}"; do
    # A bare name under $HOME, the same shape the Settings window stores and
    # the Windows installer expects. Anything with a slash in it is refused
    # rather than resolved: CLAUDE_CONFIG_DIR pointing outside $HOME is a
    # different feature, and guessing at it would write hooks somewhere the
    # user did not ask for.
    if [[ "$profile" == */* ]]; then
      echo "Skipping profile '$profile': expected a directory name under \$HOME, not a path." >&2
      continue
    fi

    echo
    echo "--- profile: $profile"

    # Built as an array rather than with ${UNINSTALL:+--uninstall}, which is
    # wrong here and was: UNINSTALL is 0 or 1, and "0" is a non-empty string,
    # so :+ expanded it every time and an *install* quietly unwired every extra
    # profile it was supposed to be wiring. Caught only because the test looked
    # at the resulting file rather than at the exit code.
    mode=()
    [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

    # Re-invoked rather than looped internally, so each profile takes exactly
    # the same path through the merge as the default one — including the
    # backup, the JSON validation and the refusal to write anything that did
    # not parse. A loop would have been a second, less-tested code path.
    "$0" "${mode[@]+"${mode[@]}"}" --no-profiles \
         --settings "$HOME/$profile/settings.json" \
         --hook-dir "$HOOK_DIR"
  done
fi

# Exit 3 means "the saved profile list could not be read": the default profile was
# still wired, but any extra ones were not, and the app turns that code into a
# message of its own (HookInstaller.SavedListUnreadableExit). Exit 0 here used to
# make an unreadable list indistinguishable from an empty one (CB-258).
if [[ $LIST_UNREADABLE -eq 1 ]]; then
  exit 3
fi
