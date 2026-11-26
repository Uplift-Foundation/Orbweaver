#!/usr/bin/env python3
"""Tests for the bug-fix org's `gh` and `git` stand-ins (CB-478).

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
BIN = os.path.realpath(os.path.join(HERE, "..", "paperclip", "bugfix-org", "host", "bin"))
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


if __name__ == "__main__":
    unittest.main()
