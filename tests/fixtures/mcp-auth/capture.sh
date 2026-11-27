#!/bin/bash
# CB-359 capture, one runnable sequence (macOS, tmux, claude on PATH, Orbweaver
# hooks installed). NEVER approves the prompt. Prints the three artefacts.
# Scrub the code, state and token from the output before committing it.
set -u
W=/tmp/cap359; P=18765; S=cap359
tmux kill-session -t $S 2>/dev/null; pkill -f "fake_oauth_mcp.py $P" 2>/dev/null
rm -rf $W; mkdir -p $W/.claude; cd $W || exit 1
cp "${1:-$OLDPWD/tests/fixtures/mcp-auth}"/fake_oauth_mcp.py . || exit 1
python3 fake_oauth_mcp.py $P 2>$W/server.log & sleep 1
printf '{"mcpServers":{"fakeauth":{"type":"http","url":"http://127.0.0.1:%s/mcp"}}}' $P > mcp.json
printf '{"hooks":{"Notification":[{"hooks":[{"type":"command","command":"cat >> %s/notifications.jsonl; echo >> %s/notifications.jsonl"}]}]}}' $W $W > .claude/settings.json
tmux new-session -d -s $S -x 120 -y 50 -c $W "claude --permission-mode default --mcp-config $W/mcp.json"
adv() { for i in $(seq 1 30); do tmux capture-pane -t $S -p | grep -q -i "trust" && tmux send-keys -t $S Enter; tmux capture-pane -t $S -p | grep -q '❯' && break; sleep 2; done; }
adv
tmux send-keys -t $S 'Call the authenticate tool for the fakeauth MCP server and show me the authorization URL. Do nothing else.' Enter
for i in $(seq 1 30); do tmux capture-pane -t $S -p -J | grep -q 'http://127.0.0.1:'$P'/authorize' && break; sleep 3; done
URL=$(tmux capture-pane -t $S -p -J | tr -d '\n ' | grep -o 'http://127.0.0.1:'$P'/authorize[^"<>)]*' | head -1)
CB=$(curl -s -o /dev/null -w '%{redirect_url}' "$URL")
echo "authorize-url-found: $([ -n "$URL" ] && echo yes || echo no); callback-found: $([ -n "$CB" ] && echo yes || echo no)"
sleep 5
tmux send-keys -t $S -l "The sign-in finished. Call complete_authentication for fakeauth with this callback URL: $CB"
tmux send-keys -t $S Enter
sleep 25
echo "=== pane.txt"; tmux capture-pane -t $S -p
echo "=== notifications.jsonl"; cat $W/notifications.jsonl 2>/dev/null
R="$(getconf DARWIN_USER_TEMP_DIR)orbweaver"
echo "=== status files (newest)"; for f in $(ls -t $R/*.json 2>/dev/null | head -2); do echo "-- $f"; cat "$f"; echo; done
echo "=== hook.log (tail)"; tail -12 $R/hook.log
echo "=== server.log"; sed -E 's/(code|state)=[^ &"]*/\1=<X>/g' $W/server.log | tail -20
# leave the session pending on purpose; clean up the processes only:
tmux kill-session -t $S; pkill -f "fake_oauth_mcp.py $P"
