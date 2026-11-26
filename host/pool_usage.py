#!/usr/bin/env python3
"""Publish each pooled Claude account's usage for the bug-fix org's pool router (CB-468).

    python3 host/pool_usage.py run-once [--config PATH]

Paperclip pools the org's Claude subscriptions, but it cannot read their usage:
its sign-in (`claude setup-token`) grants user:inference only, and the usage
API answers 403 to that. Each account's own Claude Code login on this host
holds a full-scope token, so this asks every account the way Orbweaver's
account orbs do (UsagePoller.cs: one `claude -p` per config dir, a `get_usage`
control request on stdin, no model call) and writes the answers to a file in
Paperclip's folder, where the router plugin
(paperclip/bugfix-org/host/pool-router) reads it. It runs outside the Paperclip
sandbox under launchd, beside the warden, and is deliberately separate from it:
a usage probe that fails must never be able to stop the warden's brakes.

Config (`~/.config/orbweaver-bugfix/pool-accounts.json`):

    {"output": "~/.paperclip/instances/default/pool-usage.json",
     "accounts": [{"connectionId": "<Paperclip AI connection id>",
                   "configDir": "~/.claude-board"},
                  {"connectionId": "...", "configDir": null}]}

`configDir: null` is the default login (CLAUDE_CONFIG_DIR removed, not set to
~/.claude, which reads an un-onboarded file; see UsageAccounts in
UsagePoller.cs). An account that cannot be read is published with status
"error" and no limits, so the router treats it as unknown, never as idle.
"""

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import tempfile

DEFAULT_CONFIG = "~/.config/orbweaver-bugfix/pool-accounts.json"
CONTROL_REQUEST = '{"type":"control_request","request_id":"cb-usage","request":{"subtype":"get_usage"}}\n'
TIMEOUT = 25
# The CLI's named windows. The two account-wide ones bind every model; the
# scoped ones bind only their own family, as in Paperclip's own probe.
WINDOWS = (("five_hour", "5-hour limit", None), ("seven_day", "Weekly limit", None),
           ("seven_day_opus", "Opus weekly limit", "seven_day_opus"),
           ("seven_day_sonnet", "Sonnet weekly limit", "seven_day_sonnet"))


def now_iso():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def payload_from_stream(stdout):
    """The get_usage payload out of the CLI's JSONL, or None."""
    for line in (stdout or "").splitlines():
        line = line.strip()
        if not line.startswith("{") or '"control_response"' not in line:
            continue
        try:
            row = json.loads(line)
        except ValueError:
            continue
        resp = row.get("response") if row.get("type") == "control_response" else None
        if isinstance(resp, dict) and resp.get("subtype") == "success" and isinstance(resp.get("response"), dict):
            return resp["response"]
    return None


def limits_from_payload(payload):
    """Paperclip-shaped limits (AiConnectionUsageLimit) from a get_usage payload."""
    rl = payload.get("rate_limits") if isinstance(payload, dict) else None
    if not payload or not payload.get("rate_limits_available") or not isinstance(rl, dict):
        return None
    out = []
    for key, label, scope in WINDOWS:
        w = rl.get(key)
        if not isinstance(w, dict):
            continue
        used = w.get("utilization")
        out.append({"id": key, "label": label, "scope": scope,
                    "usedPercent": used if isinstance(used, (int, float)) and not isinstance(used, bool) else None,
                    "resetsAt": w.get("resets_at") if isinstance(w.get("resets_at"), str) else None,
                    "limitReached": True if w.get("locked_reason") else None})
    return out or None


def reading(stdout, checked_at):
    limits = limits_from_payload(payload_from_stream(stdout))
    if limits is None:
        return {"status": "error", "checkedAt": checked_at, "limits": []}
    return {"status": "ok", "checkedAt": checked_at, "limits": limits}


def usage_env(config_dir, base=None):
    env = dict(os.environ if base is None else base)
    if config_dir:
        env["CLAUDE_CONFIG_DIR"] = os.path.expanduser(config_dir)
    else:
        env.pop("CLAUDE_CONFIG_DIR", None)
    return env


def run_claude(config_dir):
    """One account's get_usage answer (raw stdout), or "" on any failure."""
    try:
        return subprocess.run(
            ["claude", "-p", "--verbose", "--no-session-persistence",
             "--settings", '{"disableAllHooks":true}',
             "--input-format", "stream-json", "--output-format", "stream-json"],
            input=CONTROL_REQUEST, capture_output=True, text=True, timeout=TIMEOUT,
            env=usage_env(config_dir), cwd=tempfile.gettempdir()).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def publish(cfg, run=None, clock=now_iso):
    run = run or run_claude
    accounts = {}
    for acct in cfg.get("accounts") or []:
        cid = acct.get("connectionId")
        if not cid:
            continue
        accounts[cid] = reading(run(acct.get("configDir")), clock())
    doc = {"version": 1, "writtenAt": clock(), "accounts": accounts}
    out = os.path.expanduser(cfg.get("output") or "~/.paperclip/instances/default/pool-usage.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(out), prefix=".pool-usage.")
    try:
        with os.fdopen(fd, "w") as f:
            json.dump(doc, f, indent=1)
        os.chmod(tmp, 0o600)
        os.replace(tmp, out)  # the router never reads a half-written file
    except BaseException:
        if os.path.exists(tmp):
            os.unlink(tmp)
        raise
    return doc


def summary(doc):
    parts = []
    for cid, r in doc["accounts"].items():
        if r["status"] != "ok":
            parts.append(f"{cid[:8]} unreadable")
            continue
        worst = max((l["usedPercent"] for l in r["limits"] if l["usedPercent"] is not None), default=None)
        parts.append(f"{cid[:8]} {worst}%")
    return "; ".join(parts)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=["run-once"])
    ap.add_argument("--config", default=DEFAULT_CONFIG)
    a = ap.parse_args(argv)
    with open(os.path.expanduser(a.config)) as f:
        cfg = json.load(f)
    doc = publish(cfg)
    print(f"{doc['writtenAt']} {summary(doc)}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
