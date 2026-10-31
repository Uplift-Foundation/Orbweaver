#!/bin/bash
# PreToolUse hook for Claude Code sessions in this repo (matchers: Agent,
# Bash), registered in .claude/settings.json (CB-273).
#
# Enforces the parts of CLAUDE.md's "How a feature gets built" that a session
# has been seen skipping, as refusals rather than reminders. A memory or a
# CLAUDE.md paragraph is weighed against everything else in context; a deny is
# not (2026-10-21, CB-272: engineer and QA spawned as in-process background
# subagents with no architect stage, after the same lesson had been noted).
#
# Agent:
#   - a NAMED spawn may not carry isolation or run_in_background. Either one
#     turns it into an in-process subagent instead of a team member process
#     (--team-name / --parent-session-id), so it never joins the team and
#     never draws as a linked orb. Teammates make their own worktrees.
#   - subagent_type cb-teammate must be named (it only exists for team members).
# Bash:
#   - gh pr create: body must name a CB ticket and carry "Verified",
#     "Not verified" and "Installed on" sections.
#   - gh pr review --approve: body must name both rids.
#
# Fails open: without jq it cannot read the tool call, and a hook that blocks
# every Agent and Bash call on a machine missing one tool is worse than one
# that enforces nothing there. Exit 0 with no output is "allow".
command -v jq >/dev/null 2>&1 || exit 0

input=$(cat)
tool=$(jq -r '.tool_name // empty' <<<"$input")

deny() {
  jq -n --arg r "$1" '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"deny",permissionDecisionReason:$r}}'
  exit 0
}

if [ "$tool" = "Agent" ]; then
  name=$(jq -r '.tool_input.name // empty' <<<"$input")
  type=$(jq -r '.tool_input.subagent_type // empty' <<<"$input")
  iso=$(jq -r '.tool_input.isolation // empty' <<<"$input")
  bg=$(jq -r '.tool_input.run_in_background // false' <<<"$input")

  if [ "$type" = "cb-teammate" ] && [ -z "$name" ]; then
    deny "cb-teammate is for agent-team members only: give it a name (an existing .profiles-assets slug). CLAUDE.md: features are built by an agent team, not subagents."
  fi
  if [ -n "$name" ] && { [ -n "$iso" ] || [ "$bg" = "true" ]; }; then
    deny "Named spawn '$name' has isolation/run_in_background, which makes it an in-process SUBAGENT, not a team member process. Drop both: a plain named spawn becomes a teammate (--team-name). CLAUDE.md order: PM ticket -> architect plan (fable for hook/transcript contracts) -> engineer(s) + QA loop -> install on all three machines -> PM review of both rids."
  fi
  exit 0
fi

if [ "$tool" = "Bash" ]; then
  cmd=$(jq -r '.tool_input.command // empty' <<<"$input")

  # The body can be inline (--body) or in a file (--body-file / -F); read
  # whichever is present so the check sees what GitHub will.
  body_of() {
    local f
    f=$(grep -oE -- '(--body-file|-F)[ =]+[^ ]+' <<<"$cmd" | head -1 | sed -E 's/^(--body-file|-F)[ =]+//; s/^["'\'']//; s/["'\'']$//')
    if [ -n "$f" ] && [ -f "$f" ]; then cat "$f"; else printf '%s' "$cmd"; fi
  }

  # Anchored to the start of a line or a command separator, never a bare
  # space: a commit message or heredoc that merely *mentions* the command
  # (this hook's own commit did) is not a PR being opened.
  if grep -qE '(^|[;&|(])[[:space:]]*gh[[:space:]]+pr[[:space:]]+create' <<<"$cmd"; then
    body=$(body_of)
    missing=()
    grep -qE 'CB-[0-9]+' <<<"$body" || missing+=("a CB ticket key")
    grep -qiE '^#+ *verified' <<<"$body" || missing+=("a '## Verified' section")
    grep -qiE '^#+ *not verified' <<<"$body" || missing+=("a '## Not verified' section")
    grep -qiE 'installed on' <<<"$body" || missing+=("an 'Installed on' line naming which of the three machines (MacBook, mini, Windows) it was installed on, and which not")
    [ ${#missing[@]} -eq 0 ] || deny "PR body is missing: $(IFS=';'; echo "${missing[*]}"). CLAUDE.md: say what was verified and what wasn't, and install on all three machines before CI."
  fi

  if grep -qE '(^|[;&|(])[[:space:]]*gh[[:space:]]+pr[[:space:]]+review' <<<"$cmd" && grep -qE -- '--approve|(^| )-a( |$)' <<<"$cmd"; then
    body=$(body_of)
    grep -q 'osx-arm64' <<<"$body" && grep -q 'win-x64' <<<"$body" \
      || deny "Approving review must name both rids (osx-arm64 and win-x64) and what was confirmed on each. CLAUDE.md: review the screenshot comment for BOTH rids before approving."
  fi
fi
exit 0
