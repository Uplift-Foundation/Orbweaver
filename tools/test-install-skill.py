#!/usr/bin/env python3
"""Tests for tools/install-skill.sh, which copies the orbweaver-team skill into a
Claude Code account's config directory.

    python3 tools/test-install-skill.py

Plain stdlib unittest, like the other tools/test-*.py. Each case runs the real
script against a scratch account directory. This is plain bash, so it runs on
every platform the skill ships on (Git Bash on Windows, macOS, Linux) -- unlike
install-macos-hooks.sh, which needs osascript and can only run on a Mac, and
which is why the copy lives in its own script.
"""
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(_HERE, "install-skill.sh")
SKILL = os.path.join(_HERE, "..", "skills", "orbweaver-team")


def find_bash():
    # Git Bash, not whatever "bash" is on PATH: on the Windows runner that is
    # WSL's System32\bash.exe, which cannot open a D:\ path. Same lookup as
    # tools/test-require-team-sdlc.py.
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe",
                          r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
        return None
    return shutil.which("bash")


BASH = find_bash()


def sh_path(path):
    return path.replace("\\", "/")


@unittest.skipIf(BASH is None, "bash is not available")
class InstallSkillTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="cb-skill-")
        self.addCleanup(shutil.rmtree, self.root, True)
        self.account = os.path.join(self.root, ".claude")
        self.target = os.path.join(self.account, "skills", "orbweaver-team")

    def run_script(self, *args, cwd=None):
        run = subprocess.run([BASH or "bash", sh_path(SCRIPT), *args], capture_output=True, text=True, cwd=cwd)
        return run.returncode, run.stdout, run.stderr

    def install(self, *extra):
        return self.run_script("--settings", sh_path(os.path.join(self.account, "settings.json")), *extra)

    # --- install ----------------------------------------------------------------

    def test_it_copies_the_whole_skill_beside_the_settings_file(self):
        code, out, err = self.install()

        self.assertEqual(code, 0, err)
        self.assertIn("Skill installed:", out)
        for name in ("SKILL.md", "FORMATS.md", "orbweaver-team.sh"):
            self.assertTrue(os.path.isfile(os.path.join(self.target, name)), name)
        roles = sorted(os.listdir(os.path.join(self.target, "roles")))
        self.assertEqual(roles, ["architect.md", "engineer.md", "pm.md", "qa.md"])

    def test_a_config_dir_can_be_named_instead_of_a_settings_file(self):
        code, _, err = self.run_script("--config-dir", sh_path(self.account))

        self.assertEqual(code, 0, err)
        self.assertTrue(os.path.isfile(os.path.join(self.target, "SKILL.md")))

    @unittest.skipIf(os.name == "nt", "Windows has no executable bit to check")
    def test_the_resolver_comes_out_executable(self):
        self.install()

        mode = os.stat(os.path.join(self.target, "orbweaver-team.sh")).st_mode
        self.assertTrue(mode & stat.S_IXUSR)

    def test_the_installed_resolver_runs_from_where_it_was_put(self):
        # The point of copying it: it finds its built-in roles beside itself.
        self.install()
        env = dict(os.environ, HOME=self.root, CLAUDE_CONFIG_DIR=self.account,
                   ORBWEAVER_PERSONAS=os.path.join(self.root, "pool"))
        run = subprocess.run(
            [BASH or "bash", sh_path(os.path.join(self.target, "orbweaver-team.sh")), "list"],
            capture_output=True, text=True, env=env)

        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertIn("pm\tbuiltin", run.stdout)
        self.assertIn("qa\tbuiltin", run.stdout)

    def test_a_reinstall_replaces_the_folder_instead_of_adding_to_it(self):
        self.install()
        stale = os.path.join(self.target, "left-by-an-older-version.txt")
        with open(stale, "w") as handle:
            handle.write("stale")

        code, _, err = self.install()

        self.assertEqual(code, 0, err)
        self.assertFalse(os.path.exists(stale))
        self.assertTrue(os.path.isfile(os.path.join(self.target, "SKILL.md")))

    def test_a_neighbouring_skill_is_never_touched(self):
        neighbour = os.path.join(self.account, "skills", "someone-elses", "SKILL.md")
        os.makedirs(os.path.dirname(neighbour))
        with open(neighbour, "w") as handle:
            handle.write("keep me")

        self.install()
        self.install("--uninstall")

        with open(neighbour) as handle:
            self.assertEqual(handle.read(), "keep me")

    # --- uninstall --------------------------------------------------------------

    def test_uninstall_removes_that_one_folder(self):
        self.install()
        self.assertTrue(os.path.isdir(self.target))

        code, out, err = self.install("--uninstall")

        self.assertEqual(code, 0, err)
        self.assertIn("Removed skill:", out)
        self.assertFalse(os.path.exists(self.target))
        self.assertTrue(os.path.isdir(os.path.join(self.account, "skills")))

    def test_uninstall_with_nothing_there_is_not_an_error(self):
        code, out, err = self.install("--uninstall")

        self.assertEqual(code, 0, err)
        self.assertEqual(out, "")

    # --- refusals, each beside a case that passes -------------------------------

    def test_a_missing_source_is_an_error_that_creates_nothing(self):
        empty = os.path.join(self.root, "empty")
        os.makedirs(empty)

        code, _, err = self.install("--source", sh_path(empty))

        self.assertEqual(code, 1)
        self.assertIn("Can't find skills/orbweaver-team", err)
        self.assertFalse(os.path.exists(self.target))

    def checkout(self):
        """A folder shaped like a checkout, whose skills/orbweaver-team is the source."""
        root = os.path.join(self.root, "checkout")
        shutil.copytree(os.path.abspath(SKILL), os.path.join(root, "skills", "orbweaver-team"))
        return root, os.path.join(root, "skills", "orbweaver-team")

    def test_a_relative_settings_path_never_removes_the_shipped_source(self):
        # `--settings settings.json` has a config dir of `.`, so from a checkout's
        # root the target is the source itself.
        root, source = self.checkout()

        code, _, err = self.run_script("--settings", "settings.json", "--uninstall",
                                       "--source", sh_path(source), cwd=root)

        self.assertEqual(code, 1)
        self.assertIn("own source", err)
        self.assertTrue(os.path.isfile(os.path.join(source, "SKILL.md")))

    @unittest.skipIf(os.name == "nt", "a symlink needs privileges the Windows runner may not grant")
    def test_the_guard_sees_through_a_symlinked_spelling_of_the_source(self):
        # macOS puts its temp directory behind a symlink (/var is /private/var), so
        # the same folder arrives spelled two ways: as bash reports its working
        # directory, and as it was typed. Compared as written they differ, and the
        # uninstall deleted the source (found by the Mac CI leg).
        root, source = self.checkout()
        alias = os.path.join(self.root, "alias")
        os.symlink(root, alias)

        code, _, err = self.run_script("--settings", "settings.json", "--uninstall", "--source",
                                       sh_path(os.path.join(alias, "skills", "orbweaver-team")), cwd=root)

        self.assertEqual(code, 1, err)
        self.assertTrue(os.path.isfile(os.path.join(source, "SKILL.md")))

        code, out, err = self.run_script("--config-dir", sh_path(alias), "--source", sh_path(source))
        self.assertEqual(code, 1, err)
        self.assertNotIn("Skill installed", out)
        self.assertTrue(os.path.isfile(os.path.join(source, "SKILL.md")))

    def test_installing_into_the_checkout_itself_is_refused_and_leaves_the_source(self):
        root, source = self.checkout()

        code, out, err = self.run_script("--config-dir", sh_path(root), "--source", sh_path(source))

        self.assertEqual(code, 1)
        self.assertIn("overlaps", err)
        self.assertNotIn("Skill installed", out)
        self.assertTrue(os.path.isfile(os.path.join(source, "SKILL.md")))
        self.assertTrue(os.path.isfile(os.path.join(source, "orbweaver-team.sh")))

    def test_a_relative_settings_path_installs_where_it_says_when_that_is_not_the_source(self):
        root, source = self.checkout()
        elsewhere = os.path.join(self.root, "elsewhere")
        os.makedirs(elsewhere)

        code, _, err = self.run_script("--settings", "settings.json", "--source", sh_path(source), cwd=elsewhere)

        self.assertEqual(code, 0, err)
        self.assertTrue(os.path.isfile(os.path.join(elsewhere, "skills", "orbweaver-team", "SKILL.md")))

    def test_an_option_given_no_value_is_a_usage_error(self):
        code, _, err = self.run_script("--settings")

        self.assertEqual(code, 2)
        self.assertIn("usage:", err)

    def test_a_source_can_be_named_outright(self):
        code, _, err = self.install("--source", sh_path(os.path.abspath(SKILL)))

        self.assertEqual(code, 0, err)
        self.assertTrue(os.path.isfile(os.path.join(self.target, "SKILL.md")))

    def test_it_needs_to_be_told_where(self):
        code, _, err = self.run_script()

        self.assertEqual(code, 2)
        self.assertIn("usage:", err)

    def test_an_unknown_option_is_refused(self):
        code, _, err = self.run_script("--frobnicate")

        self.assertEqual(code, 2)
        self.assertIn("unknown option", err)

    def test_help_prints_what_it_does(self):
        code, out, _ = self.run_script("--help")

        self.assertEqual(code, 0)
        self.assertIn("orbweaver-team skill", out)


if __name__ == "__main__":
    unittest.main()
