#!/bin/bash
# CB-359 capture, one runnable sequence (macOS, tmux, claude on PATH, Orbweaver
# hooks installed). NEVER approves the prompt. Prints the joined pane at each
# stage and the three artefacts. Stops with exit 2 and a message when the
# authorize URL or the callback URL is empty, instead of typing into the session.
# Scrub the code, state and token from the output before committing it.
#   capture.sh [dir holding fake_oauth_mcp.py and extract_url.py]
set -u
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SRC=${1:-$HERE}
W=/tmp/cap359; P=18765; S=cap359
cleanup() { tmux kill-session -t $S 2>/dev/null; pkill -f "fake_oauth_mcp.py $P" 2>/dev/null; }
pane() { tmux capture-pane -t $S -p -J -S -200; }   # -J joins tmux-wrapped lines
stage() { echo "=== pane after: $1"; pane; }
die() { echo "CAPTURE FAILED: $1" >&2; echo "CAPTURE FAILED: $1"; stage "failure"; echo "=== server.log"; sed -E 's/(code|state)=[^ &"]*/\1=<X>/g' $W/server.log | tail -20; cleanup; exit 2; }
cleanup; rm -rf $W; mkdir -p $W/.claude; cd $W || exit 1
cp "$SRC/fake_oauth_mcp.py" "$SRC/extract_url.py" . || exit 1
python3 fake_oauth_mcp.py $P 2>$W/server.log & sleep 1
printf '{"mcpServers":{"fakeauth":{"type":"http","url":"http://127.0.0.1:%s/mcp"}}}' $P > mcp.json
printf '{"hooks":{"Notification":[{"hooks":[{"type":"command","command":"cat >> %s/notifications.jsonl; echo >> %s/notifications.jsonl"}]}]}}' $W $W > .claude/settings.json
tmux new-session -d -s $S -x 120 -y 50 -c $W "claude --permission-mode default --mcp-config $W/mcp.json"
for i in $(seq 1 30); do
  pane | grep -q -i "trust" && tmux send-keys -t $S Enter
  pane | grep -q '❯' && break
  sleep 2
done
stage "start"
tmux send-keys -t $S 'Call the authenticate tool for the fakeauth MCP server and show me the authorization URL. Do nothing else.' Enter
URL=
for i in $(seq 1 40); do
  URL=$(pane | python3 extract_url.py $P) && break
  URL=; sleep 3
done
stage "authenticate"
[ -n "$URL" ] || die "no complete authorize URL in the pane (extract_url.py found none)"
CID=$(sed -n 's/.*registered client_id=\(dummy-client-[0-9a-f]*\).*/\1/p' $W/server.log | tail -1)
case "$URL" in *"client_id=$CID"*) ;; *) die "authorize URL does not carry the registered client_id '$CID'";; esac
CB=$(curl -s -o /dev/null -w '%{redirect_url}' "$URL")
echo "authorize-url-found: yes; callback-found: $([ -n "$CB" ] && echo yes || echo no)"
case "$CB" in *"code="*) ;; *) die "the fake /authorize gave no callback with a code (got '${CB:+non-empty, no code}')";; esac
sleep 5
tmux send-keys -t $S -l "The sign-in finished. Call complete_authentication for fakeauth with this callback URL: $CB"
tmux send-keys -t $S Enter
for i in 1 2 3 4 5; do sleep 5; stage "complete_authentication, +$((i*5))s" | sed -E 's/(code|state)=[^ &"]*/\1=<X>/g'; done
echo "=== pane.txt"; tmux capture-pane -t $S -p
echo "=== notifications.jsonl"; cat $W/notifications.jsonl 2>/dev/null
R="$(getconf DARWIN_USER_TEMP_DIR)orbweaver"
echo "=== status files (newest)"; for f in $(ls -t $R/*.json 2>/dev/null | head -2); do echo "-- $f"; cat "$f"; echo; done
echo "=== hook.log (tail)"; tail -12 $R/hook.log
echo "=== server.log"; sed -E 's/(code|state)=[^ &"]*/\1=<X>/g' $W/server.log | tail -20
# leave the session pending on purpose; clean up the processes only:
cleanup
