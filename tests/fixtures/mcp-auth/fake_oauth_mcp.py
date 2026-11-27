#!/usr/bin/env python3
"""CB-359: a throwaway remote MCP server with OAuth, for capturing the approval
prompt Claude Code shows for `complete_authentication`. Loopback only, no real
credential: every code and token is a dummy value minted here.

  python3 fake_oauth_mcp.py [port]          (default 8765, binds 127.0.0.1)

/mcp answers 401 with OAuth metadata until a Bearer token it issued is
presented. /authorize approves at once and 302-redirects to redirect_uri with
`code` and `state`, so no person has to sign in. /token issues a dummy token.
Once authenticated, /mcp serves one tool, `ping`.
"""
import json, secrets, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, urlencode

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
BASE = f"http://127.0.0.1:{PORT}"
CODES, TOKENS, CLIENTS = {}, set(), {}


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *a):
        sys.stderr.write("fake_oauth_mcp: " + (fmt % a) + "\n")

    def _send(self, code, body=b"", ctype="application/json", headers=None):
        if not isinstance(body, bytes):
            body = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        u = urlparse(self.path)
        if u.path.startswith("/.well-known/oauth-protected-resource"):
            return self._send(200, {"resource": f"{BASE}/mcp", "authorization_servers": [BASE]})
        if u.path.startswith("/.well-known/oauth-authorization-server"):
            return self._send(200, {
                "issuer": BASE, "authorization_endpoint": f"{BASE}/authorize",
                "token_endpoint": f"{BASE}/token", "registration_endpoint": f"{BASE}/register",
                "response_types_supported": ["code"], "grant_types_supported": ["authorization_code"],
                "code_challenge_methods_supported": ["S256"],
                "token_endpoint_auth_methods_supported": ["none"]})
        if u.path == "/authorize":
            q = {k: v[0] for k, v in parse_qs(u.query).items()}
            code = "dummy-code-" + secrets.token_hex(4)
            CODES[code] = q.get("client_id")
            back = {"code": code}
            if "state" in q:
                back["state"] = q["state"]
            sep = "&" if "?" in q["redirect_uri"] else "?"
            return self._send(302, b"", "text/plain", {"Location": q["redirect_uri"] + sep + urlencode(back)})
        if u.path == "/mcp":
            return self._send(405, b"", "text/plain")
        self._send(404, {"error": "not found"})

    def do_POST(self):
        u = urlparse(self.path)
        raw = self._body()
        if u.path == "/register":
            meta = json.loads(raw or b"{}")
            cid = "dummy-client-" + secrets.token_hex(3)
            CLIENTS[cid] = meta
            return self._send(201, {**meta, "client_id": cid, "token_endpoint_auth_method": "none"})
        if u.path == "/token":
            form = {k: v[0] for k, v in parse_qs(raw.decode()).items()}
            if form.get("code") not in CODES:
                return self._send(400, {"error": "invalid_grant"})
            tok = "dummy-token-" + secrets.token_hex(4)
            TOKENS.add(tok)
            return self._send(200, {"access_token": tok, "token_type": "Bearer", "expires_in": 3600})
        if u.path == "/mcp":
            auth = self.headers.get("Authorization", "")
            if not auth.startswith("Bearer ") or auth[7:] not in TOKENS:
                return self._send(401, {"error": "unauthorized"}, headers={
                    "WWW-Authenticate": f'Bearer resource_metadata="{BASE}/.well-known/oauth-protected-resource"'})
            return self._mcp(json.loads(raw))
        self._send(404, {"error": "not found"})

    def do_DELETE(self):
        self._send(200, b"", "text/plain")

    def _mcp(self, msg):
        if "id" not in msg:
            return self._send(202, b"", "text/plain")
        m, i = msg.get("method"), msg["id"]
        if m == "initialize":
            res = {"protocolVersion": msg["params"].get("protocolVersion", "2025-03-26"),
                   "capabilities": {"tools": {}}, "serverInfo": {"name": "fakeauth", "version": "1"}}
        elif m == "tools/list":
            res = {"tools": [{"name": "ping", "description": "Reply pong",
                              "inputSchema": {"type": "object", "properties": {}}}]}
        elif m == "tools/call":
            res = {"content": [{"type": "text", "text": "pong"}]}
        else:
            return self._send(200, {"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "no such method"}})
        self._send(200, {"jsonrpc": "2.0", "id": i, "result": res})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
