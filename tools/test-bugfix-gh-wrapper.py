#!/usr/bin/env python3
"""Tests for the bug-fix org's `gh` and `git` stand-ins (CB-478, CB-489).

    python3 tools/test-bugfix-gh-wrapper.py

Each test puts a stub `gh` (or `git`) that records what it saw on a temporary
PATH behind the wrapper. The negative control for every "the stub sees the
token" check is the same run without the wrapper, where the stub sees none.
"""
import os
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.realpath(os.path.join(HERE, "..", "paperclip", "bugfix-org", "host", "shims"))
APP = "app-token-value"
WORKFLOW = "workflow-token-value"

STUB = """#!/bin/bash
echo "token=${GH_TOKEN-UNSET}"
echo "wf=${BUGFIX_GH_WORKFLOW_TOKEN-UNSET}"
echo "params=${GIT_CONFIG_PARAMETERS-UNSET}"
echo "argc=$#"
for a in "$@"; do echo "arg=$a"; done
IFS= read -r -d "" in; echo "stdin=${in%$'\n'}"
exit ${STUB_EXIT:-0}
"""


class Wrapper(unittest.TestCase):
    tool = "gh"

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp)
        self.real = os.path.join(self.tmp, "real")
        os.mkdir(self.real)
        stub = os.path.join(self.real, self.tool)
        with open(stub, "w") as f:
            f.write(STUB)
        os.chmod(stub, 0o755)
        self.base = {"HOME": self.tmp, "BUGFIX_GH_TOKEN": APP,
                     "BUGFIX_GH_WORKFLOW_TOKEN": WORKFLOW}

    def run_tool(self, path, extra=None, args=(), stdin="", wrapped=True):
        env = dict(self.base, PATH=path, **(extra or {}))
        exe = os.path.join(BIN, self.tool) if wrapped else self.tool
        return subprocess.run([exe, *args], env=env, input=stdin, text=True,
                              capture_output=True, timeout=20)

    def seen(self, r, key):
        return [l.split("=", 1)[1] for l in r.stdout.splitlines() if l.startswith(key + "=")][0]


class GhWrapper(Wrapper):
    def test_negative_control_without_wrapper_stub_sees_no_token(self):
        r = self.run_tool(self.real + ":/usr/bin:/bin", wrapped=False)
        self.assertEqual(self.seen(r, "token"), "UNSET")

    def test_sets_token_from_bugfix_token_when_unset(self):
        r = self.run_tool(BIN + ":" + self.real + ":/usr/bin:/bin")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.seen(r, "token"), APP)

    def test_blank_token_counts_as_unset(self):
        r = self.run_tool(BIN + ":" + self.real, {"GH_TOKEN": "", "GITHUB_TOKEN": ""})
        self.assertEqual(self.seen(r, "token"), APP)

    def test_existing_token_is_not_overridden(self):
        r = self.run_tool(BIN + ":" + self.real, {"GH_TOKEN": "caller-token"})
        self.assertEqual(self.seen(r, "token"), "caller-token")

    def test_workflow_token_is_never_used(self):
        r = self.run_tool(BIN + ":" + self.real, {"BUGFIX_GH_TOKEN": ""})
        self.assertEqual(self.seen(r, "token"), "UNSET")

    def test_args_stdin_and_exit_code_pass_through(self):
        r = self.run_tool(BIN + ":" + self.real, {"STUB_EXIT": "7"},
                          ["pr", "create", "--title", "a b", ""], "in put\n")
        self.assertEqual(r.returncode, 7)
        self.assertEqual(self.seen(r, "argc"), "5")
        self.assertIn("arg=a b", r.stdout)
        self.assertIn("stdin=in put", r.stdout)

    def test_token_never_in_wrapper_output(self):
        # The stub deliberately prints the token, so check the wrapper's own
        # output with a stub that does not.
        quiet = os.path.join(self.real, "gh")
        with open(quiet, "w") as f:
            f.write("#!/bin/bash\necho ok; echo err >&2\n")
        r = self.run_tool(BIN + ":" + self.real)
        self.assertEqual((r.stdout, r.stderr), ("ok\n", "err\n"))
        r = self.run_tool(BIN + ":/nonexistent")
        self.assertEqual(r.returncode, 127)
        self.assertNotIn(APP, r.stdout + r.stderr)

    def test_does_not_loop_when_first_on_path(self):
        # Wrapper first, twice (a symlinked path and a trailing slash), then an
        # empty entry, then the real one. A loop would never return.
        link = os.path.join(self.tmp, "link")
        os.symlink(BIN, link)
        r = subprocess.run([os.path.join(BIN, "gh")], timeout=20, text=True, capture_output=True, stdin=subprocess.DEVNULL,
                           env=dict(self.base, PATH=f"{BIN}:{link}:{BIN}/::{self.real}"))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("token=" + APP, r.stdout)

    def test_only_wrapper_on_path_fails_instead_of_looping(self):
        r = subprocess.run([os.path.join(BIN, "gh")], timeout=20, text=True, capture_output=True, stdin=subprocess.DEVNULL,
                           env=dict(self.base, PATH=BIN))
        self.assertEqual(r.returncode, 127)


class GitWrapper(Wrapper):
    tool = "git"

    def test_sets_credential_helper_bot_author_and_token(self):
        r = self.run_tool(BIN + ":" + self.real)
        self.assertEqual(self.seen(r, "token"), APP)
        p = self.seen(r, "params")
        self.assertIn("credential.https://github.com.helper=!gh auth git-credential", p)
        self.assertIn("user.name=orbweaver-bugfix[bot]", p)
        self.assertNotIn(APP, p)

    def test_negative_control_without_wrapper(self):
        r = self.run_tool(self.real, wrapped=False)
        self.assertEqual((self.seen(r, "token"), self.seen(r, "params")), ("UNSET", "UNSET"))

    def test_caller_settings_win(self):
        r = self.run_tool(BIN + ":" + self.real, {"GH_TOKEN": "t", "GIT_CONFIG_PARAMETERS": "'a=b'"})
        self.assertEqual((self.seen(r, "token"), self.seen(r, "params")), ("t", "'a=b'"))

    def test_nothing_set_without_bugfix_token(self):
        r = self.run_tool(BIN + ":" + self.real, {"BUGFIX_GH_TOKEN": ""})
        self.assertEqual((self.seen(r, "token"), self.seen(r, "params")), ("UNSET", "UNSET"))

    def test_args_and_exit_code_pass_through_and_no_loop(self):
        r = self.run_tool(BIN + ":" + BIN + ":" + self.real, {"STUB_EXIT": "3"}, ["commit", "-m", "x y"])
        self.assertEqual(r.returncode, 3)
        self.assertIn("arg=x y", r.stdout)

# CB-489. GitHub, as far as these stubs know, accepts only the token in
# $STUB_LIVE (a file); anything else gets gh's real 401 shape: the body on
# stdout, "Bad credentials (HTTP 401)" on stderr, exit 1. With STUB_ROTATE set,
# the first call plays the warden minting mid-command: it moves a new token
# into the spool file and the live file, then fails with the one it was given.
GITHUB_STUB = r"""#!/bin/bash
n=$(( $(cat "$STUB_CALLS" 2>/dev/null || echo 0) + 1 )); echo $n > "$STUB_CALLS"
IFS= read -r -d "" in
if [ -n "$STUB_ROTATE" ] && [ $n = 1 ]; then
  printf 'rotated\n' > "$BUGFIX_SPOOL/tokens/$STUB_ROTATE"; printf 'rotated' > "$STUB_LIVE"
fi
if [ "${GH_TOKEN-}" != "$(cat "$STUB_LIVE")" ]; then
  echo '{"message": "Bad credentials", "status": "401"}'
  echo "$STUB_401" >&2
  exit 1
fi
echo "ok call=$n stdin=${in%$'\n'}"
exit ${STUB_EXIT:-0}
"""
DEAD, LIVE, WF_DEAD, WF_LIVE = "dead-everyday", "live-everyday", "dead-workflow", "live-workflow"


class FreshTokenGh(unittest.TestCase):
    """A run's BUGFIX_GH_TOKEN copy is dead; the warden's file is live."""
    tool, msg401 = "gh", "gh: Bad credentials (HTTP 401)"

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp)
        self.real = os.path.join(self.tmp, "real")
        self.spool = os.path.join(self.tmp, "spool")
        self.scratch = os.path.join(self.tmp, "scratch")
        for d in (self.real, os.path.join(self.spool, "tokens"), self.scratch):
            os.makedirs(d)
        stub = os.path.join(self.real, self.tool)
        with open(stub, "w") as f:
            f.write(GITHUB_STUB)
        os.chmod(stub, 0o755)
        self.write("gh-token", LIVE + "\n")
        self.write("gh-workflow-token", WF_LIVE + "\n")
        self.live = os.path.join(self.tmp, "live")
        self.set_live(LIVE)
        self.calls = os.path.join(self.tmp, "calls")

    def write(self, name, text):
        with open(os.path.join(self.spool, "tokens", name), "w") as f:
            f.write(text)

    def set_live(self, token):
        with open(self.live, "w") as f:
            f.write(token)

    def run_tool(self, extra=None, args=("api", "user"), stdin="in put\n", wrapped=True):
        env = {"HOME": self.tmp, "TMPDIR": self.scratch, "PATH": f"{BIN}:{self.real}:/usr/bin:/bin",
               "BUGFIX_GH_TOKEN": DEAD, "BUGFIX_GH_WORKFLOW_TOKEN": WF_DEAD, "BUGFIX_SPOOL": self.spool,
               "STUB_LIVE": self.live, "STUB_CALLS": self.calls, "STUB_401": self.msg401}
        env.update(extra or {})
        if not wrapped:
            env["PATH"] = f"{self.real}:/usr/bin:/bin"
        exe = os.path.join(BIN, self.tool) if wrapped else self.tool
        return subprocess.run([exe, *args], env=env, input=stdin, text=True, capture_output=True, timeout=20)

    def ncalls(self):
        with open(self.calls) as f:
            return int(f.read())

    # -- the bug, and the fix

    def test_negative_control_the_skill_prefix_without_the_wrapper_gets_401(self):
        # What QAF-209 did: GH_TOKEN="$BUGFIX_GH_TOKEN", a copy taken at start.
        r = self.run_tool({"GH_TOKEN": DEAD}, wrapped=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn(self.msg401, r.stderr)

    def test_the_skill_prefix_through_the_wrapper_gets_the_live_token(self):
        r = self.run_tool({"GH_TOKEN": DEAD})
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout, "ok call=1 stdin=in put\n")

    def test_a_bare_call_gets_the_live_token(self):
        r = self.run_tool()
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_the_workflow_copy_is_swapped_for_the_live_workflow_token(self):
        self.set_live(WF_LIVE)
        r = self.run_tool({"GH_TOKEN": WF_DEAD})
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_the_workflow_token_is_never_handed_to_an_everyday_call(self):
        self.set_live(WF_LIVE)
        for extra in ({}, {"GH_TOKEN": DEAD}, {"GH_TOKEN": ""}):
            self.assertEqual(self.run_tool(extra).returncode, 1, extra)

    def test_a_token_the_caller_chose_is_kept(self):
        self.set_live("own")
        self.assertEqual(self.run_tool({"GH_TOKEN": "own"}).returncode, 0)
        self.set_live(LIVE)
        self.assertEqual(self.run_tool({"GH_TOKEN": "own"}).returncode, 1, "not swapped, not retried")
        self.assertEqual(self.ncalls(), 2)

    def test_no_file_falls_back_to_the_start_of_run_copy(self):
        os.remove(os.path.join(self.spool, "tokens", "gh-token"))
        self.set_live(DEAD)
        self.assertEqual(self.run_tool().returncode, 0)
        self.assertEqual(self.run_tool({"BUGFIX_SPOOL": ""}).returncode, 0)

    def test_an_empty_file_is_ignored(self):
        self.write("gh-token", "")
        self.set_live(DEAD)
        self.assertEqual(self.run_tool().returncode, 0)

    # -- the 401 retry

    def test_a_401_after_a_mid_command_mint_is_retried_once_with_the_new_token(self):
        r = self.run_tool({"GH_TOKEN": DEAD, "STUB_ROTATE": "gh-token"})
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(self.ncalls(), 2)
        self.assertEqual(r.stdout, "ok call=2 stdin=in put\n", "the failed attempt's output is dropped, stdin replayed")
        self.assertEqual(r.stderr, "")

    def test_the_workflow_retry_reads_the_workflow_file(self):
        self.set_live(WF_LIVE)
        r = self.run_tool({"GH_TOKEN": WF_DEAD, "STUB_ROTATE": "gh-workflow-token"})
        self.assertEqual((r.returncode, self.ncalls()), (0, 2), r.stderr)

    def test_negative_control_the_same_mint_without_the_wrapper_stays_401(self):
        r = self.run_tool({"GH_TOKEN": LIVE, "STUB_ROTATE": "gh-token"}, wrapped=False)
        self.assertEqual((r.returncode, self.ncalls()), (1, 1))

    def test_a_401_with_no_newer_token_is_not_retried_and_passes_through(self):
        self.set_live("nobody-has-this")
        r = self.run_tool()
        self.assertEqual((r.returncode, self.ncalls()), (1, 1))
        self.assertIn("Bad credentials", r.stdout)
        self.assertIn(self.msg401, r.stderr)

    def test_other_failures_are_not_retried(self):
        r = self.run_tool({"STUB_EXIT": "4", "STUB_ROTATE": ""})
        self.assertEqual((r.returncode, self.ncalls()), (4, 1))

    def test_no_token_in_output_and_no_scratch_left(self):
        self.set_live("nobody-has-this")
        r = self.run_tool({"GH_TOKEN": DEAD})
        for t in (DEAD, LIVE, WF_DEAD, WF_LIVE):
            self.assertNotIn(t, r.stdout + r.stderr)
        self.run_tool({"STUB_ROTATE": "gh-token"})
        self.assertEqual(os.listdir(self.scratch), [])

    def test_binary_stdin_and_stdout_survive(self):
        cat = os.path.join(self.real, self.tool)
        with open(cat, "w") as f:
            f.write("#!/bin/bash\n/bin/cat\n")
        data = bytes(range(256)) * 4
        env = {"HOME": self.tmp, "TMPDIR": self.scratch, "PATH": f"{BIN}:{self.real}:/usr/bin:/bin",
               "BUGFIX_SPOOL": self.spool, "BUGFIX_GH_TOKEN": DEAD}
        r = subprocess.run([os.path.join(BIN, self.tool)], env=env, input=data, capture_output=True, timeout=20)
        self.assertEqual(r.stdout, data)


class FreshTokenGit(FreshTokenGh):
    """git's own 401, as it reports a refused credential over HTTPS."""
    tool, msg401 = "git", "fatal: Authentication failed for 'https://github.com/Uplift-Foundation/Orbweaver.git/'"

    def test_git_without_any_org_variable_is_left_alone(self):
        r = self.run_tool({"BUGFIX_GH_TOKEN": "", "BUGFIX_SPOOL": "", "GH_TOKEN": "x"})
        self.assertEqual(self.ncalls(), 1)
        self.assertEqual(r.returncode, 1)


if __name__ == "__main__":
    unittest.main()
