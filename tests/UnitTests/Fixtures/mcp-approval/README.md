CB-359: capture target. Not yet captured.

Commit, unedited, from a terminal-hosted Claude Code session stopped on an MCP
tool approval ("Allow Claude to use <tool> (<server>)?", Deny / Allow once):

- pane.txt          `tmux capture-pane -p` of that session while it waits
- notification.json the Notification hook stdin JSON delivered while it waits
                    (scrubbed: no token, OAuth code/state or JWT)
- status.json       the session's status file while it waits
