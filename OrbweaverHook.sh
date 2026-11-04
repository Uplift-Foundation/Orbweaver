#!/bin/bash
# Orbweaver hook for macOS/Linux — bash twin of OrbweaverHook.ps1.
# Usage (from a Claude Code, Codex, or Grok Build hook):
#   OrbweaverHook.sh <idle|generating|waiting|ended>
#   OrbweaverHook.sh codex <idle|generating|waiting|ended>
#   OrbweaverHook.sh grok <idle|generating|waiting|ended>
# Reads the hook payload JSON on stdin for session_id and cwd.
#
# One script for the three CLIs rather than three, because almost none of it is
# about any of them: finding the terminal, finding the session's process, and
# writing the status file are the same job whoever asked. The agent word only
# gates the handful of places where they genuinely differ, and each of those
# says why.
#
# Grok Build also fires this script through Claude Code's settings.json — it
# loads ~/.claude as a compatibility layer. GROK_SESSION_ID is set on every
# Grok hook process, so that, not $1, is what decides the agent when both
# apply. Otherwise a Grok session is labelled cli:claude and auto-color
# appends a Claude Code agent-color record into Grok's updates.jsonl.
#
# It must exit 0 and print nothing, whatever happens. That has always been good
# manners; under Codex it is load-bearing. Codex reads a hook's stdout as JSON
# with `additionalProperties: false`, so anything printed is
# "hook returned invalid permission-request JSON output" — and **exit status 2
# is a deny**. A hook that fails noisily would start refusing the user's own
# approvals. See docs/codex-findings.md.

AGENT="claude"
case "$1" in
    claude|codex|grok) AGENT="$1"; shift ;;
esac

STATE="$1"
case "$STATE" in
    idle|generating|waiting|ended) ;;
    *) exit 0 ;;
esac

# Grok injects these on every hook, including ones loaded from Claude Code's
# settings.json. That is a stronger signal than argv: a Grok session that
# reached this script as `claude idle` is still a Grok session.
if [ -n "${GROK_SESSION_ID:-}" ] || [ -n "${GROK_HOOK_EVENT:-}" ]; then
    AGENT="grok"
fi

PAYLOAD=$(cat)

# Pull one top-level string out of the payload.
#
# The obvious `sed 's/.*"key":"\([^"]*\)".*/\1/'` is greedy and takes the
# *last* match on the line, which was fine while the only payloads came from
# Claude Code. Codex's PreToolUse and PermissionRequest payloads embed
# `tool_input` as arbitrary JSON, and its own command records carry
# `"cwd":"file:///…"` — so a nested key would win and CWD would become
# something that is not a directory. Everything downstream keys off cwd: the
# orb's name, its saved position, which sessions are siblings.
#
# grep -o takes the first, which is the top-level one, because the payload
# writes its own fields before nesting anything.
field() {
    printf '%s' "$PAYLOAD" \
        | grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
        | head -1 \
        | sed 's/.*:[[:space:]]*"\(.*\)"$/\1/'
}

SESSION_ID=$(field session_id)
[ -n "$SESSION_ID" ] || SESSION_ID=$(field sessionId)
[ -n "$SESSION_ID" ] || SESSION_ID="${GROK_SESSION_ID:-}"
CWD=$(field cwd)
[ -n "$CWD" ] || CWD="${GROK_WORKSPACE_ROOT:-}"
TRANSCRIPT=$(field transcript_path)
[ -n "$TRANSCRIPT" ] || TRANSCRIPT=$(field transcriptPath)
[ -n "$SESSION_ID" ] || SESSION_ID="unknown"

# Where the status file goes. The app resolves the same way, in the same
# order (StatusDirectory.Root), and the two have to keep agreeing:
#
#   1. ORBWEAVER_STATUS_ROOT — the test seam, and the only way to send one
#      hook somewhere private without moving TMPDIR for everything else.
#   2. The per-user temp directory macOS gives this user, asked of the C
#      library: `getconf DARWIN_USER_TEMP_DIR` is confstr(3), the same call
#      the app makes. It reads no environment variable, so a parent that
#      pointed TMPDIR somewhere private cannot move it.
#   3. TMPDIR, then /tmp — Linux, or a getconf that could not answer.
#
# TMPDIR came first until CB-306, "because that is what Path.GetTempPath()
# returns" — true in a login shell, false under OpenClaw. Its gateway runs
# with TMPDIR=~/.openclaw/tmp, and every Claude Code session it spawns
# inherits it — as does a tmux server started from one, and every session
# in that tmux. Those hooks wrote to ~/.openclaw/tmp/orbweaver while the app
# read /var/folders/.../T/orbweaver, and the mini served no sessions to any
# peer with five alive. Nothing failed; the two halves were looking in
# different places, which is the launchd story in StatusDirectory.cs again
# from the other side. The per-user directory carries a per-boot token, so
# it is computed on every call rather than baked in by the installer the way
# Windows bakes -TempDir. It must be an existing directory to count: a
# getconf that answers with something else is treated as no answer.
DIR="${ORBWEAVER_STATUS_ROOT:-}"
if [ -z "$DIR" ]; then
    DIR=$(getconf DARWIN_USER_TEMP_DIR 2>/dev/null)
    [ -n "$DIR" ] && [ -d "$DIR" ] || DIR=""
fi
[ -n "$DIR" ] || DIR="${TMPDIR:-/tmp/}"
DIR="${DIR%/}/orbweaver"

# Whether to give a session a colour when it has none.
#
# A marker file the app writes beside the status files, not a flag in the hook
# command — and the difference is not stylistic. The first version baked
# `--auto-color` into the command, which meant toggling the setting rewrote
# Codex's hooks.json; Codex hashes that file and marks a changed entry
# `modified`, so its hooks stop running until the review is accepted again.
# Turning a colour on would silently stop every Codex orb until the user
# noticed. A marker leaves the wiring untouched, so trust survives.
#
# Cheaper than the alternatives too: one stat, against an osascript per hook
# call to read the app's settings, on a script that runs on every tool use.
AUTO_COLOR=0
[ -f "$DIR/.auto-color" ] && AUTO_COLOR=1
FILE="$DIR/$SESSION_ID.txt"

if [ "$STATE" = "ended" ]; then
    rm -f "$FILE"
    exit 0
fi

# Codex's transcript_path is nullable, and a null is not a quoted string so it
# arrives here empty. The rollout is findable anyway — its filename ends in the
# session id — and only this script can see CODEX_HOME, which is where the app
# would otherwise have to guess. Resolving it here keeps the same division of
# labour the rest of the file has: the hook knows the environment, the app
# reads files.
if [ "$AGENT" = "codex" ] && [ -z "$TRANSCRIPT" ]; then
    for candidate in "${CODEX_HOME:-$HOME/.codex}"/sessions/*/*/*/rollout-*-"$SESSION_ID".jsonl; do
        [ -f "$candidate" ] && TRANSCRIPT="$candidate"
    done
fi

# Grok stores each session at ~/.grok/sessions/<urlencode(cwd)>/<id>/updates.jsonl.
# The payload usually names that file already (Claude-compat snake_case was
# observed live); this is the same glob fallback Codex has, for the window
# before transcript_path is set.
if [ "$AGENT" = "grok" ] && [ -z "$TRANSCRIPT" ] && [ -n "$SESSION_ID" ] \
   && [ "$SESSION_ID" != "unknown" ]; then
    for candidate in "${GROK_HOME:-$HOME/.grok}"/sessions/*/"$SESSION_ID"/updates.jsonl; do
        [ -f "$candidate" ] && TRANSCRIPT="$candidate"
    done
fi

TITLE=""
COLOR=""

# What the chat is called and what color it's been given. Claude Code keeps
# all three of these in the transcript, re-emitting them as the session goes:
#   {"type":"custom-title","customTitle":"claude-buddy",...}   <- /rename
#   {"type":"ai-title","aiTitle":"Package app with a tray",...} <- auto-named
#   {"type":"agent-color","agentColor":"green",...}             <- /color
# A name you set with /rename wins over the generated one regardless of which
# was written last, and a session too young to have either falls back to the
# directory name in the app.
#
# Anchoring the match at the start of the line is what makes this safe:
# transcripts are full of quoted text that would otherwise match, but content
# inside a message is JSON-escaped, so only a real record can start this way.
#
# Claude Code only. A Codex rollout contains none of these three records, so
# the tail below would never match and the whole-file fallback would re-read a
# multi-megabyte transcript on every single tool call.
if [ "$AGENT" = "claude" ] && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
    # Transcripts reach tens of MB and this runs on every tool call, so pull
    # all three record types out of the tail in one read — each is normally
    # within ~25KB of the end — and only scan the whole file when a long run
    # of tool output has pushed them all out of that window.
    META=$(tail -c 262144 "$TRANSCRIPT" 2>/dev/null \
        | grep -E '^\{"type":"(custom-title|ai-title|agent-color)"')
    [ -n "$META" ] || META=$(grep -E '^\{"type":"(custom-title|ai-title|agent-color)"' \
        "$TRANSCRIPT" 2>/dev/null)

    # Newest record of one type.
    cb_pick() { printf '%s\n' "$META" | grep "^{\"type\":\"$1\"" | tail -1; }
    # Its string value. Both title keys end in `Title"`, and a record only
    # ever has one, so one greedy pattern covers both. Backslashes are
    # stripped because this script hand-rolls its JSON and a stray escape
    # would break the app's parse and drop the orb; quotes can't get this far,
    # the match stops at the first one.
    cb_value() {
        printf '%s' "$1" \
            | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
            | tr -d '\\'
    }

    TITLE=$(cb_value "$(cb_pick custom-title)" customTitle)
    [ -n "$TITLE" ] || TITLE=$(cb_value "$(cb_pick ai-title)" aiTitle)

    # Color names only — letters, nothing that could need escaping.
    COLOR=$(cb_value "$(cb_pick agent-color)" agentColor | tr -cd 'a-zA-Z')
fi

# Give this session a colour, the way Claude Code gives one itself.
#
# Off unless the installer was told to switch it on, because this writes to a
# file the app does not own. What it writes is not an invention: `/color`
# persists a colour by appending exactly this record to the transcript — read
# out of Claude Code's own saveAgentColor, which appends
# {"type":"agent-color","agentColor":…,"sessionId":…} and does nothing else —
# and the same records are read back into session state when a session resumes.
# So a colour set here is one Claude Code itself will agree with next time it
# loads that session, rather than a stand-in only this app can see. That
# distinction is exactly why the README declined to derive an accent, and why
# this is allowed to exist.
#
# Once per session: the moment the record is there, the read above finds it and
# this is skipped for the rest of the session's life. A colour the user later
# sets with /color wins for the same reason — theirs is appended later, and the
# read takes the newest.
#
# One short line, appended with >>. An O_APPEND write well under a page is
# atomic against the writer Claude Code has open on the same file, so this
# cannot land in the middle of one of its own rows.
if [ "$AGENT" = "claude" ] && [ "$AUTO_COLOR" = "1" ] && [ -z "$COLOR" ] \
   && [ -n "$SESSION_ID" ] && [ "$SESSION_ID" != "unknown" ] \
   && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
    # Keyed on the directory, not the session id. A session id is new every
    # run, so keying on it would repaint a project's orb a different colour
    # every time it was opened — worse than no colour, because it would look
    # like the colour meant something. The directory is what the saved orb
    # position is keyed by too, so a project keeps one colour.
    #
    # cksum because it is POSIX and gives the same answer on every machine and
    # every run, which is what "stable" has to mean for a value written into a
    # file that outlives the process.
    CB_HASH=$(printf '%s' "$CWD" | cksum | awk '{print $1}')

    # Names Claude Code accepts and the app knows how to draw. Deliberately
    # not the whole palette: grey and white read as "no colour" on an orb, and
    # the point of this is telling sessions apart.
    set -- red orange yellow green teal cyan blue purple violet magenta pink
    CB_INDEX=$(( CB_HASH % $# + 1 ))
    eval "COLOR=\${$CB_INDEX}"

    printf '{"type":"agent-color","agentColor":"%s","sessionId":"%s"}\n' \
        "$COLOR" "$SESSION_ID" >> "$TRANSCRIPT" 2>/dev/null || COLOR=""
fi

# What Codex calls this chat. It has both halves of what Claude Code has, in
# its own state database rather than in the transcript:
#
#   name   <- /rename, the name you chose
#   title  <- Codex's own, taken from the first thing you asked
#
# Same precedence as the Claude Code branch above, and for the same reason: a
# name you set by hand outranks a generated one however recently the generated
# one was written. There is still no /color equivalent, so COLOR stays empty and
# a Codex orb keeps the plain ring.
#
# Read from the database on every hook rather than cached in the status file,
# which is what the first version of this did. Caching was there to avoid
# grepping a multi-megabyte rollout, and a primary-key lookup costs nothing —
# measured at 7ms against the live file. It also means /rename shows up on the
# orb at the next hook instead of never, which is a limitation Claude Code
# sessions still have.
#
# Read-only, via a file: URI, so a hook can never take a write lock on a
# database Codex is using. Anything that goes wrong here — no sqlite3, a
# database mid-write, a schema this doesn't recognise — falls through to the
# rollout, which is the same answer by a slower route.
if [ "$AGENT" = "codex" ]; then
    # The filename carries a schema version (state_5.sqlite today) and will be
    # bumped by some future Codex. Take the highest rather than naming one, so
    # an upgrade degrades to the fallback below only if the *schema* changes,
    # not merely because the number did.
    CODEX_DB=$(ls -1 "${CODEX_HOME:-$HOME/.codex}"/state_*.sqlite 2>/dev/null | sort -V | tail -1)

    # The session id goes into a SQL string, and it arrives from a hook payload
    # this script does not control. Everything but hex and dashes is stripped —
    # a real thread id is a UUID, so nothing legitimate is lost, and there is
    # no quoting to get right.
    SAFE_ID=$(printf '%s' "$SESSION_ID" | tr -cd '0-9a-fA-F-')

    if [ -n "$CODEX_DB" ] && [ -n "$SAFE_ID" ] && command -v sqlite3 >/dev/null 2>&1; then
        TITLE=$(sqlite3 -readonly "file:$CODEX_DB?mode=ro" \
            "select coalesce(nullif(name,''), nullif(title,'')) from threads where id='$SAFE_ID';" \
            2>/dev/null | head -1)

        # Codex's own colour, which lives on a *section* rather than on a
        # thread: thread_sections.appearance is {"icon":…,"color":…} and a
        # thread points at one through thread_section_id. So a Codex orb is
        # coloured when its session has been filed under a coloured section and
        # not otherwise — which is the honest reading, because that is the only
        # colour Codex itself associates with the session.
        #
        # Read, never written. Creating a section to hold a colour would
        # reorganise the user's own thread list in Codex's sidebar, which is a
        # far larger side effect than a coloured ring is worth.
        COLOR=$(sqlite3 -readonly "file:$CODEX_DB?mode=ro" \
            "select s.appearance from threads t
               join thread_sections s on s.id = t.thread_section_id
              where t.id='$SAFE_ID';" 2>/dev/null \
            | sed -n 's/.*"color"[[:space:]]*:[[:space:]]*"\([a-zA-Z]*\)".*/\1/p' \
            | head -1)
    fi

    # Nothing to read, because most sessions are in no section at all. Derive
    # one instead, when the user has asked for colours.
    #
    # This is the one place a colour is invented rather than read, and the
    # asymmetry with Claude Code is deliberate rather than a shortcut. There,
    # deriving was refused for a specific reason — the terminal shows its own
    # accent, so a stand-in would visibly disagree with it — and the fix was to
    # write the record Claude Code itself reads back. Codex offers no such
    # record: its only colour belongs to a *section*, a grouping the user
    # arranges, and filing a session into one to carry a colour would rearrange
    # their sidebar. So the choice for Codex is a derived colour or none.
    #
    # A derived one is safe here precisely because of what is missing: Codex
    # shows no per-session colour anywhere, so there is nothing for this to
    # disagree with. It is keyed on the directory like Claude Code's, so the
    # same project is the same colour in both, and a real section colour above
    # always wins.
    if [ "$AUTO_COLOR" = "1" ] && [ -z "$COLOR" ] && [ -n "$CWD" ]; then
        CB_HASH=$(printf '%s' "$CWD" | cksum | awk '{print $1}')
        set -- red orange yellow green teal cyan blue purple violet magenta pink
        CB_INDEX=$(( CB_HASH % $# + 1 ))
        eval "COLOR=\${$CB_INDEX}"
    fi

    # No database, or nothing in it about this session yet — a thread is written
    # there a moment after it starts, so the very first hook of a session can
    # legitimately find nothing. The rollout has the same first message that
    # Codex's own title is built from, so this agrees with the database rather
    # than competing with it.
    if [ -z "$TITLE" ] && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
        # -m1 stops at the first match, and a UserMessage is within the first
        # handful of rows, so this reads the head of the file rather than the
        # file. The row is one line of compact JSON; the prompt is the first
        # "text" value inside the first UserMessage item.
        TITLE=$(grep -m1 '"type":"UserMessage"' "$TRANSCRIPT" 2>/dev/null \
            | sed -n 's/.*"type":"UserMessage".*"text":"\([^"]*\)".*/\1/p')
    fi

    # Whichever it came from, it has to survive being spliced into the JSON this
    # script hand-rolls, and it has to fit on an orb's tooltip.
    #
    # The two sed passes before the trim are not tidying. A first message is
    # several lines as often as not, and its newlines arrive as the two
    # characters \ and n — so stripping backslashes first would weld the lines
    # together with an "n" in the seam ("first linensecond line").
    if [ -n "$TITLE" ]; then
        TITLE=$(printf '%s' "$TITLE" \
            | sed 's/\\[nrt]/ /g' \
            | tr -d '\\' \
            | tr -s '[:space:]' ' ' \
            | cut -c1-60 \
            | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')

        # Cut at 60 characters lands mid-word about as often as not, and this is
        # a name rather than a summary — "remove all usa" reads as a bug in the
        # app. Back off to the last space, but only when that still leaves
        # something worth reading; a first word longer than 30 characters is a
        # path or a URL, and half of one is better than none of it.
        SHORTER="${TITLE% *}"
        if [ "$SHORTER" != "$TITLE" ] && [ "${#SHORTER}" -ge 30 ]; then
            TITLE="$SHORTER"
        fi
    fi
fi

# Grok's name lives in summary.json next to the updates.jsonl the payload
# already named, not in Claude-style custom-title rows. /rename sets
# title_is_manual and wins over generated_title — see docs/grok-findings.md.
# Colour is Codex-shaped: derived from cwd when asked, never written. Grok
# has /theme (TUI-wide) and no /color; appending an agent-color record into
# updates.jsonl was the accidental path this branch exists to stop.
if [ "$AGENT" = "grok" ]; then
    if [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
        GROK_SUMMARY="$(dirname "$TRANSCRIPT")/summary.json"
        if [ -f "$GROK_SUMMARY" ]; then
            GROK_MANUAL=$(grep -o '"title_is_manual"[[:space:]]*:[[:space:]]*true' \
                "$GROK_SUMMARY" 2>/dev/null | head -1)
            if [ -n "$GROK_MANUAL" ]; then
                TITLE=$(sed -n 's/.*"title"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
                    "$GROK_SUMMARY" 2>/dev/null | head -1)
            fi
            [ -n "$TITLE" ] || TITLE=$(sed -n \
                's/.*"generated_title"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
                "$GROK_SUMMARY" 2>/dev/null | head -1)
            [ -n "$TITLE" ] || TITLE=$(sed -n \
                's/.*"session_summary"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
                "$GROK_SUMMARY" 2>/dev/null | head -1)
        fi
    fi

    if [ "$AUTO_COLOR" = "1" ] && [ -z "$COLOR" ] && [ -n "$CWD" ]; then
        CB_HASH=$(printf '%s' "$CWD" | cksum | awk '{print $1}')
        set -- red orange yellow green teal cyan blue purple violet magenta pink
        CB_INDEX=$(( CB_HASH % $# + 1 ))
        eval "COLOR=\${$CB_INDEX}"
    fi

    if [ -n "$TITLE" ]; then
        TITLE=$(printf '%s' "$TITLE" \
            | sed 's/\\[nrt]/ /g' \
            | tr -d '\\' \
            | tr -s '[:space:]' ' ' \
            | cut -c1-60 \
            | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
        SHORTER="${TITLE% *}"
        if [ "$SHORTER" != "$TITLE" ] && [ "${#SHORTER}" -ge 30 ]; then
            TITLE="$SHORTER"
        fi
    fi
fi

# Identify the terminal hosting this session so a click on the orb can jump
# to it. This script runs inside the terminal's process tree, so:
# - inside tmux, the pane id is the only trustworthy coordinate (see below);
# - otherwise ITERM_SESSION_ID ("w0t0p0:UUID") pins the exact iTerm2 pane;
# - the controlling tty of the nearest ancestor that has one (the claude
#   TUI process — this hook itself runs on a pipe, not a tty) pins the
#   exact Terminal.app tab.
#
# tmux: $TMUX is "<socket>,<server pid>,<session index>" and $TMUX_PANE is a
# server-unique pane id like "%3". Both are inherited by this hook from the
# claude process running in the pane. Deliberately *not* recording
# ITERM_SESSION_ID in this case: inside tmux it's whatever was in the
# environment when the pane was created, which is stale as often as not, and
# jumping to the wrong pane is worse than not jumping at all. The pane's own
# tty is likewise a tmux pty, not a terminal tab, so the app resolves the
# real terminal from the attached tmux client at click time instead.
TMUX_SOCKET=""
TMUX_PANE_ID=""
TMUX_BIN=""
TERM_ID=""
#
# term_id is "the handle this terminal understands", not an iTerm2 field —
# which is what it has always meant, and what the three branches added below
# rely on. kitty and WezTerm both hand out a pane id and both ship a CLI that
# types into one, so they route exactly the way tmux does; recording the id is
# the whole of what this script has to do for them.
#
# kitty does not set TERM_PROGRAM at all, so this fills it in. That is not a
# lie about the environment: the field means "which terminal is this", and
# leaving it empty for the one terminal that declines to say would make it
# unaddressable for no better reason than a missing variable.
if [ -n "$TMUX" ]; then
    TMUX_SOCKET="${TMUX%%,*}"
    TMUX_PANE_ID="$TMUX_PANE"
    TMUX_BIN=$(command -v tmux 2>/dev/null)
elif [ -n "$ITERM_SESSION_ID" ]; then
    TERM_ID="${ITERM_SESSION_ID#*:}"
elif [ -n "$KITTY_WINDOW_ID" ]; then
    TERM_ID="$KITTY_WINDOW_ID"
    TERM_PROGRAM="${TERM_PROGRAM:-kitty}"
elif [ -n "$WEZTERM_PANE" ]; then
    TERM_ID="$WEZTERM_PANE"
    TERM_PROGRAM="${TERM_PROGRAM:-WezTerm}"
fi

# The ancestor that owns the tty is the claude TUI process itself, so the same
# walk that finds the tty also hands us the pid to record. The app uses it to
# tell "this session is still running" from "this file was left behind": a
# session killed with Ctrl+C never fires SessionEnd, so its file survives, and
# without a pid the only way to notice is to wait out the orb lifetime — which
# is forever if that is what you picked. See SessionManager.SessionGone.
#
# For Codex the walk looks for the codex process by name first, and only then
# for a tty. The two usually land on the same process and the distinction only
# shows up in one case — but it is a case that costs someone an orb they were
# using. Claude Code running `codex exec` as a Bash tool puts a codex process
# in a pipe with no tty of its own, so a tty-only walk would run straight past
# it and record *Claude's* pid. The app groups status files by pid to work out
# which one supersedes which, so the nested codex file would be the newest in
# Claude's bucket and would delete the live Claude orb.
# Claude Code gets the same by-name treatment Codex already had, and for a
# failure that turned out to be worse.
#
# A tty-only walk records the first ancestor that owns a terminal, which for a
# session with no terminal of its own is *somebody else's* process. Measured on
# a real machine: a session dispatched from `claude agents` walked past its own
# pid (no tty, being a background agent) and recorded the **viewer's** pid
# instead. The consequence is not a cosmetic mislabel — SessionManager reads a
# recorded pid as "is this session still alive", so when that session later
# ended, its orb stayed on screen forever pointing at a dead conversation,
# because the pid it named belonged to a viewer that was still running. Two orbs
# for one conversation, one of them a ghost with a stale transcript behind it.
#
# So the pid is now found by identifying this session's own process rather than
# by whoever happens to own a terminal. The claude binary reports its comm as
# either "claude" or a bare version ("2.1.240") depending on how it was
# launched — both observed on the same machine, which is why both are matched.
# Helpers that are also the claude binary are skipped: the background pty host,
# the daemon, and the agents viewer are none of them the session that fired this
# hook.
#
# A *claimed spare* is, and used to be skipped with them. A daemon-hosted
# background job has no other ancestors at all —
#
#   claude bg-spare      <- the process actually running the session
#   claude bg-pty-host   <- its pty wrapper
#   claude daemon run    <- the daemon
#   launchd
#
# — so skipping all three left every background job with no pid. `claude agents
# --json` names the spare as the job's pid, which settles what it is; and an
# unclaimed spare fires no hooks, so reaching one by walking up from a hook means
# it has been claimed and is this session.
#
# What separates it from its wrapper is the process **title**, not the command
# line, and that distinction is the whole reason this reads comm below. macOS
# reports argv[0] for comm, which for these is the full title ("claude bg-spare",
# not "claude") — so the old name test never matched them and the skip list was
# never even reached. It could not have decided it anyway: the pty host passes
# the spare's own argv after a `--`, so its command line contains `--bg-spare`
# too, and an args test for the spare matches the host as well.
#
# Roles this doesn't recognise fall through to the command line, which is where
# the layouts with no title live (an ordinary session, an older build). The two
# spare-ish patterns stay in that list, so an unfamiliar wrapper is skipped
# rather than guessed at — the same direction of caution as before.
#
# TTY is deliberately left to the old rule. What terminal to focus and what pid
# is alive are two different questions, and conflating them is what caused this;
# a background agent legitimately has no terminal of its own, and
# AgentTeamViewer already knows how to find the window you watch it in.
TTY=""
SESSION_PID=""
PID=$$
for _ in 1 2 3 4 5; do
    PID=$(ps -o ppid= -p "$PID" 2>/dev/null | tr -d ' ')
    { [ -z "$PID" ] || [ "$PID" = "0" ] || [ "$PID" = "1" ]; } && break

    T=$(ps -o tty= -p "$PID" 2>/dev/null | tr -d ' ')
    COMM=$(basename "$(ps -o comm= -p "$PID" 2>/dev/null)" 2>/dev/null)

    # The binary's own name, and the role word the title carries after it. A
    # title without one leaves ROLE empty, which is every shape that predates
    # the daemon.
    COMM_NAME="${COMM%% *}"
    ROLE="${COMM#"$COMM_NAME"}"
    ROLE="${ROLE# }"

    if [ "$AGENT" = "codex" ]; then
        if [ "$COMM_NAME" = "codex" ]; then
            SESSION_PID="$PID"
            [ -n "$T" ] && [ "$T" != "??" ] && TTY="$T"
            break
        fi
    elif [ "$AGENT" = "grok" ]; then
        # Same by-name walk Codex has, for the same nested-CLI reason: a
        # Claude Code session that shells out to grok would otherwise record
        # Claude's pid, and Grok firing the Claude-compat hook would record
        # whichever ancestor first owned a tty.
        if [ "$COMM_NAME" = "grok" ]; then
            SESSION_PID="$PID"
            [ -n "$T" ] && [ "$T" != "??" ] && TTY="$T"
            break
        fi
    elif [ -z "$SESSION_PID" ]; then
        # The claude binary, by either of the two names it reports.
        case "$COMM_NAME" in
            claude|[0-9]*.[0-9]*.[0-9]*)
                case "$ROLE" in
                    # A spare that a hook can see has been claimed, and is the
                    # session that ran this hook.
                    bg-spare) SESSION_PID="$PID" ;;

                    # Wrappers above the session, never the session.
                    bg-pty-host|daemon) ;;

                    *)
                        ARGS=$(ps -o args= -p "$PID" 2>/dev/null)
                        case "$ARGS" in
                            # Every one of these is the claude binary and none
                            # of them is the session that ran this hook.
                            *--bg-pty-host*|*--bg-spare*|*"daemon run"*|*" agents"*) ;;
                            *) SESSION_PID="$PID" ;;
                        esac
                        ;;
                esac
                ;;
        esac
    fi

    if [ -n "$T" ] && [ "$T" != "??" ]; then
        TTY="$T"

        # Only as a fallback, for a layout where no claude ancestor was
        # recognised — an older build, or a launcher this does not know about.
        # Better a terminal-owning ancestor than nothing, which is what this
        # rule was always doing.
        [ -z "$SESSION_PID" ] && SESSION_PID="$PID"
        break
    fi
done

mkdir -p "$DIR"

# "cli" is what tells the app which of the two wrote this. Written by name
# rather than left to be inferred from which fields happen to be empty — the
# app has an enum for it and the whole point is that a typo shouldn't be
# indistinguishable from a Claude Code session. A file from an older hook has
# no such key, which reads as Claude Code, which is what it was.
# "entrypoint" is how Claude Code says what launched it: "cli" in a terminal,
# "claude-desktop" when Claude Desktop's Code tab spawned it with no terminal at
# all. The app needs it to keep such a session's orb, which would otherwise be
# dropped for having nowhere to focus. Only Claude Code sets the variable, so a
# Codex or Grok file gets "" even if the hook inherited it from a parent
# claude. Reduced to [A-Za-z0-9_-] because it is interpolated into JSON by
# printf, and a quote or backslash in it would corrupt the file.
ENTRYPOINT=""
[ "$AGENT" = "claude" ] && ENTRYPOINT=$(printf '%s' "${CLAUDE_CODE_ENTRYPOINT:-}" | tr -cd 'A-Za-z0-9_-')

# "host_session_id" is Claude Desktop's own id for the chat
# (CLAUDE_CODE_HOST_SESSION_ID, "local_<uuid>"), which names the per-chat record
# the app reads to find a Remote Control bridge (CB-266). Same rules as
# entrypoint: Claude Code only, and reduced to [A-Za-z0-9_-] for printf.
HOST_SESSION_ID=""
[ "$AGENT" = "claude" ] && HOST_SESSION_ID=$(printf '%s' "${CLAUDE_CODE_HOST_SESSION_ID:-}" | tr -cd 'A-Za-z0-9_-')

printf '{"state":"%s","cli":"%s","entrypoint":"%s","host_session_id":"%s","cwd":"%s","title":"%s","color":"%s","term_program":"%s","term_id":"%s","tty":"%s","tmux_socket":"%s","tmux_pane":"%s","tmux_bin":"%s","session_pid":%s,"transcript_path":"%s"}' \
    "$STATE" "$AGENT" "$ENTRYPOINT" "$HOST_SESSION_ID" "$CWD" "$TITLE" "$COLOR" "$TERM_PROGRAM" "$TERM_ID" "$TTY" \
    "$TMUX_SOCKET" "$TMUX_PANE_ID" "$TMUX_BIN" "${SESSION_PID:-0}" "$TRANSCRIPT" > "$FILE"

exit 0
