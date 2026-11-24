#!/usr/bin/env python3
"""One-shot GitHub App manifest helper for the bug-fix org (CB-455, plan §2).

    python3 host/create_app.py --public-host 192.168.0.127

Serves one local page. Warren opens it, which auto-posts the App manifest to
github.com for the Uplift-Foundation organisation; he clicks "Create GitHub
App" there, and GitHub redirects his browser back here with a one-time `code`.
This helper exchanges the code for the App's id and private key, writes them
under ~/.config/orbweaver-bugfix/ at 0600 (outside Paperclip's folder, so the
sandboxed agents cannot reach them), and exits. It prints the URL to open and
never a secret.

Three refusals are deliberate, and each is pinned by tools/test-bugfix-host.py:

  * a callback whose `state` is not the one this run generated (a stray or
    forged request must not be able to trigger the exchange);
  * a conversion whose granted permissions differ from PERMISSIONS (the plan's
    point is that the App holds nothing else, so an App GitHub created with
    more is refused rather than stored);
  * a second callback (the server stops after the first valid one).

The webhook secret and client secret GitHub also returns are discarded, not
stored: the App has no webhook (events are empty) and the warden uses the
private key only.

GitHub's manifest flow has not been run against this organisation; only the
local half is tested, against a fake GitHub endpoint.
"""
import argparse
import hmac
import html
import http.server
import json
import os
import secrets
import sys
import threading
import urllib.error
import urllib.parse
import urllib.request

ORG = "Uplift-Foundation"
APP_NAME = "orbweaver-bugfix"
PERMISSIONS = {
    "contents": "write",
    "pull_requests": "write",
    "checks": "read",
    "actions": "read",
    "statuses": "read",
    "metadata": "read",
}
DEFAULT_CONFIG_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")


def manifest(redirect_url):
    return {
        "name": APP_NAME,
        "url": f"https://github.com/{ORG}/Orbweaver",
        "redirect_url": redirect_url,
        "public": False,
        "default_permissions": dict(PERMISSIONS),
        "default_events": [],
    }


def form_page(github_web, state, redirect_url):
    """A page that posts the manifest to GitHub as soon as it loads."""
    action = (f"{github_web}/organizations/{ORG}/settings/apps/new?"
              + urllib.parse.urlencode({"state": state}))
    body = html.escape(json.dumps(manifest(redirect_url)), quote=True)
    return (
        "<!doctype html><meta charset=utf-8><title>Create the bug-fix App</title>"
        f'<form id=f method=post action="{html.escape(action, quote=True)}">'
        f'<input type=hidden name=manifest value="{body}">'
        "<p>Sending the App manifest to GitHub...</p>"
        "<noscript><button>Continue to GitHub</button></noscript></form>"
        "<script>document.getElementById('f').submit()</script>"
    )


def permissions_problem(granted):
    """None when GitHub granted exactly PERMISSIONS, else what differs."""
    if granted == PERMISSIONS:
        return None
    extra = sorted(set(granted) - set(PERMISSIONS))
    wrong = sorted(k for k in PERMISSIONS if granted.get(k) != PERMISSIONS[k])
    return f"extra={extra} differing_or_missing={wrong}"


def convert(github_api, code, opener=urllib.request.urlopen):
    req = urllib.request.Request(
        f"{github_api}/app-manifests/{urllib.parse.quote(code, safe='')}/conversions",
        data=b"", method="POST",
        headers={"Accept": "application/vnd.github+json"})
    with opener(req, timeout=30) as r:
        return json.load(r)


def write_private(path, data):
    """Create `path` at 0600 from the first byte (no chmod-after window)."""
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    fd = os.open(path, flags, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(data)
    os.chmod(path, 0o600)


def store(config_dir, conversion):
    os.makedirs(config_dir, mode=0o700, exist_ok=True)
    os.chmod(config_dir, 0o700)
    write_private(os.path.join(config_dir, "app.pem"), conversion["pem"])
    write_private(os.path.join(config_dir, "app.json"), json.dumps(
        {"app_id": conversion["id"], "slug": conversion["slug"]}) + "\n")


class Result:
    def __init__(self):
        self.ok = False
        self.error = None
        self.done = threading.Event()


def make_handler(state, github_web, github_api, config_dir, redirect_url, result,
                 opener=urllib.request.urlopen):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):  # request lines carry the one-time code
            pass

        def _send(self, code, body, ctype="text/html; charset=utf-8"):
            data = body.encode()
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            url = urllib.parse.urlparse(self.path)
            q = urllib.parse.parse_qs(url.query)
            if url.path == "/":
                return self._send(200, form_page(github_web, state, redirect_url))
            if url.path != "/callback":
                return self._send(404, "not found")
            got = (q.get("state") or [""])[0]
            code = (q.get("code") or [""])[0]
            if result.done.is_set():
                return self._send(410, "This helper already finished.")
            if not code or not hmac.compare_digest(got, state):
                return self._send(400, "Bad or missing state; nothing was stored.")
            try:
                conv = convert(github_api, code, opener)
                problem = permissions_problem(conv.get("permissions", {}))
                if problem:
                    result.error = f"App was created with unexpected permissions: {problem}"
                    self._send(409, result.error + " Nothing was stored; delete the App.")
                else:
                    store(config_dir, conv)
                    result.ok = True
                    self._send(200, "Now choose Only select repositories -> Orbweaver "
                                    "and click Install on GitHub. You can close this tab.")
            except (urllib.error.URLError, KeyError, ValueError, OSError) as e:
                result.error = f"conversion failed: {type(e).__name__}"
                self._send(502, result.error)
            result.done.set()
    return Handler


def serve(args, opener=urllib.request.urlopen, ready=None):
    state = secrets.token_urlsafe(24)
    result = Result()
    srv = http.server.HTTPServer((args.bind, args.port), lambda *a, **k: None)
    port = srv.server_address[1]
    redirect_url = f"http://{args.public_host}:{port}/callback"
    srv.RequestHandlerClass = make_handler(
        state, args.github_web, args.github_api, args.config_dir, redirect_url,
        result, opener)
    print(f"Open this URL in a browser: http://{args.public_host}:{port}/", flush=True)
    if ready:
        ready(port, state)
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    result.done.wait(timeout=args.timeout)
    srv.shutdown()
    srv.server_close()
    if not result.done.is_set():
        print("timed out waiting for the callback", file=sys.stderr)
        return 2
    if not result.ok:
        print(result.error, file=sys.stderr)
        return 1
    print(f"stored app id and key under {args.config_dir}")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--public-host", required=True,
                   help="address Warren's browser reaches this machine at")
    p.add_argument("--bind", default="0.0.0.0")
    p.add_argument("--port", type=int, default=0)
    p.add_argument("--config-dir", default=DEFAULT_CONFIG_DIR)
    p.add_argument("--github-web", default="https://github.com")
    p.add_argument("--github-api", default="https://api.github.com")
    p.add_argument("--timeout", type=float, default=900)
    return serve(p.parse_args(argv))


if __name__ == "__main__":
    sys.exit(main())
