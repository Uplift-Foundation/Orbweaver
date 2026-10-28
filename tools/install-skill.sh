#!/usr/bin/env bash
# Copies the orbweaver-team skill into one Claude Code account's config
# directory, or takes it out again.
#
#   install-skill.sh --settings ~/.claude/settings.json     # <dir>/skills/orbweaver-team
#   install-skill.sh --config-dir ~/.claude-work
#   install-skill.sh --settings ~/.claude/settings.json --uninstall
#
# Each Claude Code account reads only its own config directory, which is why the
# hook is wired into every account's settings.json and why this is copied into
# every account's skills folder. install-macos-hooks.sh calls it once per
# settings file it wires (the default account and each saved profile), so the
# extra accounts get their copy by exactly the path the default one does.
#
# Kept apart from that script because that one needs osascript to merge JSON and
# can only run on a Mac; this is plain bash, so it can be tested on every
# platform it ships on.
#
# The skill is shipped code, not the user's files: their personas, roles and
# teams live in their pool or their project, never in here. So an install
# replaces the folder outright, which also drops anything a previous version
# shipped that this one no longer does, and --uninstall removes that one folder
# and nothing beside it. That means a folder of your own that happens to be
# named skills/orbweaver-team is replaced too: the name is ours.
set -u

SETTINGS=""
CONFIG_DIR=""
SOURCE=""
UNINSTALL=0

usage() {
  echo "usage: install-skill.sh (--settings <settings.json> | --config-dir <dir>) [--source <dir>] [--uninstall]" >&2
}

# An option that takes a value and is not given one is a usage error, not an
# "unbound variable" from under `set -u`.
need_value() {
  if [[ $1 -lt 2 ]]; then usage; exit 2; fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --settings) need_value $#; SETTINGS="$2"; shift 2 ;;
    --config-dir) need_value $#; CONFIG_DIR="$2"; shift 2 ;;
    --source) need_value $#; SOURCE="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$CONFIG_DIR" && -n "$SETTINGS" ]]; then CONFIG_DIR="$(dirname "$SETTINGS")"; fi
if [[ -z "$CONFIG_DIR" ]]; then usage; exit 2; fi

# Absolute before anything is deleted. A relative `--settings settings.json` has a
# config dir of `.`, so the target is `./skills/orbweaver-team` -- which, run from
# a checkout's root, is the skill's own source. The directory may not exist yet on
# an install, so only an existing one is canonicalised; for an uninstall a missing
# one means there is nothing to remove.
if [[ -d "$CONFIG_DIR" ]]; then
  CONFIG_DIR="$(cd "$CONFIG_DIR" && pwd)"
elif [[ $UNINSTALL -eq 1 ]]; then
  exit 0
fi

TARGET="$CONFIG_DIR/skills/orbweaver-team"

# Whether two paths are the same place or one is inside the other, for the guard
# below. Compared as canonical directories where they exist.
canonical() {
  if [[ -d "$1" ]]; then (cd "$1" && pwd); else printf '%s' "$1"; fi
}

overlaps() {
  local a b
  a="$(canonical "$1")"; b="$(canonical "$2")"
  [[ "$a/" == "$b/"* || "$b/" == "$a/"* ]]
}

if [[ $UNINSTALL -eq 1 ]]; then
  if [[ -d "$TARGET" || -L "$TARGET" ]]; then
    # Never the shipped source, however the path was spelled.
    for candidate in "${SOURCE:-}" "$(cd "$(dirname "$0")" && pwd)/skills/orbweaver-team" "$(cd "$(dirname "$0")" && pwd)/../skills/orbweaver-team"; do
      if [[ -n "$candidate" && -d "$candidate" ]] && [[ "$(canonical "$candidate")" == "$(canonical "$TARGET")" ]]; then
        echo "Refusing to remove $TARGET: it is the skill's own source, not an installed copy." >&2
        exit 1
      fi
    done
    rm -rf "$TARGET"
    echo "Removed skill: $TARGET"
  fi
  exit 0
fi

HERE="$(cd "$(dirname "$0")" && pwd)"

# Alongside (an installed app bundle's Resources) wins over one level up (a repo
# checkout), the same resolution the hook installers use for the hook script.
if [[ -z "$SOURCE" ]]; then
  if [[ -d "$HERE/skills/orbweaver-team" ]]; then SOURCE="$HERE/skills/orbweaver-team"
  elif [[ -d "$HERE/../skills/orbweaver-team" ]]; then SOURCE="$HERE/../skills/orbweaver-team"
  fi
fi

if [[ -z "$SOURCE" || ! -f "$SOURCE/SKILL.md" ]]; then
  echo "Can't find skills/orbweaver-team next to $HERE or one level up; the team skill was not installed." >&2
  exit 1
fi

# The same guard for an install, which deletes the target first: a config dir that
# is the checkout itself would otherwise remove the source it is about to copy.
if overlaps "$SOURCE" "$TARGET"; then
  echo "Refusing to install into $TARGET: it overlaps the skill's own source ($SOURCE)." >&2
  exit 1
fi

# Every step checked. Reporting "installed" after a failed copy leaves an empty
# or half-copied folder that looks like a skill.
rm -rf "$TARGET" || { echo "Could not clear $TARGET." >&2; exit 1; }
mkdir -p "$TARGET" || { echo "Could not create $TARGET." >&2; exit 1; }
if ! cp -R "$SOURCE/." "$TARGET/"; then
  echo "Could not copy the skill into $TARGET." >&2
  rm -rf "$TARGET"
  exit 1
fi
chmod +x "$TARGET/orbweaver-team.sh" 2>/dev/null || true
echo "Skill installed: $TARGET"
