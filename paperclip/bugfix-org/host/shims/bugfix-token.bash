# Sourced by the `gh` and `git` stand-ins beside it (CB-489); not run alone.
#
# Paperclip resolves BUGFIX_GH_TOKEN and BUGFIX_GH_WORKFLOW_TOKEN once, when a
# run starts, and an installation token lives an hour. So a run's environment
# copy can be dead before the run ends, or even at its start: QAF-190 and
# QAF-209 both began with an expired one. The warden writes every token it
# mints to its token folder as well, and these functions read that file at
# each call. The folder is $BUGFIX_TOKEN_DIR, else bugfix-tokens beside
# $BUGFIX_SPOOL (where the warden puts it; never inside the spool, which
# agents write to). The workflow file is read only for a GH_TOKEN equal to a
# non-empty $BUGFIX_GH_WORKFLOW_TOKEN, so a run that was never given that
# token never gets it from here, so `gh` and `git` always carry the newest token.
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

# The token folder, or nothing.
bugfix_token_dir() {
  local s
  if [ -n "${BUGFIX_TOKEN_DIR:-}" ]; then
    printf '%s' "$BUGFIX_TOKEN_DIR"
  elif [ -n "${BUGFIX_SPOOL:-}" ]; then
    s=${BUGFIX_SPOOL%/}
    printf '%s' "${s%/*}/bugfix-tokens"
  fi
}

# The current token from <token folder>/<kind>, or nothing.
bugfix_token_read() {
  local d f t=
  d=$(bugfix_token_dir)
  [ -n "$d" ] || return 0
  f="$d/$1"
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

# Run "$real" "$@" with the current token. stdout streams as it comes, so a
# `gh run watch` that runs for many minutes shows its progress rather than
# looking hung. stderr is held per attempt, which is what the retry reads.
# A failed attempt is retried once, with the token file's newer value, only
# when its stderr shows GitHub's 401 AND it wrote nothing to stdout: output a
# caller has already received cannot be taken back. So `gh api`, which prints
# the 401 body on stdout, is not retried here; the skill's "run it once more"
# covers it. The exit status is the last attempt's.
#
# stdin: a regular file (a here-document) is copied, so a retry reads it again.
# A pipe or socket is handed straight to the command, never waited on, since
# its writer may stay open for as long as the command runs (`producer | gh`);
# such a call is not retried, because its input cannot be replayed. A tty or
# /dev/null needs nothing. /bin/bash is 3.2 on macOS, hence a pipe through tee, not >( ).
bugfix_run_with_retry() {
  local real=$1; shift
  local kind used status fresh once
  kind=$(bugfix_token_kind)
  if [ -z "$kind" ]; then
    exec "$real" "$@"
  fi
  bugfix_token_apply "$kind"
  used=${GH_TOKEN:-}
  # Globals, not locals: the EXIT trap runs after this function returns.
  bugfix_tmp=$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/bugfix-shim.XXXXXX" 2>/dev/null) || exec "$real" "$@"
  trap '/bin/rm -rf "$bugfix_tmp" 2>/dev/null' EXIT
  out="$bugfix_tmp/out"; err="$bugfix_tmp/err"; in=; once=
  if [ -p /dev/stdin ] || [ -S /dev/stdin ]; then
    once=1
  elif [ -f /dev/stdin ]; then
    in="$bugfix_tmp/in"
    /bin/cat > "$in"
  fi
  bugfix_attempt "$real" "$@"; status=$?
  if [ "$status" -ne 0 ] && [ -z "$once" ] && [ ! -s "$out" ] && bugfix_saw_401; then
    fresh=$(bugfix_token_read "$kind")
    if [ -n "$fresh" ] && [ "$fresh" != "$used" ]; then
      export GH_TOKEN="$fresh"
      bugfix_attempt "$real" "$@"; status=$?
    fi
  fi
  [ -s "$err" ] && /bin/cat "$err" >&2
  return "$status"
}

# One attempt: stdout through tee to the caller and to $out (only to know
# whether anything was written), stderr to $err.
bugfix_attempt() {
  local real=$1 s; shift
  if [ -n "$in" ]; then
    "$real" "$@" < "$in" 2> "$err" | /usr/bin/tee "$out"; s=${PIPESTATUS[0]}
  else
    "$real" "$@" 2> "$err" | /usr/bin/tee "$out"; s=${PIPESTATUS[0]}
  fi
  return "$s"
}

# GitHub's 401, as gh and git (through gh auth git-credential) report it.
bugfix_saw_401() {
  local text
  text=$(< "$err")
  case "$text" in
    *"HTTP 401"*|*"Bad credentials"*|*"401 Unauthorized"*|*"returned error: 401"*|*"Invalid username or token"*|*"Authentication failed for"*) return 0 ;;
  esac
  return 1
}
