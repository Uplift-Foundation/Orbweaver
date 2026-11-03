#!/usr/bin/env python3
"""Runs publish-screenshots.yml's real publish step against a local bare repository.

CB-237: every run pushes to one `screenshots` branch at the same flat
<rid>/<name>.png paths, and the PR comment used to link to the branch tip, so a
comment showed whichever run pushed last, from any PR. The step is extracted
from the workflow text and run with a fake `gh`, so what is tested is the shell
that ships, not a copy of it.

Linux and macOS only: the workflow itself runs on ubuntu-latest.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
WORKFLOW = os.path.join(HERE, "..", ".github", "workflows", "publish-screenshots.yml")
REPO = "o/r"
GIT_ENV = {"GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t",
           "GIT_COMMITTER_EMAIL": "t@t"}


def publish_step(text):
    """The `run:` block of the step named 'Publish to the screenshots branch...'."""
    lines = text.splitlines()
    start = next(i for i, l in enumerate(lines) if "name: Publish to the screenshots branch" in l)
    run = next(i for i in range(start, len(lines)) if lines[i].strip() == "run: |")
    body = []
    for l in lines[run + 1:]:
        if l.strip() and not l.startswith(" " * 10):
            break
        body.append(l[10:] if l.startswith(" " * 10) else l)
    return "\n".join(body)


def substitute(script, run_id, pr, head_branch="", head_owner="", event="workflow_dispatch"):
    """Replace ${{ ... }} expressions the way Actions does: as TEXT, before bash parses
    the script. The current workflow keeps none in the script (they arrive as env), so
    this is a no-op there; an older copy that still splices them in gets the values
    spliced in, which is what lets a test show that it was injectable."""
    def one(m):
        expr = m.group(0)
        if "github.repository" in expr:
            return REPO
        if "run_id" in expr:
            return str(run_id)
        if "inputs.pr_number" in expr:
            return str(pr)
        if "head_repository.owner.login" in expr:
            return head_owner
        if "head_branch" in expr:
            return head_branch
        if "workflow_run.event" in expr:
            return event
        return ""
    return re.sub(r"\$\{\{.*?\}\}", one, script)


def script_bodies(text):
    """Every `run:` script in a workflow file, as text."""
    lines = text.splitlines()
    out = []
    for i, l in enumerate(lines):
        m = re.match(r"^(\s*)(?:- )?run:\s*(.*)$", l)
        if not m:
            continue
        indent, rest = len(m.group(1)), m.group(2)
        if rest and rest[0] not in "|>":
            out.append(rest)
            continue
        body = []
        for nxt in lines[i + 1:]:
            if nxt.strip() and len(nxt) - len(nxt.lstrip()) <= indent:
                break
            body.append(nxt)
        out.append("\n".join(body))
    return out


def point_at_local_remote(script):
    """Older copies of the workflow have no override; patch the one line so a test can
    never reach github.com. Refuse to run if it still could."""
    if "SCREENSHOTS_REMOTE_URL" not in script:
        script = script.replace(
            'remote_url="https://x-access-token:${GH_TOKEN}@github.com/${repo}.git"',
            'remote_url="$SCREENSHOTS_REMOTE_URL"')
    assert "@github.com/${repo}.git" not in script.replace(
        "${SCREENSHOTS_REMOTE_URL:-https://x-access-token:${GH_TOKEN}@github.com/${repo}.git}", ""), \
        "the step could still reach github.com"
    return script


@unittest.skipIf(os.name == "nt", "the workflow runs on ubuntu-latest")
class PublishStep(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="cb-publish-shots-")
        self.remote = os.path.join(self.tmp, "remote.git")
        subprocess.run(["git", "init", "-q", "--bare", "-b", "screenshots", self.remote], check=True)
        self.work = os.path.join(self.tmp, "ws", "work")
        os.makedirs(self.work)
        self.bin = os.path.join(self.tmp, "bin")
        os.makedirs(self.bin)
        self.comments = os.path.join(self.tmp, "comments")
        os.makedirs(self.comments)
        # `pr list` answers 7 and records each argument on its own line, so a test can see
        # exactly what the step handed gh; `pr comment` saves the body it was given.
        with open(os.path.join(self.bin, "gh"), "w") as f:
            f.write('#!/bin/sh\n'
                    'if [ "$1 $2" = "pr list" ]; then printf \'%%s\\n\' "$@" > "%s/list-args"; echo 7; exit 0; fi\n'
                    '[ "$1 $2" = "pr comment" ] && cat > "%s/pr-$3.md"\nexit 0\n' % (self.comments, self.comments))
        os.chmod(os.path.join(self.bin, "gh"), 0o755)
        with open(self.workflow_path()) as f:
            self.script = point_at_local_remote(publish_step(f.read()))

    def workflow_path(self):
        return os.environ.get("PUBLISH_WORKFLOW_UNDER_TEST", WORKFLOW)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def execute(self, pr, png, run_id, head_branch="", head_owner="", event="workflow_dispatch"):
        """Run the step once the way Actions would: expressions still in the script text are
        spliced in, and the values the workflow now passes as env are set too."""
        d = os.path.join(self.work, "downloaded", "ui-screenshots-osx-arm64")
        shutil.rmtree(os.path.join(self.work, "downloaded"), ignore_errors=True)
        os.makedirs(d)
        with open(os.path.join(d, "a.png"), "wb") as f:
            f.write(png)
        env = dict(os.environ, **GIT_ENV, PATH=self.bin + os.pathsep + os.environ["PATH"],
                   GH_TOKEN="x", GITHUB_STEP_SUMMARY=os.path.join(self.tmp, "summary"),
                   SCREENSHOTS_REMOTE_URL="file://" + self.remote,
                   REPO=REPO, RUN_ID=str(run_id), PR_INPUT=str(pr) if pr else "", PR_FROM_EVENT="",
                   HEAD_OWNER=head_owner, HEAD_BRANCH=head_branch, TRIGGER_EVENT=event)
        script = substitute(self.script, run_id, pr, head_branch, head_owner, event)
        return subprocess.run(["bash", "-c", script], cwd=self.work, env=env,
                              capture_output=True, text=True)

    def run_step(self, pr, png, run_id):
        r = self.execute(pr, png, run_id)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        with open(os.path.join(self.comments, "pr-%s.md" % pr)) as f:
            return r.stdout, f.read()

    def git(self, *args):
        return subprocess.run(["git", "-C", self.remote, *args], capture_output=True)

    def shown_by(self, comment):
        """What the link in a comment shows: the blob at <ref>:<path>, or None if the
        ref does not exist on the remote."""
        m = re.search(r"raw/(.*)/(osx-arm64/a\.png)\)", comment)
        self.assertIsNotNone(m, comment)
        ref, path = m.groups()
        r = self.git("show", "%s:%s" % (ref, path))
        return (r.stdout if r.returncode == 0 else None), ref

    def test_each_comment_still_shows_its_own_images_after_another_run_overwrote_them(self):
        _, a = self.run_step(135, b"IMAGE-FROM-A", 1)
        _, b = self.run_step(139, b"IMAGE-FROM-B", 2)
        self.assertEqual(b"IMAGE-FROM-A", self.shown_by(a)[0])
        self.assertEqual(b"IMAGE-FROM-B", self.shown_by(b)[0])

    def test_the_link_names_a_commit_not_a_branch(self):
        _, a = self.run_step(135, b"A", 1)
        self.assertRegex(self.shown_by(a)[1], r"^[0-9a-f]{40}$")

    def test_a_run_with_nothing_new_to_commit_still_links_to_a_commit_holding_its_images(self):
        self.run_step(135, b"SAME", 1)
        _, again = self.run_step(139, b"SAME", 2)
        shown, ref = self.shown_by(again)
        self.assertEqual(b"SAME", shown)
        self.assertRegex(ref, r"^[0-9a-f]{40}$")

    def test_a_retry_that_rebases_onto_another_runs_push_links_to_the_commit_that_landed(self):
        self.run_step(135, b"FIRST", 1)
        flag = os.path.join(self.tmp, "rejected-once")
        hook = os.path.join(self.remote, "hooks", "pre-receive")
        with open(hook, "w") as f:
            f.write("""#!/bin/sh
if [ ! -e "%s" ]; then
  touch "%s"
  unset GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_QUARANTINE_PATH
  tip=$(git rev-parse refs/heads/screenshots)
  blob=$(echo competing | git hash-object -w --stdin)
  tree=$( { git ls-tree "$tip^{tree}"; printf '100644 blob %%s\\tcompeting.txt\\n' "$blob"; } | git mktree )
  c=$(git commit-tree "$tree" -p "$tip" -m "another run pushed first")
  git update-ref refs/heads/screenshots "$c"
  exit 1
fi
""" % (flag, flag))
        os.chmod(hook, 0o755)
        out, comment = self.run_step(139, b"SECOND", 2)
        self.assertIn("Push conflict, retry 1", out)
        shown, ref = self.shown_by(comment)
        self.assertEqual(b"SECOND", shown)
        # The commit is on the remote's branch, not a pre-rebase one that never arrived.
        self.assertEqual(0, self.git("merge-base", "--is-ancestor", ref, "screenshots").returncode)

    def test_a_run_that_lands_right_after_our_push_does_not_take_our_link(self):
        """The race this ticket is about, at its tightest: another run's commit lands on
        `screenshots` after our push succeeded and before we name a commit. The link must
        name the commit WE pushed, not whatever the tip is by the time we look — which a
        read of the remote after the push (`git ls-remote ... | cut -f1`) would get wrong,
        and which every other case here passes either way, because nothing else moves the
        tip in that window. post-receive runs after the ref update and before the client's
        push returns, so it lands the competing commit exactly there."""
        flag = os.path.join(self.tmp, "landed-once")
        hook = os.path.join(self.remote, "hooks", "post-receive")
        with open(hook, "w") as f:
            f.write("""#!/bin/sh
if [ ! -e "%s" ]; then
  touch "%s"
  tip=$(git rev-parse refs/heads/screenshots)
  blob=$(printf 'IMAGE-FROM-THE-NEXT-RUN' | git hash-object -w --stdin)
  sub=$( { git ls-tree "$tip:osx-arm64" | grep -v '	a.png$'; printf '100644 blob %%s\\ta.png\\n' "$blob"; } | git mktree )
  tree=$( { git ls-tree "$tip^{tree}" | grep -v '	osx-arm64$'; printf '040000 tree %%s\\tosx-arm64\\n' "$sub"; } | git mktree )
  c=$(git commit-tree "$tree" -p "$tip" -m "the next run pushed right after us")
  git update-ref refs/heads/screenshots "$c"
fi
""" % (flag, flag))
        os.chmod(hook, 0o755)

        _, comment = self.run_step(139, b"IMAGE-FROM-OUR-RUN", 2)

        # The competing commit really landed: the tip now shows the other run's image.
        self.assertEqual(b"IMAGE-FROM-THE-NEXT-RUN",
                         self.git("show", "screenshots:osx-arm64/a.png").stdout)
        shown, ref = self.shown_by(comment)
        self.assertEqual(b"IMAGE-FROM-OUR-RUN", shown)
        self.assertNotEqual(self.git("rev-parse", "screenshots").stdout.decode().strip(), ref)

    # --- Script injection through the triggering run's branch name -------------------------
    # head_branch is chosen by whoever opens the PR, and git allows shell syntax in it. The
    # step holds a contents: write token, so running that syntax was a write to main. Each
    # payload below tries to create a file named for it; none may succeed.

    HOSTILE_BRANCHES = [
        '"; touch "{canary}"; "',
        "$(touch {canary})",
        "`touch {canary}`",
        "x\";touch${{IFS}}{canary};\"",
    ]

    def hostile(self, template):
        canary = os.path.join(self.tmp, "PWNED")
        return template.format(canary=canary), canary

    def test_a_branch_name_made_of_shell_syntax_is_never_run(self):
        for template in self.HOSTILE_BRANCHES:
            branch, canary = self.hostile(template)
            with self.subTest(branch=branch):
                r = self.execute(0, b"PNG", 1, head_branch=branch, head_owner="someone",
                                 event="pull_request")
                self.assertEqual(0, r.returncode, r.stdout + r.stderr)
                self.assertFalse(os.path.exists(canary), "the branch name was executed as shell")

    def test_a_branch_name_made_of_shell_syntax_still_reaches_gh_as_one_literal_argument(self):
        """The fix must not break the lookup it protects: the PR is still found and commented
        on, and gh is handed the branch name byte for byte, as a single argument."""
        branch, canary = self.hostile('"; touch "{canary}"; "')
        r = self.execute(0, b"PNG", 1, head_branch=branch, head_owner="someone", event="pull_request")
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        with open(os.path.join(self.comments, "list-args")) as f:
            args = f.read().split("\n")
        self.assertEqual(branch, args[args.index("--head") + 1])
        self.assertTrue(os.path.exists(os.path.join(self.comments, "pr-7.md")))
        self.assertFalse(os.path.exists(canary))

    def test_an_owner_that_is_not_a_github_login_is_not_used_to_build_the_jq_filter(self):
        r = self.execute(0, b"PNG", 1, head_branch="feature/x", event="pull_request",
                         head_owner='x")|.[]|{n:1}|select(("1"=="1')
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.comments, "list-args")),
                         "gh pr list ran with an owner that cannot be a login")

    def test_a_pr_number_that_is_not_digits_is_dropped(self):
        r = self.execute("--repo evil/repo", b"PNG", 1)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        self.assertEqual([], [n for n in os.listdir(self.comments) if n.startswith("pr-")])

    def test_the_script_has_no_expression_in_it_for_a_value_a_pr_author_controls(self):
        """The static half of the same rule, so a later edit that splices one back in fails
        here by name instead of waiting for someone to craft a branch."""
        with open(self.workflow_path()) as f:
            text = f.read()
        spliced = [b for b in script_bodies(text) if "${{" in b]
        self.assertEqual([], spliced, "an expression is spliced into a run: script; pass it via env:")

    def test_the_resolver_would_catch_a_branch_tip_link(self):
        """Control for the checks above: build the OLD link form by hand and show that
        it is the one that resolves to the wrong run."""
        _, a = self.run_step(135, b"IMAGE-FROM-A", 1)
        self.run_step(139, b"IMAGE-FROM-B", 2)
        old_form = a.replace(self.shown_by(a)[1], "refs/heads/screenshots")
        self.assertEqual(b"IMAGE-FROM-B", self.shown_by(old_form)[0])


if __name__ == "__main__":
    unittest.main()
