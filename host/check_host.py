#!/usr/bin/env python3
"""Check the host is set up the way the bug-fix org needs (CB-455, phase 0 facts).

    python3 host/check_host.py [--config ~/.config/orbweaver-bugfix/warden.json]

Read-only: it signs in to Paperclip (a login, no writes) and reads the
experimental instance settings, reads the service plist and warden.json, and
prints one line per problem. Exit 0 only when there are none. The checks
themselves are pure functions, tested without Paperclip.

Facts it pins, each measured on the mini by QA in phase 0:
  * git_worktree only works with enableIsolatedWorkspaces and
    enableWorktreeRunExecution on; enableIsolatedWorkspacesByDefault stays off.
  * the service plist sets GIT_CONFIG_GLOBAL to the empty file, or the managed
    git clone fails under the sandbox profile.
  * warden.json carries what the warden needs.
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.expanduser("~/.config/orbweaver-bugfix/warden.json")
DEFAULT_PLIST = os.path.expanduser("~/Library/LaunchAgents/ing.paperclip.paperclipai.plist")
DEFAULT_GITCONFIG = os.path.expanduser("~/.paperclip/instances/default/gitconfig")
REQUIRED_CONFIG = ("company_id", "paperclip_login", "spool")


def flag_problems(experimental):
    out = []
    for k in ("enableIsolatedWorkspaces", "enableWorktreeRunExecution"):
        if experimental.get(k) is not True:
            out.append(f"instance flag {k} is not on (git_worktree will not run)")
    if experimental.get("enableIsolatedWorkspacesByDefault") is True:
        out.append("enableIsolatedWorkspacesByDefault is on; it must stay off")
    return out


def plist_problems(text, gitconfig_path):
    m = re.search(r"<key>GIT_CONFIG_GLOBAL</key>\s*<string>([^<]*)</string>", text)
    if not m:
        return ["service plist does not set GIT_CONFIG_GLOBAL"]
    if m.group(1) != gitconfig_path:
        return [f"GIT_CONFIG_GLOBAL is {m.group(1)}, expected {gitconfig_path}"]
    return []


def gitconfig_problems(path):
    if not os.path.exists(path):
        return [f"{path} does not exist"]
    if os.path.getsize(path) != 0:
        return [f"{path} is not empty"]
    return []


def config_problems(cfg):
    out = [f"warden.json lacks {k}" for k in REQUIRED_CONFIG if not cfg.get(k)]
    for k in ("token_ceiling", "max_runs_per_day"):
        if k in cfg and not (isinstance(cfg[k], int) and cfg[k] > 0):
            out.append(f"{k} must be a positive integer")
    return out


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--config", default=DEFAULT_CONFIG)
    p.add_argument("--plist", default=DEFAULT_PLIST)
    p.add_argument("--gitconfig", default=DEFAULT_GITCONFIG)
    a = p.parse_args(argv)
    problems = []
    try:
        cfg = json.load(open(a.config))
    except (OSError, ValueError) as e:
        cfg = {}
        problems.append(f"cannot read {a.config}: {type(e).__name__}")
    problems += config_problems(cfg) if cfg else []
    problems += gitconfig_problems(a.gitconfig)
    try:
        problems += plist_problems(open(a.plist).read(), a.gitconfig)
    except OSError:
        problems.append(f"cannot read {a.plist}")
    if cfg.get("paperclip_login"):
        sys.path.insert(0, HERE)
        import warden
        try:
            pc = warden.PaperclipHttp(cfg["paperclip_login"], cfg.get("company_id", ""))
            problems += flag_problems(pc.call("GET", "/api/instance/settings/experimental") or {})
        except Exception as e:  # network, auth, shape: all mean "could not check"
            problems.append(f"could not read Paperclip's instance flags: {type(e).__name__}")
    for pr in problems:
        print("PROBLEM:", pr)
    if not problems:
        print("host ok")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
