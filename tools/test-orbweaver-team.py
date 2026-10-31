#!/usr/bin/env python3
"""Tests for skills/orbweaver-team/orbweaver-team.sh, the team resolver.

    python3 tools/test-orbweaver-team.py

Plain stdlib unittest, like the other tools/test-*.py. Every case runs the real
script as a subprocess against a pool built in a scratch directory, with the
environment it reads (HOME, CLAUDE_CONFIG_DIR, ORBWEAVER_PERSONAS and the list
of live instance names) set outright, so the answer never depends on whose
machine this runs on. Each refusal is paired with the passing case next to it,
so a resolver that refused everything (or nothing) fails here rather than
looking healthy.

On Windows it runs under Git Bash, which is what Claude Code uses there, and the
scratch paths are Windows paths on purpose: converting them is part of what the
script has to get right.
"""
import os
import shutil
import subprocess
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(_HERE, "..", "skills", "orbweaver-team", "orbweaver-team.sh")


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


def same_path(a, b):
    """Two spellings of one file: C:/x and C:\\x, a case-blind Windows, and a
    symlinked temp directory (macOS puts /var/folders under /private/var, and bash
    reports the real one as its working directory)."""
    return os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(b))


@unittest.skipIf(BASH is None, "bash is not available")
class ResolverTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="cb-team-")
        self.addCleanup(shutil.rmtree, self.root, True)
        self.home = self.dir("home")
        self.account = self.dir("account")
        self.pool = self.dir("pool")
        self.project = self.dir("work", "project")
        self.builtin = os.path.abspath(os.path.join(_HERE, "..", "skills", "orbweaver-team", "roles"))

    # --- fixtures ---------------------------------------------------------------

    def dir(self, *parts):
        path = os.path.join(self.root, *parts)
        os.makedirs(path, exist_ok=True)
        return path

    def write(self, path, text, newline="\n"):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", newline="") as handle:
            handle.write(text.replace("\n", newline))
        return path

    def persona(self, root, name, **extra):
        front = ["---", f'name: "{name.title()}"', f'slug: "{name}"'] + [f"{k}: {v}" for k, v in extra.items()] + ["---", ""]
        return self.write(os.path.join(root, "profiles", name, name + ".md"), "\n".join(front))

    def team(self, root, name, lines, extends=None):
        front = ["---", f"team: {name}"] + ([f"extends: {extends}"] if extends else []) + lines + ["---", ""]
        return self.write(os.path.join(root, "teams", name + ".md"), "\n".join(front))

    def role(self, root, name, **front):
        text = ["---", f"role: {name}"] + [f"{k}: {v}" for k, v in front.items()] + ["---", f"The {name} role.", ""]
        return self.write(os.path.join(root, "roles", name + ".md"), "\n".join(text))

    def alpha(self):
        for name in ("atlas", "vector", "sentry", "nova"):
            self.persona(self.pool, name)
        self.team(self.pool, "alpha", ["pm: atlas", "architect: vector", "engineer: vector, nova", "qa: sentry"])

    def run_script(self, *args, live="", extra_env=None, cwd=None):
        env = dict(os.environ)
        for key in ("WSL_DISTRO_NAME", "ORBWEAVER_BUILTIN_ROLES"):
            env.pop(key, None)
        env.update({
            "HOME": self.home,
            "CLAUDE_CONFIG_DIR": self.account,
            "ORBWEAVER_PERSONAS": self.pool,
            "ORBWEAVER_LIVE_NAMES": live,
        })
        env.update(extra_env or {})
        run = subprocess.run(
            [BASH or "bash", SCRIPT.replace("\\", "/"), *args],
            capture_output=True, text=True, env=env, cwd=cwd or self.project)
        return run.returncode, run.stdout, run.stderr

    def resolve(self, *args, **kwargs):
        code, out, err = self.run_script("resolve", *args, **kwargs)
        return code, out, err

    @staticmethod
    def rows(out):
        lines = [line for line in out.splitlines() if line and not line.startswith("#")]
        header = lines[0].split("\t")
        return [dict(zip(header, line.split("\t"))) for line in lines[1:]]

    def ok(self, *args, **kwargs):
        code, out, err = self.resolve(*args, **kwargs)
        self.assertEqual(code, 0, err)
        self.assertEqual(err, "")
        return self.rows(out)

    def refused(self, *args, **kwargs):
        code, out, err = self.resolve(*args, **kwargs)
        self.assertEqual(code, 2, f"expected a refusal, got exit {code}: {out}")
        # All or nothing: a lead that sees any manifest could start half a team.
        self.assertEqual(out, "", "a refusal must put nothing on stdout")
        return err

    # --- a saved team -----------------------------------------------------------

    def test_a_saved_team_resolves_each_slot_to_a_persona_and_a_role(self):
        self.alpha()

        rows = self.ok("alpha")

        self.assertEqual([r["slot"] for r in rows], ["pm", "architect", "engineer", "engineer", "qa"])
        self.assertEqual([r["persona"] for r in rows], ["atlas", "vector", "vector", "nova", "sentry"])
        pm = rows[0]
        self.assertTrue(same_path(pm["persona_file"], os.path.join(self.pool, "profiles", "atlas", "atlas.md")))
        self.assertTrue(same_path(pm["role_file"], os.path.join(self.builtin, "pm.md")))
        self.assertTrue(os.path.exists(pm["persona_file"]) and os.path.exists(pm["role_file"]))

    def test_a_persona_in_two_slots_is_two_instances_not_one_name_twice(self):
        # Orbweaver cannot tell two live sessions with one name apart; Vector as
        # both architect and engineer must therefore be two names.
        self.alpha()

        rows = self.ok("alpha")

        names = [r["instance"] for r in rows]
        self.assertEqual(names, ["atlas", "vector", "vector_2", "nova", "sentry"])
        self.assertEqual(len(set(names)), len(names))

    def test_a_persona_already_live_gets_the_next_free_instance_name(self):
        self.alpha()

        rows = self.ok("alpha", live="atlas vector vector_2")

        by_slot = {(r["slot"], r["persona"]): r["instance"] for r in rows}
        self.assertEqual(by_slot[("pm", "atlas")], "atlas_2")
        self.assertEqual(by_slot[("architect", "vector")], "vector_3")
        self.assertEqual(by_slot[("engineer", "vector")], "vector_4")
        self.assertEqual(by_slot[("qa", "sentry")], "sentry")

    def test_two_teams_that_share_nobody_get_plain_names_and_a_shared_persona_does_not(self):
        self.alpha()
        self.persona(self.pool, "echo")
        self.team(self.pool, "bravo", ["pm: echo", "qa: sentry"])

        first = self.ok("bravo")
        self.assertEqual({r["instance"] for r in first}, {"echo", "sentry"})

        # Alpha is running, so Bravo's Sentry is the second.
        second = self.ok("bravo", live="atlas vector nova sentry")
        self.assertEqual({r["slot"]: r["instance"] for r in second}, {"pm": "echo", "qa": "sentry_2"})

    # --- nearest wins, entirely -------------------------------------------------

    def test_a_nearer_team_file_replaces_a_farther_one_without_merging(self):
        self.alpha()
        self.persona(self.project, "nova")
        project_orbweaver = os.path.join(self.project, ".orbweaver")
        self.team(project_orbweaver, "alpha", ["pm: nova"])
        self.persona(project_orbweaver, "nova")

        rows = self.ok("alpha")

        self.assertEqual([(r["slot"], r["persona"]) for r in rows], [("pm", "nova")])

    def test_a_role_in_the_pool_replaces_the_built_in_and_a_disabled_one_refuses(self):
        self.alpha()
        custom = self.role(self.pool, "qa", model="haiku")

        rows = self.ok("alpha")
        qa = [r for r in rows if r["slot"] == "qa"][0]
        self.assertTrue(same_path(qa["role_file"], custom))
        self.assertEqual(qa["model"], "haiku")

        self.role(self.pool, "qa", disabled="true")
        self.assertIn("role 'qa' is disabled", self.refused("alpha"))

    def test_an_added_role_is_a_slot_like_any_other(self):
        self.persona(self.pool, "nova")
        writer = self.role(self.pool, "writer")

        rows = self.ok("writer=nova")

        self.assertEqual(len(rows), 1)
        self.assertTrue(same_path(rows[0]["role_file"], writer))

    def test_the_account_beats_the_pool_and_the_project_beats_both_for_a_persona(self):
        self.persona(self.pool, "atlas")
        account_file = self.persona(self.account, "atlas")
        self.assertTrue(same_path(self.ok("pm=atlas")[0]["persona_file"], account_file))

        project_file = self.persona(self.project, "atlas")        # profile-gen's own layout, in the project
        self.assertTrue(same_path(self.ok("pm=atlas")[0]["persona_file"], project_file))

    def test_profile_gens_fixed_persona_file_is_a_persona_only_by_its_slug(self):
        fixed = self.write(os.path.join(self.account, "persona", "persona.md"),
                           '---\nname: "Echo"\nslug: "echo"\n---\n')

        self.assertTrue(same_path(self.ok("pm=echo")[0]["persona_file"], fixed))
        self.assertTrue(same_path(self.ok("pm=echo_2")[0]["persona_file"], fixed))
        self.assertIn("unknown persona 'nova'", self.refused("pm=nova"))

    def test_an_instance_name_written_in_a_team_finds_its_persona(self):
        self.persona(self.pool, "vector")

        rows = self.ok("engineer=vector_2")

        self.assertTrue(same_path(rows[0]["persona_file"], os.path.join(self.pool, "profiles", "vector", "vector.md")))

    # --- extends ----------------------------------------------------------------

    def test_a_team_that_extends_another_swaps_whole_slots(self):
        self.alpha()
        self.persona(self.pool, "echo")
        self.team(self.pool, "alpha-lead", ["pm: nova", "pm.mode: lead"])   # for contrast: a slot with a mode
        self.team(self.pool, "bravo", ["qa: echo"], extends="alpha")

        rows = self.ok("bravo")

        self.assertEqual([(r["slot"], r["persona"]) for r in rows],
                         [("pm", "atlas"), ("architect", "vector"), ("engineer", "vector"),
                          ("engineer", "nova"), ("qa", "echo")])

    def test_a_slot_a_team_names_drops_the_base_slots_mode_and_model(self):
        self.persona(self.pool, "atlas")
        self.persona(self.pool, "nova")
        self.persona(self.pool, "vector")
        self.team(self.pool, "base", ["pm: atlas, nova", "pm.mode: lead", "pm.lead: atlas", "pm.model: opus"])
        self.team(self.pool, "child", ["pm: vector"], extends="base")

        rows = self.ok("child")

        self.assertEqual([(r["persona"], r["mode"], r["lead"], r["model"]) for r in rows], [("vector", "", "", "")])

    def test_a_cycle_in_extends_is_refused_not_followed_forever(self):
        self.team(self.pool, "a", ["pm: atlas"], extends="b")
        self.team(self.pool, "b", ["pm: atlas"], extends="a")

        self.assertIn("extends too deeply", self.refused("a"))

    # --- unions -----------------------------------------------------------------

    def two_teams(self):
        self.alpha()
        self.persona(self.pool, "echo")
        self.persona(self.pool, "kestrel")
        self.team(self.pool, "bravo", ["pm: echo", "qa: kestrel", "writer: nova"])
        self.role(self.pool, "writer")

    def test_a_union_of_teams_with_different_slots_just_combines_them(self):
        self.two_teams()
        self.team(self.pool, "solo", ["writer: nova"])

        rows = self.ok("alpha+solo")

        self.assertIn(("writer", "nova"), [(r["slot"], r["persona"]) for r in rows])
        self.assertEqual(len([r for r in rows if r["slot"] == "pm"]), 1)

    def test_a_slot_both_teams_fill_with_different_people_must_say_how_they_work_together(self):
        self.two_teams()

        problem = self.refused("alpha+bravo")

        self.assertIn("slot 'pm' is filled by atlas and echo in a union", problem)
        self.assertIn("pm.mode=lead", problem)
        self.assertIn("slot 'qa' is filled by sentry and kestrel in a union", problem)

    def test_a_union_with_the_modes_stated_resolves_them(self):
        self.two_teams()

        rows = self.ok("alpha+bravo", "pm.mode=lead", "pm.lead=echo", "qa.mode=collaborate")

        pm = [r for r in rows if r["slot"] == "pm"]
        self.assertEqual([(r["persona"], r["mode"], r["lead"]) for r in pm], [("atlas", "lead", "echo"), ("echo", "lead", "echo")])
        qa = [r for r in rows if r["slot"] == "qa"]
        self.assertEqual({(r["persona"], r["mode"]) for r in qa}, {("sentry", "collaborate"), ("kestrel", "collaborate")})

    def test_a_union_where_both_teams_agree_on_a_slot_is_not_a_conflict(self):
        self.alpha()
        self.team(self.pool, "alpha2", ["pm: atlas", "qa: sentry"])

        rows = self.ok("alpha+alpha2")

        self.assertEqual(len([r for r in rows if r["slot"] == "pm"]), 1)

    def test_a_slot_replaced_outright_forgets_what_the_union_said_about_it(self):
        self.two_teams()

        rows = self.ok("alpha+bravo", "pm=nova", "qa=sentry")

        self.assertEqual([(r["slot"], r["persona"]) for r in rows if r["slot"] in ("pm", "qa")],
                         [("pm", "nova"), ("qa", "sentry")])

    # --- overrides and explicit specs -------------------------------------------

    def test_a_saved_team_with_one_slot_swapped(self):
        self.alpha()
        self.persona(self.pool, "echo")

        rows = self.ok("alpha", "qa=echo")

        self.assertEqual([r["persona"] for r in rows if r["slot"] == "qa"], ["echo"])
        self.assertEqual([r["persona"] for r in rows if r["slot"] == "pm"], ["atlas"])

    def test_a_fully_explicit_spec_needs_no_team_at_all(self):
        self.alpha()

        rows = self.ok("pm=atlas", "qa=sentry,nova")

        self.assertEqual([(r["slot"], r["persona"], r["mode"]) for r in rows],
                         [("pm", "atlas", ""), ("qa", "sentry", "collaborate"), ("qa", "nova", "collaborate")])

    def test_a_list_with_a_lead_named_is_a_lead_slot(self):
        self.alpha()

        rows = self.ok("qa=sentry,nova", "qa.lead=nova")

        self.assertEqual({(r["mode"], r["lead"]) for r in rows}, {("lead", "nova")})

    def test_the_whole_spec_may_arrive_as_one_argument(self):
        self.alpha()

        rows = self.ok("alpha qa=nova")

        self.assertEqual([r["persona"] for r in rows if r["slot"] == "qa"], ["nova"])

    def test_a_slot_model_overrides_the_roles_model(self):
        self.persona(self.pool, "atlas")
        self.role(self.pool, "pm", model="opus")

        self.assertEqual(self.ok("pm=atlas")[0]["model"], "opus")
        self.assertEqual(self.ok("pm=atlas", "pm.model=haiku")[0]["model"], "haiku")

    # --- refusals, each beside a case that passes --------------------------------

    def test_every_problem_is_reported_not_just_the_first(self):
        self.alpha()

        problem = self.refused("pm=nobody", "qa=sentry", "ghost=atlas", "architect=ghost2")

        self.assertIn("unknown persona 'nobody'", problem)
        self.assertIn("unknown role 'ghost'", problem)
        self.assertIn("unknown persona 'ghost2'", problem)
        self.assertEqual(problem.count("error:"), 3)

    def test_an_unknown_team_names_where_it_looked(self):
        self.alpha()

        problem = self.refused("charlie")

        self.assertIn("unknown team 'charlie'", problem)
        self.assertIn("pool=", problem)

    def test_a_lead_slot_without_a_lead_or_with_a_stranger_for_one_is_refused(self):
        self.alpha()

        self.assertIn("mode lead but no qa.lead", self.refused("qa=sentry,nova", "qa.mode=lead"))
        self.assertIn("lead 'atlas' is not one of", self.refused("qa=sentry,nova", "qa.mode=lead", "qa.lead=atlas"))
        self.ok("qa=sentry,nova", "qa.mode=lead", "qa.lead=nova")

    def test_a_mode_that_is_neither_lead_nor_collaborate_is_refused(self):
        self.alpha()

        self.assertIn("mode must be lead or collaborate", self.refused("qa=sentry,nova", "qa.mode=boss"))

    def test_names_that_could_leave_the_pool_are_refused(self):
        self.alpha()

        for spec in (["../../etc/passwd"], ["pm=../atlas"], ["../x=atlas"], ["pm=at/las"]):
            with self.subTest(spec=spec):
                self.assertIn("error:", self.refused(*spec))

    def test_an_unknown_override_and_an_empty_spec_are_refused(self):
        self.alpha()

        self.assertIn("unknown override 'pm.color'", self.refused("alpha", "pm.color=red"))

        code, _, err = self.run_script("resolve")
        self.assertEqual(code, 2)
        self.assertIn("usage:", err)

    def test_a_team_file_with_a_slot_that_is_not_a_name_is_refused(self):
        self.alpha()
        self.write(os.path.join(self.pool, "teams", "odd.md"), "---\nteam: odd\npm/x: atlas\npm.color: red\n---\n")

        problem = self.refused("odd")

        self.assertIn("'pm/x' is not a slot name", problem)
        self.assertIn("unknown key 'pm.color'", problem)

    # --- the shape of the files -------------------------------------------------

    def test_windows_line_endings_and_quotes_and_comments_are_fine(self):
        self.persona(self.pool, "atlas")
        self.write(os.path.join(self.pool, "teams", "crlf.md"),
                   '---\n# a comment\nteam: "crlf"\n\npm: "atlas"\n---\nnotes\n', newline="\r\n")

        rows = self.ok("crlf")

        self.assertEqual([(r["slot"], r["persona"]) for r in rows], [("pm", "atlas")])

    def test_a_spec_with_a_star_in_it_is_a_word_not_a_glob(self):
        self.alpha()

        self.assertIn("error:", self.refused("*"))

    # --- list and roots ---------------------------------------------------------

    def test_list_shows_each_name_once_from_its_nearest_place(self):
        self.alpha()
        self.persona(self.account, "atlas")
        self.role(self.pool, "qa")

        code, out, err = self.run_script("list")

        self.assertEqual(code, 0, err)
        lines = [line.split("\t") for line in out.splitlines() if line and not line.startswith("#")]
        by_name = {(sections[0]): sections for sections in lines}
        self.assertEqual(by_name["atlas"][1], "account")      # nearer than the pool's atlas
        self.assertEqual(by_name["alpha"][1], "pool")
        self.assertEqual(by_name["qa"][1], "pool")             # replaces the built-in
        self.assertEqual(by_name["pm"][1], "builtin")
        self.assertEqual(len([line for line in lines if line[0] == "atlas"]), 1)

    def test_roots_are_listed_nearest_first(self):
        os.makedirs(os.path.join(self.project, ".orbweaver"))

        code, out, err = self.run_script("roots")

        self.assertEqual(code, 0, err)
        kinds = [line.split("\t")[0] for line in out.splitlines()]
        self.assertLess(kinds.index("project"), kinds.index("account"))
        self.assertLess(kinds.index("account"), kinds.index("pool"))
        self.assertLess(kinds.index("pool"), kinds.index("builtin"))

    def test_with_no_pool_named_the_default_is_a_dot_orbweaver_in_the_home_directory(self):
        self.persona(os.path.join(self.home, ".orbweaver"), "atlas")

        rows = self.ok("pm=atlas", extra_env={"ORBWEAVER_PERSONAS": ""})

        self.assertTrue(same_path(rows[0]["persona_file"],
                                  os.path.join(self.home, ".orbweaver", "profiles", "atlas", "atlas.md")))

    # --- the review's findings, each beside the case that works ----------------------

    def test_a_trailing_comment_is_not_part_of_the_value(self):
        # FORMATS.md annotates its own examples this way. Kept, the comment ended up
        # in the model column, and `disabled: true  # optional` was not true.
        self.alpha()
        self.write(os.path.join(self.pool, "roles", "qa.md"),
                   "---\nrole: qa\nmodel: sonnet        # optional: the model\ndisabled: false      # optional\n---\nbody\n")

        rows = self.ok("alpha")
        self.assertEqual([r["model"] for r in rows if r["slot"] == "qa"], ["sonnet"])

        self.write(os.path.join(self.pool, "roles", "qa.md"),
                   "---\nrole: qa\ndisabled: true      # optional\n---\nbody\n")
        self.assertIn("role 'qa' is disabled", self.refused("alpha"))

    def test_a_comment_after_a_team_slot_is_ignored_and_a_hash_inside_quotes_is_kept(self):
        self.persona(self.pool, "atlas")
        self.write(os.path.join(self.pool, "teams", "annotated.md"),
                   "---\nteam: annotated\npm: atlas # the boss\npm.model: \"fast # ish\"\n---\n")

        rows = self.ok("annotated")

        self.assertEqual([(r["persona"], r["model"]) for r in rows], [("atlas", "fast # ish")])

    def test_a_directorys_own_profiles_beat_its_orbweaver_folder_as_the_orb_does(self):
        # The app looks in a directory's profiles/ before its .orbweaver (the
        # orb and the prompt must describe the same persona).
        profiles = self.persona(self.project, "atlas")
        self.persona(os.path.join(self.project, ".orbweaver"), "atlas")

        self.assertTrue(same_path(self.ok("pm=atlas")[0]["persona_file"], profiles))

    def test_a_file_that_names_nobody_is_skipped_for_the_next_place(self):
        # The app skips a nearer file that gives it nothing and goes on.
        self.write(os.path.join(self.account, "profiles", "echo", "echo.md"), "just some notes, no front matter\n")
        farther = self.persona(self.pool, "echo")

        self.assertTrue(same_path(self.ok("pm=echo")[0]["persona_file"], farther))

    def test_a_slug_is_matched_without_regard_to_case_as_the_app_does(self):
        fixed = self.write(os.path.join(self.account, "persona", "persona.md"),
                           '---\nname: "Sluggy"\nslug: "Sluggy"\n---\n')

        self.assertTrue(same_path(self.ok("pm=sluggy")[0]["persona_file"], fixed))
        self.assertTrue(same_path(self.ok("pm=SLUGGY")[0]["persona_file"], fixed))

    def settings_dir(self, **values):
        directory = self.dir("app-settings")
        import json
        self.write(os.path.join(directory, "settings.json"), json.dumps(values))
        return directory

    def test_the_saved_personaPoolDir_is_the_pool_when_the_environment_names_none(self):
        # What the tray app reads: a user who set only the setting gets the same
        # pool from the orb and from here. json.dumps escapes a Windows path's
        # backslashes, which the resolver has to undo.
        saved = self.dir("saved-pool")
        self.persona(saved, "atlas")
        env = {"ORBWEAVER_PERSONAS": "", "ORBWEAVER_SETTINGS_DIR": self.settings_dir(personaPoolDir=saved)}

        rows = self.ok("pm=atlas", extra_env=env)

        self.assertTrue(same_path(rows[0]["persona_file"], os.path.join(saved, "profiles", "atlas", "atlas.md")))

    def test_the_environment_variable_outranks_the_saved_setting(self):
        saved = self.dir("saved-pool")
        self.persona(saved, "atlas")
        env = {"ORBWEAVER_SETTINGS_DIR": self.settings_dir(personaPoolDir=saved)}   # ORBWEAVER_PERSONAS stays set

        self.assertIn("unknown persona 'atlas'", self.refused("pm=atlas", extra_env=env))

    def test_a_relative_pool_is_refused_whichever_way_it_was_given(self):
        problem = self.refused("pm=atlas", extra_env={"ORBWEAVER_PERSONAS": "relative/pool"})
        self.assertIn("ORBWEAVER_PERSONAS must be an absolute path", problem)

        env = {"ORBWEAVER_PERSONAS": "", "ORBWEAVER_SETTINGS_DIR": self.settings_dir(personaPoolDir="also/relative")}
        self.assertIn("personaPoolDir must be an absolute path", self.refused("pm=atlas", extra_env=env))

    def test_a_tilde_pool_means_the_home_directory(self):
        self.persona(os.path.join(self.home, "mypool"), "atlas")

        rows = self.ok("pm=atlas", extra_env={"ORBWEAVER_PERSONAS": "~/mypool"})

        self.assertTrue(same_path(rows[0]["persona_file"], os.path.join(self.home, "mypool", "profiles", "atlas", "atlas.md")))

    def test_members_of_a_team_the_account_already_has_running_count_as_live(self):
        # Under Git Bash `ps` cannot see a native claude.exe, so the teams Claude
        # Code records are the source that works everywhere. The team's own name,
        # which sits above the members list, is not a member.
        self.alpha()
        self.write(os.path.join(self.account, "teams", "session-1", "config.json"),
                   '{\n  "name": "atlas",\n  "members": [\n    {"name": "team-lead"},\n    {\n      "name": "vector"\n    }\n  ]\n}\n')

        rows = self.ok("alpha")

        by = {(r["slot"], r["persona"]): r["instance"] for r in rows}
        self.assertEqual(by[("pm", "atlas")], "atlas")             # the team's name is not a member
        self.assertEqual(by[("architect", "vector")], "vector_2")  # a live member is

    # --- under WSL, the pool on the Windows side -------------------------------------

    def windows_users(self):
        users = self.dir("windows-users")
        for name in ("Default", "Public"):
            self.dir("windows-users", name, ".orbweaver", "profiles", "ghost")
        self.persona(os.path.join(users, "alice", ".orbweaver"), "atlas")
        self.dir("windows-users", "bob")                                  # a user with no pool
        return users

    def test_under_wsl_with_no_pool_named_each_windows_users_pool_is_looked_in(self):
        users = self.windows_users()
        wsl = {"WSL_DISTRO_NAME": "Ubuntu", "ORBWEAVER_PERSONAS": "", "ORBWEAVER_WINDOWS_USERS": users}

        rows = self.ok("pm=atlas", extra_env=wsl)

        self.assertTrue(same_path(
            rows[0]["persona_file"], os.path.join(users, "alice", ".orbweaver", "profiles", "atlas", "atlas.md")))

        code, out, _ = self.run_script("roots", extra_env=wsl)
        pools = [line.split("\t")[1] for line in out.splitlines() if line.startswith("pool")]
        self.assertTrue(any(same_path(p, os.path.join(users, "alice", ".orbweaver")) for p in pools))
        self.assertFalse(any("Default" in p or "Public" in p or "bob" in p for p in pools))

    def test_a_pool_that_was_named_is_never_searched_for_a_windows_one(self):
        users = self.windows_users()
        wsl = {"WSL_DISTRO_NAME": "Ubuntu", "ORBWEAVER_WINDOWS_USERS": users}   # ORBWEAVER_PERSONAS stays set

        self.assertIn("unknown persona 'atlas'", self.refused("pm=atlas", extra_env=wsl))

    def test_outside_wsl_the_windows_users_are_not_looked_in(self):
        users = self.windows_users()

        self.assertIn("unknown persona 'atlas'", self.refused(
            "pm=atlas", extra_env={"ORBWEAVER_PERSONAS": "", "ORBWEAVER_WINDOWS_USERS": users}))

    def test_help_and_an_unknown_command(self):
        code, out, _ = self.run_script("--help")
        self.assertEqual(code, 0)
        self.assertIn("orbweaver-team.sh resolve", out)

        code, _, err = self.run_script("frobnicate")
        self.assertEqual(code, 2)
        self.assertIn("unknown command", err)


if __name__ == "__main__":
    unittest.main()
