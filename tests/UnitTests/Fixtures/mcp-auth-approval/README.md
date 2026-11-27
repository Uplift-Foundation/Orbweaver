CB-359 (remote OAuth form): the capture for McpAuthApprovalCaptureTests goes
here, made by tests/fixtures/mcp-auth/capture.sh (see the PR body). Commit it
unedited except that the OAuth code, state and token and the home directory are
replaced with `<CODE>`, `<STATE>`, `<TOKEN>` and `<HOME>`.

- pane.txt             `tmux capture-pane -p` while the session waits
- notifications.jsonl  every Notification hook payload, one JSON object per line
- status.json          the session's status file while it waits
- hook.log             the status hook log lines for that session
