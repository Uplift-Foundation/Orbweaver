#!/bin/bash
# CB-359 capture, one runnable sequence (macOS, tmux, claude on PATH, Orbweaver
# hooks installed). NEVER approves the prompt. Leaves nothing behind in the
# live account: the server (named cb359cap) binds 127.0.0.1 on a port the system
# picks, reaches the session only through the scratch --mcp-config with
# --strict-mcp-config (never `claude mcp add`), and its token step always
# refuses. The session uses the machine's own default login. The last lines stop
# the server and the tmux session and print what, if anything, was left behind.
# Exits 2 with `CAPTURE FAILED: <stage>` and the server's stderr on any failure.
# Scrub the code and state from the output before committing it.
#   capture.sh [dir holding fake_oauth_mcp.py and extract_url.py]
set -u
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SRC=${1:-$HERE}
W=/tmp/cap359; S=cap359; SPID=; P=
scrub() { sed -E 's/(code|state)=[^ &"]*/\1=<X>/g'; }
cleanup() { tmux kill-session -t $S 2>/dev/null; [ -n "$SPID" ] && kill "$SPID" 2>/dev/null; }
leftover() {
  cleanup; echo "stopped: server (pid ${SPID:-none}) and tmux session $S"
  f=$(grep -l "cb359cap" ~/.claude*/*.json ~/.claude*/.*.json ~/.claude.json 2>/dev/null); echo "left behind: ${f:-none}"
}
pane() { tmux capture-pane -t $S -p -J -S -200; }   # -J joins tmux-wrapped lines
stage() { echo "=== pane after: $1"; pane; }
# --- submit logic (selftest.sh runs this block against a fake prompt in tmux)
# The prompt box is the last line holding the prompt mark; once a prompt is
# submitted that line is empty again. Tool-call lines start with the record mark.
box_has() { pane | grep '❯' | tail -1 | grep -qF -- "$1"; }
tool_lines() { pane | grep -c '^[[:space:]]*⏺' || true; }
# submit <text>: type it literally, wait, send Enter on its own, check the box
# emptied; one more Enter if not; otherwise fail loudly.
submit() {
  local text=$1 marker=${1:0:30}
  tmux send-keys -t $S -l -- "$text"; sleep ${SUBMIT_WAIT:-1}
  tmux send-keys -t $S Enter; sleep ${SUBMIT_SETTLE:-3}
  if box_has "$marker"; then
    echo "submit: text still in the input box after Enter, sending one more Enter"
    tmux send-keys -t $S Enter; sleep ${SUBMIT_SETTLE:-3}
    box_has "$marker" && die "prompt not submitted (still in the input box after two Enters)"
  fi
  echo "submit: prompt left the input box; tool-call lines in pane: $(tool_lines)"
}
# --- end submit logic
die() {
  echo "CAPTURE FAILED: $1" >&2; echo "CAPTURE FAILED: $1"
  stage "failure" 2>/dev/null
  echo "=== server stderr"; scrub < $W/server.log | tail -30
  echo "=== server stdout"; cat $W/server.out 2>/dev/null
  leftover; exit 2
}
cleanup; rm -rf $W; mkdir -p $W/.claude; cd $W || exit 1
cp "$SRC/fake_oauth_mcp.py" "$SRC/extract_url.py" . || exit 1
python3 fake_oauth_mcp.py >$W/server.out 2>$W/server.log & SPID=$!
for i in 1 2 3 4 5 6 7 8 9 10; do
  P=$(sed -n 's/^PORT=//p' $W/server.out | head -1); [ -n "$P" ] && break; sleep 1
done
[ -n "$P" ] || die "the server printed no port (it did not start)"
echo "server: pid $SPID port $P"
code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$P/mcp -X POST -H 'Content-Type: application/json' -d '{}')
[ "$code" = 401 ] || die "server not up: POST /mcp gave HTTP '$code', wanted 401"
echo "server check: POST /mcp -> $code"
printf '{"mcpServers":{"cb359cap":{"type":"http","url":"http://127.0.0.1:%s/mcp"}}}' $P > mcp.json
printf '{"hooks":{"Notification":[{"hooks":[{"type":"command","command":"cat >> %s/notifications.jsonl; echo >> %s/notifications.jsonl"}]}]}}' $W $W > .claude/settings.json
f=$(grep -l "cb359cap" ~/.claude*/*.json ~/.claude*/.*.json ~/.claude.json 2>/dev/null); echo "left behind before start: ${f:-none}"
tmux new-session -d -s $S -x 120 -y 50 -c $W "claude --permission-mode default --strict-mcp-config --mcp-config $W/mcp.json"
ready=
for i in $(seq 1 30); do
  pane | grep -q -i "trust" && tmux send-keys -t $S Enter
  pane | grep -q '❯' && { ready=1; break; }
  sleep 2
done
stage "start"
[ -n "$ready" ] || die "claude never showed its prompt in 60s"
submit 'Call the authenticate tool of the MCP server you have and show me the authorization URL. Do nothing else.'
URL=
# up to ~5 min: keep waiting while the pane still shows it is generating
for i in $(seq 1 100); do
  URL=$(pane | python3 extract_url.py $P) && break
  URL=
  sleep 3
done
stage "authenticate"
if [ -z "$URL" ]; then
  if pane | grep -q -E 'esc to interrupt|✻|✶|✳|Thinking|…'; then echo "pane still shows generating after 5 min"; fi
  grep -q 'POST /mcp\|GET /mcp' $W/server.log || echo "claude never contacted the server (nothing in server.log)"
  die "no complete authorize URL in the pane (extract_url.py found none); tool-call lines in pane: $(tool_lines)"
fi
CID=$(sed -n 's/.*registered client_id=\(dummy-client-[0-9a-f]*\).*/\1/p' $W/server.log | tail -1)
case "$URL" in *"client_id=$CID"*) ;; *) die "authorize URL does not carry the registered client_id '$CID'";; esac
CB=$(curl -s -o /dev/null -w '%{redirect_url}' "$URL")
echo "authorize-url-found: yes; callback-found: $([ -n "$CB" ] && echo yes || echo no)"
case "$CB" in *"code="*) ;; *) die "the fake /authorize gave no callback with a code (got '${CB:+non-empty, no code}')";; esac
sleep 5
submit "The sign-in finished. Call the complete_authentication tool of that MCP server with this callback URL: $CB"
for i in 1 2 3 4 5 6; do sleep 5; stage "complete_authentication, +$((i*5))s" | scrub; done
echo "=== pane.txt"; tmux capture-pane -t $S -p | scrub
echo "=== notifications.jsonl"; cat $W/notifications.jsonl 2>/dev/null
R="$(getconf DARWIN_USER_TEMP_DIR)orbweaver"
echo "=== status files (newest)"; for f in $(ls -t $R/*.json 2>/dev/null | head -2); do echo "-- $f"; cat "$f"; echo; done
echo "=== hook.log (tail)"; tail -12 $R/hook.log
echo "=== server.log"; scrub < $W/server.log | tail -20
# the prompt is never answered; stop everything and show what was left behind
leftover
