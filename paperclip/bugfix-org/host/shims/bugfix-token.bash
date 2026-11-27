# Sourced by the `gh` and `git` stand-ins beside it (CB-489); not run alone.
#
# Paperclip resolves BUGFIX_GH_TOKEN and BUGFIX_GH_WORKFLOW_TOKEN once, when a
# run starts, and an installation token lives an hour. So a run's environment
# copy can be dead before the run ends, or even at its start: QAF-190 and
# QAF-209 both began with an expired one. The warden writes every token it
# mints to $BUGFIX_SPOOL/tokens/ as well, and these functions read that file
# at each call, so `gh` and `git` always carry the newest token.
#
# A GH_TOKEN that is empty, or that is one of the two start-of-run copies
# (which is what the skill's `export GH_TOKEN="$BUGFIX_GH_TOKEN"` prefix
# gives), is swapped for the matching file's current value. Any other GH_TOKEN
# was set on purpose and is kept. Nothing here ever prints a token.
# PATH-independent: builtins, plus /bin/cat and /usr/bin/mktemp by absolute
# path, which every macOS has whatever the service PATH holds.

# The file name for the token in GH_TOKEN, or nothing when GH_TOKEN is not
# one of the org's start-of-run copies.
bugfix_token_kind() {
  if [ -z "${GH_TOKEN:-}" ] || [ "$GH_TOKEN" = "${BUGFIX_GH_TOKEN:-}" ]; then
    echo gh-token
  elif [ -n "${BUGFIX_GH_WORKFLOW_TOKEN:-}" ] && [ "$GH_TOKEN" = "$BUGFIX_GH_WORKFLOW_TOKEN" ]; then
    echo gh-workflow-token
  fi
}

# The current token from $BUGFIX_SPOOL/tokens/<kind>, or nothing.
bugfix_token_read() {
  local f t=
  [ -n "${BUGFIX_SPOOL:-}" ] || return 0
  f="$BUGFIX_SPOOL/tokens/$1"
  [ -f "$f" ] && [ -r "$f" ] || return 0
  IFS= read -r t < "$f" || true
  printf '%s' "$t"
}

# Point GH_TOKEN at the current token of kind $1. Falls back to the
# start-of-run copy only when GH_TOKEN is empty and there is no file, which is
# what the CB-478 stand-ins did.
bugfix_token_apply() {
  local t
  t=$(bugfix_token_read "$1")
  if [ -n "$t" ]; then
    export GH_TOKEN="$t"
  elif [ -z "${GH_TOKEN:-}" ] && [ -n "${BUGFIX_GH_TOKEN:-}" ]; then
    export GH_TOKEN="$BUGFIX_GH_TOKEN"
  fi
}

# Run "$real" "$@" with the current token. When GitHub answers 401 and the
# token file now holds a different token than the one just used, run it once
# more with that one. stdout and stderr are held until the command ends, so
# the failed attempt's output (`gh api` prints the 401 body on stdout) never
# reaches the caller ahead of the retry's. stdin is kept for the retry. The
# exit status is the last attempt's.
bugfix_run_with_retry() {
  local real=$1; shift
  local kind used status fresh
  kind=$(bugfix_token_kind)
  if [ -z "$kind" ]; then
    exec "$real" "$@"
  fi
  bugfix_token_apply "$kind"
  used=${GH_TOKEN:-}
  # Globals, not locals: the EXIT trap runs after this function returns.
  bugfix_tmp=$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/bugfix-shim.XXXXXX" 2>/dev/null) || exec "$real" "$@"
  trap '/bin/rm -rf "$bugfix_tmp" 2>/dev/null' EXIT
  out="$bugfix_tmp/out"; err="$bugfix_tmp/err"; in=
  if [ ! -t 0 ]; then
    in="$bugfix_tmp/in"
    /bin/cat > "$in"
  fi
  bugfix_attempt "$real" "$@"; status=$?
  if [ "$status" -ne 0 ] && bugfix_saw_401; then
    fresh=$(bugfix_token_read "$kind")
    if [ -n "$fresh" ] && [ "$fresh" != "$used" ]; then
      export GH_TOKEN="$fresh"
      bugfix_attempt "$real" "$@"; status=$?
    fi
  fi
  bugfix_emit "$out" 1; bugfix_emit "$err" 2
  return "$status"
}

bugfix_attempt() {
  local real=$1; shift
  if [ -n "$in" ]; then
    "$real" "$@" < "$in" > "$out" 2> "$err"
  else
    "$real" "$@" > "$out" 2> "$err"
  fi
}

# GitHub's 401, as gh and git (through gh auth git-credential) report it.
bugfix_saw_401() {
  local text
  text=$(< "$err")$(< "$out")
  case "$text" in
    *"HTTP 401"*|*"Bad credentials"*|*"401 Unauthorized"*|*"returned error: 401"*|*"Invalid username or token"*|*"Authentication failed for"*) return 0 ;;
  esac
  return 1
}

bugfix_emit() {
  [ -s "$1" ] || return 0
  if [ "$2" = 1 ]; then /bin/cat "$1"; else /bin/cat "$1" >&2; fi
}
