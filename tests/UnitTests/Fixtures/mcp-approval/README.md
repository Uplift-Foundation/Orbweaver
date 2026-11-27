CB-359: a real capture of an MCP tool approval, made on a Mac mini at the PR 434
head (60a7efc) from a terminal Claude Code session in default permission mode
with a local stdio MCP server that was not pre-allowed.

- pane.txt          `tmux capture-pane -p` while the session waits, as captured
- notification.json the Notification hook stdin JSON delivered while it waits
- status.json       the session's status file while it waits
- hook.log          the status hook log (`generating` then `waiting`)

Unedited except that the home directory in `transcript_path` is replaced with
`<HOME>`. Nothing else needed removing: no hostname, token, OAuth code/state or
JWT was present.
