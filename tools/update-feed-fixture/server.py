#!/usr/bin/env python3
"""A fake GitHub releases feed for testing the auto-updater by hand (CB-308).

Point the app at it and pretend to be an older build:

    python3 tools/update-feed-fixture/server.py --version 9.9.9 --asset dist/Orbweaver-9.9.9-osx-arm64.dmg
    ORBWEAVER_UPDATE_FEED_URL=http://127.0.0.1:8765 ORBWEAVER_UPDATE_CURRENT_VERSION=0.0.1 \
        /Applications/Orbweaver.app/Contents/MacOS/Orbweaver

It serves GitHub's two shapes: GET /releases/latest (one release object) and
GET /releases?per_page=N (a list, newest first), plus the assets themselves and
a SHA256SUMS.txt in sha256sum's own format ("<hex>  <name>"), computed from the
files as given.

Negative controls, each one flag, because "the updater refuses a bad download"
is only proven by handing it one:

    --tamper      serve the asset with its first byte flipped, but keep the
                  checksum of the ORIGINAL, so the download must be rejected
    --drop-sums-entry   leave the asset out of SHA256SUMS.txt
    --no-sums     404 on SHA256SUMS.txt
    --prerelease  mark the release as a prerelease (and put it only in the list
                  endpoint, as GitHub's /latest never returns one)

Standard library only, binds 127.0.0.1 only.
"""
import argparse
import hashlib
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True, help="release version, without the leading v")
    ap.add_argument("--asset", action="append", required=True, help="file to publish (repeatable)")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--body", default="## Fixture release\n\n- A fake release served by update-feed-fixture.")
    ap.add_argument("--prerelease", action="store_true")
    ap.add_argument("--tamper", action="store_true")
    ap.add_argument("--drop-sums-entry", action="store_true")
    ap.add_argument("--no-sums", action="store_true")
    args = ap.parse_args()

    files = {os.path.basename(p): p for p in args.asset}
    original = {n: open(p, "rb").read() for n, p in files.items()}
    sums = {n: hashlib.sha256(d).hexdigest() for n, d in original.items()}
    served = {n: (bytes([d[0] ^ 0xFF]) + d[1:] if args.tamper and d else d) for n, d in original.items()}
    sum_names = list(files)[1:] if args.drop_sums_entry else list(files)
    sums_text = "".join(f"{sums[n]}  {n}\n" for n in sum_names).encode()

    base = f"http://127.0.0.1:{args.port}"

    def release() -> dict:
        assets = [{"name": n, "browser_download_url": f"{base}/assets/{n}", "size": len(served[n])} for n in files]
        if not args.no_sums:
            assets.append({"name": "SHA256SUMS.txt", "browser_download_url": f"{base}/assets/SHA256SUMS.txt", "size": len(sums_text)})
        return {"tag_name": f"v{args.version}", "name": f"v{args.version}", "draft": False,
                "prerelease": args.prerelease, "body": args.body, "assets": assets}

    class Handler(BaseHTTPRequestHandler):
        def _send(self, code: int, body: bytes, ctype: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            path = self.path.split("?")[0].rstrip("/")
            sys.stderr.write(f"GET {self.path}\n")
            if path.endswith("/releases/latest"):
                if args.prerelease:  # GitHub's /latest never returns a prerelease
                    return self._send(404, b'{"message":"Not Found"}', "application/json")
                return self._send(200, json.dumps(release()).encode(), "application/json")
            if path.endswith("/releases"):
                return self._send(200, json.dumps([release()]).encode(), "application/json")
            name = path.rsplit("/", 1)[-1]
            if path.startswith("/assets/"):
                if name == "SHA256SUMS.txt" and not args.no_sums:
                    return self._send(200, sums_text, "text/plain")
                if name in served:
                    return self._send(200, served[name], "application/octet-stream")
            self._send(404, b'{"message":"Not Found"}', "application/json")

        def log_message(self, *_):  # request lines are printed above
            pass

    print(f"serving v{args.version} at {base} ({', '.join(files)})", file=sys.stderr)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
