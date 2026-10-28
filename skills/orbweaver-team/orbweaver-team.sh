#!/usr/bin/env bash
# Resolves an Orbweaver team spec into a manifest: for each member, which persona
# fills which role, under what instance name, and where the files are.
#
#   orbweaver-team.sh resolve alpha                       # a saved team
#   orbweaver-team.sh resolve alpha+bravo pm.mode=lead pm.lead=atlas
#   orbweaver-team.sh resolve alpha qa=bravo-qa           # a saved team, one slot swapped
#   orbweaver-team.sh resolve pm=atlas engineer=vector,echo
#   orbweaver-team.sh list                                # every persona, role and team found
#   orbweaver-team.sh roots                               # where it looks, nearest first
#
# The manifest is tab-separated, one member per line, after `#` comment lines:
#
#   slot  instance  persona  persona_file  role_file  model  mode  lead
#
# Nothing is spawned and nothing is written. On any problem every problem is
# printed to stderr, prefixed `error:`, and nothing at all goes to stdout, so a
# lead that reads an empty manifest knows to stop rather than to start half a
# team. Exit status is 2 for a bad spec, 0 otherwise.
#
# Why a script and not the lead's own reading of the files: resolution has rules
# (nearest file wins entirely, `extends`, unions, instance names) that must come
# out the same every time, and an answer a model reconstructs from prose is an
# answer that differs between runs.
#
# Why it is written the way it is. Claude Code already needs a bash on every
# platform it runs on (Git Bash on Windows), and macOS ships bash 3.2, so: no
# associative arrays, no mapfile, no ${var,,}. And on Windows a process is
# expensive — measured, an awk spawn costs about 0.1 s under Git Bash, and a first
# version that used awk, sed and tr freely took 14 s to resolve a four-slot team.
# So everything on the hot path is a bash builtin: values come back in variables
# rather than through command substitutions (each one is a fork), and the files
# are read with `read`. The only external commands left are `ps` (once, to learn
# which instance names are live) and `cygpath` (only on Windows, only for a path
# that is not on a drive).
#
# Where things are looked up, nearest first, and a nearer file of the same name
# replaces a farther one entirely (never merged field by field):
#
#   project   <dir>/.orbweaver for the nearest ancestor of $PWD that has one
#             (and <dir>/profiles/<name>/ for personas, as profile-gen writes)
#   account   $CLAUDE_CONFIG_DIR, else ~/.claude
#   pool      $ORBWEAVER_PERSONAS, else ~/.orbweaver; under WSL with neither,
#             each Windows user's .orbweaver that exists
#   built-in  roles shipped beside this script (roles only)
#
# Under each: profiles/<name>/<name>.md, roles/<role>.md, teams/<team>.md.
# profile-gen's fixed `persona/persona.md` is accepted in the account and pool as
# the persona named by its own `slug:`.
set -u
# No globbing: a spec is words, and a `*` in one must stay a `*`. The one place
# that wants it (cmd_list) turns it back on.
set -f

NL=$'\n'
TAB=$'\t'
CR=$'\r'

ERRORS=""
err() { ERRORS="${ERRORS}error: $*${NL}"; }
fail_if_errors() {
  if [ -n "$ERRORS" ]; then
    printf '%s' "$ERRORS" >&2
    exit 2
  fi
}

# --- small string helpers ---------------------------------------------------
#
# Each leaves its answer in REPLY.

trim() {
  local v="$1"
  v="${v#"${v%%[![:space:]]*}"}"
  v="${v%"${v##*[![:space:]]}"}"
  REPLY="$v"
}

# A name that is safe to put in a path: what a persona, role, team or slot may
# be called. Anything else is refused before it is used, because these come
# from files and from the command line.
valid_name() {
  case "$1" in
    ''|*[!A-Za-z0-9_.-]*|.*|-*) return 1 ;;
    *) return 0 ;;
  esac
}

LOWER=abcdefghijklmnopqrstuvwxyz
UPPER=ABCDEFGHIJKLMNOPQRSTUVWXYZ

# One letter, case-swapped. bash 3.2 has no ${var,,}.
swap_case() {
  local c="$1" from="$2" to="$3" i
  for ((i = 0; i < 26; i++)); do
    if [ "${from:i:1}" = "$c" ]; then REPLY="${to:i:1}"; return 0; fi
  done
  REPLY="$c"
}

# Comma list from words and commas: `a, b,,c` -> `a,b,c`.
norm_list() {
  local parts out="" p
  IFS=', ' read -r -a parts <<EOF
$1
EOF
  for p in ${parts[@]+"${parts[@]}"}; do
    [ -n "$p" ] && out="${out:+$out,}$p"
  done
  REPLY="$out"
}

in_list() { case ",$2," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

# Case-blind equality, as the app compares a persona's slug (OrdinalIgnoreCase).
ieq() {
  local r=1
  shopt -s nocasematch
  case "$1" in "$2") r=0 ;; esac
  shopt -u nocasematch
  return $r
}

same_set() {
  local a b x
  IFS=, read -r -a a <<EOF
$1
EOF
  IFS=, read -r -a b <<EOF
$2
EOF
  [ "${#a[@]}" -eq "${#b[@]}" ] || return 1
  for x in ${a[@]+"${a[@]}"}; do in_list "$x" "$2" || return 1; done
  return 0
}

# a then b, without repeats, in order.
union_lists() {
  local out="$1" x parts
  IFS=, read -r -a parts <<EOF
$2
EOF
  for x in ${parts[@]+"${parts[@]}"}; do
    in_list "$x" "$out" || out="${out:+$out,}$x"
  done
  REPLY="$out"
}

# --- paths ------------------------------------------------------------------
#
# On Windows this runs under Git Bash, where an environment variable set by a
# Windows program is `C:\x` and a path bash can open is `/c/x`; and where the
# paths handed back to Claude's file tools must be the Windows spelling (`C:/x`).
# Both conversions are done in bash for a path on a drive, which is every path
# that matters; only a path with no drive needs cygpath.

case "${OSTYPE:-}" in msys*|cygwin*) ON_WINDOWS=1 ;; *) ON_WINDOWS=0 ;; esac

# A path as bash can open it.
native() {
  case "$1" in
    [A-Za-z]:[\\/]*)
      if [ "$ON_WINDOWS" -eq 1 ]; then
        local rest="${1:2}"
        swap_case "${1:0:1}" "$UPPER" "$LOWER"
        REPLY="/$REPLY${rest//\\//}"
        return 0
      fi ;;
  esac
  REPLY="$1"
}

# A path as Claude's file tools and the user would write it.
shown() {
  if [ "$ON_WINDOWS" -eq 1 ]; then
    case "$1" in
      /[A-Za-z]/*)
        swap_case "${1:1:1}" "$LOWER" "$UPPER"
        REPLY="$REPLY:${1:2}"
        return 0 ;;
      /*)
        if command -v cygpath >/dev/null 2>&1; then REPLY="$(cygpath -m "$1")"; return 0; fi ;;
    esac
  fi
  REPLY="$1"
}

# --- where this script is ---------------------------------------------------
#
# The built-in roles sit beside it. Found without a subshell, so it is spelled out
# here: no directory part at all (`bash orbweaver-team.sh`) is the current
# directory, a path on a drive (`C:/x/orbweaver-team.sh`, which is how a Windows
# program hands it over) is absolute even though it does not begin with a slash,
# and anything else relative is relative to where we are.
case "${BASH_SOURCE[0]}" in
  */*) SELF_DIR="${BASH_SOURCE[0]%/*}" ;;
  *) SELF_DIR="." ;;
esac
case "$SELF_DIR" in
  /*) ;;
  .) SELF_DIR="$PWD" ;;
  [A-Za-z]:[\\/]*) native "$SELF_DIR"; SELF_DIR="$REPLY" ;;
  *) SELF_DIR="$PWD/$SELF_DIR" ;;
esac
BUILTIN_ROLES="${ORBWEAVER_BUILTIN_ROLES:-$SELF_DIR/roles}"

# --- roots ------------------------------------------------------------------

# One line per root, "kind<TAB>path", nearest first, in $ROOTS. `projectdir` is a
# project directory itself (personas only: profile-gen's own profiles/ layout).
ROOTS=""
add_root() { ROOTS="$ROOTS$1$TAB$2$NL"; }

# The string value of a key in a JSON file, in REPLY; 1 when there is none. This
# is not a JSON parser and does not pretend to be one: settings.json is written by
# the app, one flat object, and the key wanted holds a path. It reads the file
# without a subprocess, finds `"key"`, then the string after the colon, and undoes
# the two escapes a path can carry (`\\` and `\/`).
json_string() {
  local content="" line rest v
  REPLY=""
  [ -r "$2" ] || return 1
  while IFS= read -r line || [ -n "$line" ]; do content="$content$line "; done < "$2"
  case "$content" in *"\"$1\""*) ;; *) return 1 ;; esac
  rest="${content#*\"$1\"}"
  rest="${rest#*:}"
  trim "$rest"; rest="$REPLY"
  case "$rest" in \"*) rest="${rest#\"}"; v="${rest%%\"*}" ;; *) return 1 ;; esac
  v="${v//\\\\/\\}"
  v="${v//\\\//\/}"
  [ -n "$v" ] || return 1
  REPLY="$v"
}

# The `personaPoolDir` the app has saved, in REPLY; 1 when there is none. Where the
# app keeps settings.json: the folder ORBWEAVER_SETTINGS_DIR names when set (and
# then nowhere else, as the app does), else the Mac's Application Support, else
# %APPDATA% under Git Bash, else under WSL each Windows user's AppData.
settings_pool_dir() {
  local dir f u home appdata
  dir="${ORBWEAVER_SETTINGS_DIR:-${CLAUDE_BUDDY_SETTINGS_DIR:-}}"
  if [ -n "$dir" ]; then
    native "$dir"; json_string personaPoolDir "${REPLY%/}/settings.json"; return
  fi
  native "${HOME:-}"; home="$REPLY"
  for f in "$home/Library/Application Support/Orbweaver/settings.json"; do
    if [ -f "$f" ]; then json_string personaPoolDir "$f"; return; fi
  done
  if [ -n "${APPDATA:-}" ]; then
    native "$APPDATA"; f="${REPLY%/}/Orbweaver/settings.json"
    if [ -f "$f" ]; then json_string personaPoolDir "$f"; return; fi
  fi
  if [ -n "${WSL_DISTRO_NAME:-}" ]; then
    set +f
    for u in "${ORBWEAVER_WINDOWS_USERS:-/mnt/c/Users}"/*/; do
      f="${u}AppData/Roaming/Orbweaver/settings.json"
      if [ -f "$f" ]; then set -f; json_string personaPoolDir "$f"; return; fi
    done
    set -f
  fi
  return 1
}

# Every join below strips a trailing slash first, and that is not tidiness: the
# walk up from $PWD reaches `/`, and `/` + `/profiles/x` is `//profiles/x`, which
# Git Bash reads as a UNC path to a host called "profiles". Each `[ -f ]` on one
# then waits about 2.7 s for that host to answer, and a four-person team took 14 s.
build_roots() {
  local d="$PWD" seen="" parent home account pool u
  native "${HOME:-}"; home="$REPLY"

  while :; do
    # The directory's own profiles/ first, then its .orbweaver folder: the same
    # order the app looks in (LocalPersona.CandidatesBelow), so the persona the
    # orb wears is the persona the prompt describes.
    add_root projectdir "$d"
    if [ -d "${d%/}/.orbweaver" ]; then
      add_root project "${d%/}/.orbweaver"
      seen="$seen|${d%/}/.orbweaver"
    fi
    parent="${d%/*}"
    [ -z "$parent" ] && parent=/
    [ "$parent" = "$d" ] && break
    d="$parent"
  done

  if [ -n "${CLAUDE_CONFIG_DIR:-}" ]; then native "$CLAUDE_CONFIG_DIR"; account="$REPLY"
  else account="$home/.claude"; fi
  [ -n "$account" ] && add_root account "$account"

  # The pool: the environment variable, else the `personaPoolDir` setting (what
  # the tray app reads, since it never sees a shell's environment), else
  # ~/.orbweaver. Chosen in the same order as PersonaPool.SharedRoot so a user who
  # set only the setting gets the same pool from the orb and from here. A `~` is the
  # home directory, and a relative path is refused rather than guessed at.
  local chosen="${ORBWEAVER_PERSONAS:-}" from=ORBWEAVER_PERSONAS
  if [ -z "$chosen" ] && settings_pool_dir; then chosen="$REPLY"; from="personaPoolDir"; fi

  if [ -n "$chosen" ]; then
    native "$chosen"; pool="$REPLY"
    case "$pool" in "~"|"~/"*) pool="$home${pool#\~}" ;; esac
    case "$pool" in
      /*) case "$seen" in *"|$pool"*) ;; *) add_root pool "$pool" ;; esac ;;
      *) err "$from must be an absolute path (or start with ~), not '$chosen'" ;;
    esac
  else
    if [ -n "$home" ]; then
      case "$seen" in *"|$home/.orbweaver"*) ;; *) add_root pool "$home/.orbweaver" ;; esac
    fi
    # Under WSL the pool most people keep is on the Windows side, and a bare
    # ~/.orbweaver there is a different, empty directory. With nothing chosen,
    # look in each Windows user's own. Never when a pool was named: that is the
    # user saying where it is.
    local windows_users="${ORBWEAVER_WINDOWS_USERS:-/mnt/c/Users}"
    if [ -n "${WSL_DISTRO_NAME:-}" ] && [ -d "$windows_users" ]; then
      set +f
      for u in "${windows_users%/}"/*/; do
        case "$u" in */Default/|*/Public/|*/"Default User"/|*/"All Users"/) continue ;; esac
        [ -d "${u}.orbweaver" ] && add_root pool "${u%/}/.orbweaver"
      done
      set -f
    fi
  fi

  [ -d "$BUILTIN_ROLES" ] && add_root builtin "$BUILTIN_ROLES"
  return 0
}

build_roots

# "project=..., account=..., pool=..." for a message about where it looked.
describe_roots() {
  local rk rp out=""
  while IFS="$TAB" read -r rk rp; do
    [ -n "$rk" ] || continue
    [ "$rk" = projectdir ] && continue
    out="${out:+$out, }$rk=$rp"
  done <<EOF
$ROOTS
EOF
  REPLY="$out"
}

# --- front matter -----------------------------------------------------------
#
# `key: value` lines between a first line of `---` and the next `---`. Nothing
# nested: that is the whole of the format, so there is nothing to get wrong
# about indentation. Comments (`# ...`) and blank lines are skipped, a value may
# be quoted, and a Windows file's carriage returns are ignored.

FM=""

fm_read() {
  FM=""
  local line n=0 k v t
  [ -r "$1" ] || return 1
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%"$CR"}"
    n=$((n + 1))
    if [ "$n" -eq 1 ]; then
      [ "$line" = "---" ] || return 0
      continue
    fi
    [ "$line" = "---" ] && break
    trim "$line"; t="$REPLY"
    case "$t" in ''|'#'*) continue ;; esac
    case "$line" in *:*) ;; *) continue ;; esac
    trim "${line%%:*}"; k="$REPLY"
    trim "${line#*:}"; v="$REPLY"
    # A quoted value is what is inside the quotes, so a `#` in it is part of it;
    # an unquoted one ends at a space followed by `#`, which is how the examples
    # in FORMATS.md annotate themselves (`model: sonnet   # optional`). Keeping
    # that comment would put it in the model column, and make
    # `disabled: true  # optional` read as something other than true.
    case "$v" in
      \"*) v="${v#\"}"; v="${v%%\"*}" ;;
      \'*) v="${v#\'}"; v="${v%%\'*}" ;;
      *" #"*) v="${v%% #*}"; trim "$v"; v="$REPLY" ;;
    esac
    FM="$FM$k$TAB$v$NL"
  done < "$1"
  return 0
}

# The value of a key in the front matter fm_read last read, in REPLY.
fm_get() {
  local k v
  while IFS="$TAB" read -r k v; do
    if [ "$k" = "$1" ]; then REPLY="$v"; return 0; fi
  done <<EOF
$FM
EOF
  REPLY=""
  return 1
}

# --- finding files ----------------------------------------------------------

# The nearest file of a kind (`teams`, `roles`) with a name, across the roots
# that carry that kind, in REPLY (empty when there is none).
find_file() {
  local kind="$1" name="$2" rk rp f
  while IFS="$TAB" read -r rk rp; do
    case "$rk" in
      project|account|pool) f="${rp%/}/$kind/$name.md" ;;
      builtin) [ "$kind" = roles ] || continue; f="${rp%/}/$name.md" ;;
      *) continue ;;
    esac
    if [ -f "$f" ]; then REPLY="$f"; return 0; fi
  done <<EOF
$ROOTS
EOF
  REPLY=""
}

# Whether a persona file says its own name in its front matter, leaving that
# front matter in FM for the caller. A file that does not is not a persona the
# resolver can describe to a teammate, and the app skips a nearer file that gives
# it nothing too, so the search goes on to the next place instead of stopping on it.
names_itself() {
  fm_read "$1" || return 1
  fm_get name
  [ -n "$REPLY" ]
}

# A persona's file, in REPLY: profiles/<name>/<name>.md in the tracked and
# gitignored layouts at every level, and the fixed persona/persona.md where its
# slug says it is this persona. Tried under the full name and then the persona it
# is an instance of (`vector_2` is Vector's).
find_persona() {
  local name="$1" names="$1" rk rp f n suffix
  case "$name" in
    ?*_[0-9]*)
      suffix="${name##*_}"
      case "$suffix" in *[!0-9]*) ;; *) names="$name ${name%_*}" ;; esac ;;
  esac
  while IFS="$TAB" read -r rk rp; do
    case "$rk" in project|projectdir|account|pool) ;; *) continue ;; esac
    for n in $names; do
      for f in "${rp%/}/profiles/$n/$n.md" "${rp%/}/.profiles-assets/$n/$n.md"; do
        if [ -f "$f" ] && names_itself "$f"; then REPLY="$f"; return 0; fi
      done
    done
    case "$rk" in
      project|account|pool)
        f="${rp%/}/persona/persona.md"
        if [ -f "$f" ] && names_itself "$f"; then
          fm_get slug
          for n in $names; do
            if ieq "$REPLY" "$n"; then REPLY="$f"; return 0; fi
          done
        fi ;;
    esac
  done <<EOF
$ROOTS
EOF
  REPLY=""
}

# --- team data --------------------------------------------------------------
#
# A team is held in three parallel arrays, row i being slot KS[i], key KK[i],
# value KV[i]. Keys: members (a comma list), mode, lead, model, and `unioned` (a
# flag set when two teams filled the slot with different people, which is what
# makes its mode something the user has to say).

KS=(); KK=(); KV=()

kv_get() {
  local i
  for ((i = 0; i < ${#KS[@]}; i++)); do
    if [ "${KS[i]}" = "$1" ] && [ "${KK[i]}" = "$2" ]; then REPLY="${KV[i]}"; return 0; fi
  done
  REPLY=""
  return 1
}

# Remove rows. $2 empty means every row of the slot; otherwise just that key.
kv_del() {
  local i ns=() nk=() nv=()
  for ((i = 0; i < ${#KS[@]}; i++)); do
    if [ "${KS[i]}" = "$1" ] && { [ -z "${2:-}" ] || [ "${KK[i]}" = "$2" ]; }; then continue; fi
    ns[${#ns[@]}]="${KS[i]}"; nk[${#nk[@]}]="${KK[i]}"; nv[${#nv[@]}]="${KV[i]}"
  done
  KS=(); KK=(); KV=()
  for ((i = 0; i < ${#ns[@]}; i++)); do
    KS[i]="${ns[i]}"; KK[i]="${nk[i]}"; KV[i]="${nv[i]}"
  done
}

kv_set() {
  kv_del "$1" "$2"
  KS[${#KS[@]}]="$1"; KK[${#KK[@]}]="$2"; KV[${#KV[@]}]="$3"
}

# The slots, once each in the order first seen, space-separated in REPLY.
kv_slots() {
  local i out=" "
  for ((i = 0; i < ${#KS[@]}; i++)); do
    case "$out" in *" ${KS[i]} "*) ;; *) out="$out${KS[i]} " ;; esac
  done
  trim "$out"
}

# The rows of one team after `extends`, as lines on stdout ("slot TAB key TAB
# value"). A slot the team names replaces the base's slot entirely, along with
# that slot's mode, lead and model. Problems come out as `!error: ...` lines among
# the rows, because this runs in a command substitution and an `err` here would
# be lost with the subshell; the caller routes them with absorb.
load_team() {
  local name="$1" depth="${2:-0}" file base="" rows="" own="" ownslots=" " k v slot rk rv rr pairs
  if ! valid_name "$name"; then echo "!error: team name '$name' is not a name (letters, digits, '_', '-', '.')"; return 0; fi
  if [ "$depth" -gt 5 ]; then echo "!error: team '$name' extends too deeply (a cycle?)"; return 0; fi

  find_file teams "$name"; file="$REPLY"
  if [ -z "$file" ]; then
    describe_roots
    echo "!error: unknown team '$name' (looked in: $REPLY)"
    return 0
  fi

  fm_read "$file"
  pairs="$FM"
  fm_get extends; base="$REPLY"
  if [ -n "$base" ]; then rows="$(load_team "$base" $((depth + 1)))"; fi

  while IFS="$TAB" read -r k v; do
    case "$k" in
      '') continue ;;
      team|title|extends|description) continue ;;
      *.mode|*.lead|*.model)
        slot="${k%%.*}"
        own="$own$slot${TAB}${k#*.}${TAB}$v$NL"
        case "$ownslots" in *" $slot "*) ;; *) ownslots="$ownslots$slot " ;; esac ;;
      *.*) echo "!error: team '$name': unknown key '$k'" ;;
      *)
        if ! valid_name "$k"; then echo "!error: team '$name': '$k' is not a slot name"; continue; fi
        norm_list "$v"
        own="$own$k${TAB}members${TAB}$REPLY$NL"
        case "$ownslots" in *" $k "*) ;; *) ownslots="$ownslots$k " ;; esac ;;
    esac
  done <<EOF
$pairs
EOF

  # Every base row except those of a slot the team names, then the team's own.
  while IFS= read -r line; do
    case "$line" in
      '') continue ;;
      '!error:'*) printf '%s\n' "$line"; continue ;;
    esac
    rk="${line%%"$TAB"*}"
    case "$ownslots" in *" $rk "*) continue ;; esac
    printf '%s\n' "$line"
  done <<EOF
$rows
EOF
  printf '%s' "$own"
}

# Routes the `!error:` lines load_team printed into ERRORS and leaves the rows in
# $ABSORBED. It cannot be a pipeline stage or a command substitution: either
# would run it in a subshell, and the errors it files would be filed there and
# lost.
ABSORBED=""
absorb() {
  ABSORBED=""
  local line
  while IFS= read -r line; do
    case "$line" in
      '!error: '*) ERRORS="${ERRORS}error: ${line#!error: }$NL" ;;
      '') ;;
      *) ABSORBED="$ABSORBED$line$NL" ;;
    esac
  done <<EOF
$1
EOF
}

# Loads rows ("slot TAB key TAB value" lines) into the arrays as they are.
kv_load() {
  local s k v
  while IFS="$TAB" read -r s k v; do
    [ -n "$s" ] || continue
    kv_set "$s" "$k" "$v"
  done <<EOF
$1
EOF
}

# Adds a team's rows (in $1) to the arrays: a slot not there yet is taken whole; a
# slot both fill with the same people is left alone; one filled by different
# people becomes the union of them, flagged.
merge_team() {
  local rows="$1" slot key val have new seen=" " slot_rows s2 k2 v2 merged
  while IFS="$TAB" read -r slot key val; do
    [ -n "$slot" ] || continue
    case "$seen" in *" $slot "*) continue ;; esac
    seen="$seen$slot "

    # This slot's own rows, and its members.
    new=""; slot_rows=""
    while IFS="$TAB" read -r s2 k2 v2; do
      [ "$s2" = "$slot" ] || continue
      slot_rows="$slot_rows$s2$TAB$k2$TAB$v2$NL"
      [ "$k2" = members ] && new="$v2"
    done <<EOF
$rows
EOF

    kv_get "$slot" members; have="$REPLY"
    if [ -z "$have" ]; then
      kv_load "$slot_rows"
    elif same_set "$have" "$new"; then
      :
    else
      union_lists "$have" "$new"; merged="$REPLY"
      kv_del "$slot"
      kv_set "$slot" members "$merged"
      kv_set "$slot" unioned 1
    fi
  done <<EOF
$rows
EOF
}

# --- instance names ---------------------------------------------------------

LIVE=""
ASSIGNED=" "

# The instance names in use, in $LIVE. Two sources, because neither is complete:
# the teams Claude Code records in `<account>/teams/*/config.json` (a member is
# listed while it lives; this is the one source that works under Git Bash, where
# `ps` cannot see a native claude.exe), and the process table for a teammate that
# is not in this account's teams. A stale entry costs nothing but an unnecessary
# `_2`.
live_names() {
  local ps_out line rest rk rp f in_members
  if [ -n "${ORBWEAVER_LIVE_NAMES+x}" ]; then
    for rest in $ORBWEAVER_LIVE_NAMES; do LIVE="$LIVE$rest$NL"; done
  else
    ps_out="$(ps -eo args 2>/dev/null)"
    while IFS= read -r line; do
      case "$line" in
        *"--agent-name="*) rest="${line#*--agent-name=}"; LIVE="$LIVE${rest%% *}$NL" ;;
        *"--agent-name "*) rest="${line#*--agent-name }"; LIVE="$LIVE${rest%% *}$NL" ;;
      esac
    done <<EOF
$ps_out
EOF
  fi

  set +f
  while IFS="$TAB" read -r rk rp; do
    [ "$rk" = account ] || continue
    for f in "${rp%/}"/teams/*/config.json; do
      [ -f "$f" ] || continue
      in_members=0
      while IFS= read -r line || [ -n "$line" ]; do
        case "$line" in *'"members"'*) in_members=1; continue ;; esac
        [ "$in_members" -eq 1 ] || continue
        case "$line" in
          *'"name"'*)
            rest="${line#*\"name\"}"; rest="${rest#*:}"; trim "$rest"; rest="${REPLY#\"}"
            LIVE="$LIVE${rest%%\"*}$NL" ;;
        esac
      done < "$f"
    done
  done <<EOF
$ROOTS
EOF
  set -f
  return 0
}

# The first of `<persona>`, `<persona>_2`, `<persona>_3`... that no live session
# and no member already in this manifest has, in PICKED. Two live sessions with
# one name cannot be told apart by Orbweaver, which would then show one's
# conversation on the other's panel; this is what keeps that from being possible
# by accident. Sets a variable rather than printing: through a command
# substitution it would record the name in a subshell and forget it, and the same
# persona in two slots of one team would be handed the same name twice.
PICKED=""
pick_instance() {
  local persona="$1" n=1 candidate
  while :; do
    if [ "$n" -eq 1 ]; then candidate="$persona"; else candidate="${persona}_$n"; fi
    case "$ASSIGNED" in *" $candidate "*) n=$((n + 1)); continue ;; esac
    case "$NL$LIVE" in *"$NL$candidate$NL"*) n=$((n + 1)); continue ;; esac
    ASSIGNED="$ASSIGNED$candidate "
    PICKED="$candidate"
    return 0
  done
}

# --- resolve ----------------------------------------------------------------

cmd_resolve() {
  local spec="$*" token team_tokens="" overrides="" t parts rows first=1
  if [ -z "$spec" ]; then
    echo "usage: orbweaver-team.sh resolve <team>[+<team>...] [slot=persona[,persona]] [slot.mode=lead|collaborate] [slot.lead=persona] [slot.model=model]" >&2
    exit 2
  fi

  for token in $spec; do
    case "$token" in
      *=*) overrides="$overrides $token" ;;
      *) team_tokens="$team_tokens $token" ;;
    esac
  done

  # Teams first, unioned left to right.
  for token in $team_tokens; do
    parts="${token//+/ }"
    for t in $parts; do
      rows="$(load_team "$t")"
      absorb "$rows"
      if [ "$first" -eq 1 ]; then
        first=0
        kv_load "$ABSORBED"
      else
        merge_team "$ABSORBED"
      fi
    done
  done

  # Then the overrides, in the order given. A slot named by `slot=` is replaced
  # outright, including anything a union said about it.
  local ov key val slot sub
  for ov in $overrides; do
    key="${ov%%=*}"
    val="${ov#*=}"
    case "$key" in
      *.mode|*.lead|*.model)
        slot="${key%%.*}"; sub="${key#*.}"
        valid_name "$slot" || { err "'$slot' is not a slot name"; continue; }
        kv_set "$slot" "$sub" "$val" ;;
      *.*) err "unknown override '$key'" ;;
      *)
        valid_name "$key" || { err "'$key' is not a slot name"; continue; }
        kv_del "$key"
        norm_list "$val"
        kv_set "$key" members "$REPLY" ;;
    esac
  done

  fail_if_errors
  kv_slots
  if [ -z "$REPLY" ]; then err "the spec names no team and no slot"; fail_if_errors; fi
  local slots="$REPLY"

  live_names
  describe_roots; local roots_text="$REPLY"

  local rows_out="" members mode lead model role_file role_model persona pfile inst count unioned disabled mem i role_shown
  for slot in $slots; do
    kv_get "$slot" members; members="$REPLY"
    kv_get "$slot" mode; mode="$REPLY"
    kv_get "$slot" lead; lead="$REPLY"
    kv_get "$slot" model; model="$REPLY"
    kv_get "$slot" unioned; unioned="$REPLY"

    if [ -z "$members" ]; then err "slot '$slot' names no persona"; continue; fi
    IFS=, read -r -a mem <<EOF
$members
EOF
    count="${#mem[@]}"

    case "$mode" in ''|lead|collaborate) ;; *) err "slot '$slot': mode must be lead or collaborate, not '$mode'"; continue ;; esac
    if [ "$count" -gt 1 ]; then
      [ -z "$mode" ] && [ -n "$lead" ] && mode=lead
      if [ -z "$mode" ]; then
        if [ -n "$unioned" ]; then
          err "slot '$slot' is filled by ${members//,/ and } in a union: say how they work together with $slot.mode=lead (and $slot.lead=<persona>) or $slot.mode=collaborate"
          continue
        fi
        mode=collaborate
      fi
      if [ "$mode" = lead ]; then
        if [ -z "$lead" ]; then err "slot '$slot' has mode lead but no $slot.lead"; continue; fi
        in_list "$lead" "$members" || { err "slot '$slot': lead '$lead' is not one of $members"; continue; }
      fi
    else
      mode=""; lead=""
    fi

    find_file roles "$slot"; role_file="$REPLY"
    if [ -z "$role_file" ]; then err "unknown role '$slot' (looked in: $roots_text)"; continue; fi
    fm_read "$role_file"
    fm_get disabled; disabled="$REPLY"
    fm_get model; role_model="$REPLY"
    shown "$role_file"; role_shown="$REPLY"
    case "$disabled" in
      true|yes|1) err "role '$slot' is disabled ($role_shown)"; continue ;;
    esac
    [ -n "$model" ] || model="$role_model"

    for ((i = 0; i < count; i++)); do
      persona="${mem[i]}"
      if ! valid_name "$persona"; then err "slot '$slot': '$persona' is not a persona name"; continue; fi
      find_persona "$persona"; pfile="$REPLY"
      if [ -z "$pfile" ]; then err "slot '$slot': unknown persona '$persona' (looked in: $roots_text)"; continue; fi
      pick_instance "$persona"; inst="$PICKED"
      shown "$pfile"
      rows_out="$rows_out$slot$TAB$inst$TAB$persona$TAB$REPLY$TAB$role_shown$TAB$model$TAB$mode$TAB$lead$NL"
    done
  done

  fail_if_errors

  printf '# orbweaver-team manifest\n'
  printf '# spec: %s\n' "$spec"
  printf '# roots: %s\n' "$roots_text"
  printf 'slot\tinstance\tpersona\tpersona_file\trole_file\tmodel\tmode\tlead\n'
  printf '%s' "$rows_out"
}

# --- list and roots ---------------------------------------------------------

cmd_list() {
  set +f
  local kind rk rp f name base seen
  for kind in teams roles profiles; do
    printf '# %s\n' "$kind"
    seen=" "
    while IFS="$TAB" read -r rk rp; do
      case "$rk" in
        project|account|pool) ;;
        projectdir) [ "$kind" = profiles ] || continue ;;
        builtin) [ "$kind" = roles ] || continue ;;
        *) continue ;;
      esac
      if [ "$kind" = profiles ]; then
        for f in "${rp%/}"/profiles/*/*.md "${rp%/}"/.profiles-assets/*/*.md; do
          [ -f "$f" ] || continue
          base="${f##*/}"; base="${base%.md}"
          name="${f%/*}"; name="${name##*/}"
          [ "$base" = "$name" ] || continue
          case "$seen" in *" $name "*) continue ;; esac
          seen="$seen$name "
          shown "$f"; printf '%s\t%s\t%s\n' "$name" "$rk" "$REPLY"
        done
      else
        if [ "$rk" = builtin ]; then set -- "${rp%/}"/*.md; else set -- "${rp%/}/$kind"/*.md; fi
        for f in "$@"; do
          [ -f "$f" ] || continue
          name="${f##*/}"; name="${name%.md}"
          case "$seen" in *" $name "*) continue ;; esac
          seen="$seen$name "
          shown "$f"; printf '%s\t%s\t%s\n' "$name" "$rk" "$REPLY"
        done
      fi
    done <<EOF
$ROOTS
EOF
  done
}

cmd_roots() {
  local rk rp
  while IFS="$TAB" read -r rk rp; do
    if [ -n "$rk" ]; then printf '%s\t%s\n' "$rk" "$rp"; fi
  done <<EOF
$ROOTS
EOF
  return 0
}

cmd_help() {
  local line n=0
  while IFS= read -r line; do
    n=$((n + 1))
    [ "$n" -lt 2 ] && continue
    [ "$n" -gt 45 ] && break
    printf '%s\n' "${line#\# }"
  done < "${BASH_SOURCE[0]}"
}

case "${1:-}" in
  resolve) shift; cmd_resolve "$@" ;;
  list) cmd_list ;;
  roots) cmd_roots ;;
  -h|--help|help|'') cmd_help; exit 0 ;;
  *) echo "unknown command '$1' (resolve, list, roots)" >&2; exit 2 ;;
esac
