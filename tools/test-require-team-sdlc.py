#!/usr/bin/env python3
"""Tests for tools/require-team-sdlc.sh, the CB-273 PreToolUse hook.

    python3 tools/test-require-team-sdlc.py

Plain stdlib unittest, like the other tools/test-*.py. Every case pipes a real
PreToolUse payload into the real script, the way Claude Code does, and reads
the decision back off stdout: a deny is a JSON object with
permissionDecision "deny", an allow is no output at all. Each refusal is paired
with the passing case next to it, so a hook that denied everything (or nothing)
fails here rather than looking healthy.

Skips itself where bash or jq is missing, which is exactly where the hook
fails open by design.
"""
import json
import os
import shutil
import subprocess
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
HOOK = os.path.join(_HERE, "require-team-sdlc.sh")

FULL_BODY = (
    "## Summary\nCB-272\n\n"
    "## Verified\n- dotnet test tests/Tests.sln\n\n"
    "## Not verified\n- the Windows click\n\n"
    "Installed on: this MacBook and the mini; the Windows box was off.\n"
)


def find_bash():
    # Git Bash, not whatever "bash" is on PATH: on the Windows runner that is
    # WSL's System32\bash.exe, which cannot open a D:\ path and failed every
    # case with exit 1 before the hook ran. Claude Code itself runs hooks
    # through Git Bash on Windows, so that is the shell worth testing. Same
    # lookup as tools/test-build-macos-app.py.
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe",
                          r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
        return None
    return shutil.which("bash")


BASH = find_bash()


def decide(payload):
    """Runs the hook on one payload; returns the deny reason, or None for allow."""
    run = subprocess.run(
        [BASH, HOOK.replace("\\", "/")], input=json.dumps(payload),
        capture_output=True, text=True)
    if run.returncode != 0:
        raise AssertionError(f"hook exited {run.returncode}: {run.stderr.strip()}")
    out = run.stdout.strip()
    if not out:
        return None
    decision = json.loads(out)["hookSpecificOutput"]
    assert decision["permissionDecision"] == "deny", decision
    return decision["permissionDecisionReason"]


def agent(**tool_input):
    return {"tool_name": "Agent", "tool_input": tool_input}


def bash(command):
    return {"tool_name": "Bash", "tool_input": {"command": command}}


@unittest.skipUnless(BASH and shutil.which("jq"),
                     "needs bash and jq; the hook fails open without them")
class AgentSpawns(unittest.TestCase):

    def test_named_spawn_with_worktree_isolation_is_refused(self):
        # The CB-272 shape: this is what made the engineer a subagent.
        reason = decide(agent(name="nadia-okafor", subagent_type="cb-teammate",
                              isolation="worktree"))
        self.assertIn("SUBAGENT", reason)

    def test_named_spawn_in_the_background_is_refused(self):
        self.assertIsNotNone(decide(agent(name="wren-asare", run_in_background=True)))

    def test_plain_named_spawn_is_a_teammate_and_allowed(self):
        self.assertIsNone(decide(agent(name="idris-belanger", model="fable")))

    def test_explicit_false_background_on_a_named_spawn_is_allowed(self):
        self.assertIsNone(decide(agent(name="idris-belanger", run_in_background=False)))

    def test_unnamed_cb_teammate_is_refused(self):
        self.assertIn("cb-teammate", decide(agent(subagent_type="cb-teammate")))

    def test_unnamed_helper_may_still_run_in_the_background(self):
        # Explore/Plan helpers are not team members; nothing here is about them.
        self.assertIsNone(decide(agent(subagent_type="Explore", run_in_background=True)))


@unittest.skipUnless(BASH and shutil.which("jq"),
                     "needs bash and jq; the hook fails open without them")
class PullRequests(unittest.TestCase):

    def test_create_with_a_bare_body_names_everything_missing(self):
        reason = decide(bash('gh pr create --base develop --body "adds tiles"'))
        for part in ("CB ticket", "## Verified", "## Not verified", "Installed on"):
            self.assertIn(part, reason)

    def test_create_with_a_full_body_file_is_allowed(self):
        with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False) as fh:
            fh.write(FULL_BODY)
        try:
            self.assertIsNone(decide(bash(f"gh pr create --base develop --body-file {fh.name}")))
        finally:
            os.unlink(fh.name)

    def test_create_without_the_install_line_is_refused(self):
        with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False) as fh:
            fh.write(FULL_BODY.replace("Installed on", "Built on"))
        try:
            self.assertIn("Installed on",
                          decide(bash(f"gh pr create --base develop --body-file {fh.name}")))
        finally:
            os.unlink(fh.name)

    def test_approve_naming_one_rid_is_refused(self):
        self.assertIn("both rids",
                      decide(bash('gh pr review 9 --approve --body "fine on osx-arm64"')))

    def test_approve_naming_both_rids_is_allowed(self):
        self.assertIsNone(decide(bash(
            'gh pr review 9 --approve --body "osx-arm64 confirmed, win-x64 confirmed"')))

    def test_a_comment_review_is_not_an_approval(self):
        self.assertIsNone(decide(bash('gh pr review 9 --comment --body "looked"')))

    def test_prose_that_mentions_the_command_is_not_a_pr(self):
        # This hook's own commit message mentioned the command inside a
        # heredoc, and the first version refused the commit.
        self.assertIsNone(decide(bash(
            "git commit -F - <<'EOF'\nDenies a gh pr create whose body lacks sections.\nEOF")))

    def test_create_after_a_separator_is_still_checked(self):
        self.assertIsNotNone(decide(bash('cd repo && gh pr create --body "x"')))

    def test_unrelated_commands_pass_through(self):
        self.assertIsNone(decide(bash("git status")))


if __name__ == "__main__":
    unittest.main()
