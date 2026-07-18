#!/usr/bin/env python3
import json
import os
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Lock


HOST = os.environ.get("ASW_USAGE_HOST", "0.0.0.0")
PORT = int(os.environ.get("ASW_USAGE_PORT", "8787"))
TOKEN = os.environ.get("ASW_USAGE_TOKEN", "")
ADMIN_KEY = os.environ.get("ASW_USAGE_ADMIN_KEY", "")
TTL_SECONDS = int(os.environ.get("ASW_USAGE_TTL", "45"))
CARD_CODES = set("DMWLOZ")

lock = Lock()
users = {}


def now_ts():
    return int(time.time())


def trim(value, max_len):
    value = value or ""
    value = value.replace("\r", " ").replace("\n", " ").strip()
    return value[:max_len]


def normalize_card(value):
    value = trim(value, 8).upper()
    if not value:
        return "U"
    card = value[0]
    return card if card in CARD_CODES else "U"


def purge_stale(ts=None):
    if ts is None:
        ts = now_ts()
    stale = [pid for pid, row in users.items() if ts - row.get("last_seen", 0) > TTL_SECONDS]
    for pid in stale:
        users.pop(pid, None)


def json_bytes(payload):
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    server_version = "ASWUsage/1.0"

    def log_message(self, fmt, *args):
        return

    def send_text(self, code, body, content_type="text/plain; charset=utf-8"):
        data = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def send_json(self, code, payload):
        data = json_bytes(payload)
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def authed(self):
        if not TOKEN:
            return False
        return self.headers.get("X-ASW-Token", "") == TOKEN

    def admin_authed(self, query):
        if not ADMIN_KEY:
            return False
        if self.headers.get("X-ASW-Admin", "") == ADMIN_KEY:
            return True
        return query.get("key", [""])[0] == ADMIN_KEY

    def read_form(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        body = self.rfile.read(length) if length > 0 else b""
        return urllib.parse.parse_qs(body.decode("utf-8", errors="replace"), keep_blank_values=True)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)

        if parsed.path == "/health":
            self.send_text(200, "ok\n")
            return

        if parsed.path in ("/", "/api/stats"):
            if not self.admin_authed(query):
                self.send_json(403, {"ok": False, "error": "forbidden"})
                return

            ts = now_ts()
            with lock:
                purge_stale(ts)
                rows = []
                for row in users.values():
                    rows.append({
                        "pid": row.get("pid", ""),
                        "uid": row.get("uid", ""),
                        "name": row.get("name", ""),
                        "features": row.get("features", ""),
                        "client": row.get("client", ""),
                        "card": row.get("card", "U"),
                        "ip": row.get("ip", ""),
                        "first_seen": row.get("first_seen", 0),
                        "last_seen": row.get("last_seen", 0),
                        "age": ts - row.get("first_seen", ts),
                        "ttl_left": max(0, TTL_SECONDS - (ts - row.get("last_seen", ts))),
                    })
            rows.sort(key=lambda r: r["last_seen"], reverse=True)
            self.send_json(200, {"ok": True, "online": len(rows), "ttl": TTL_SECONDS, "users": rows})
            return

        self.send_json(404, {"ok": False, "error": "not_found"})

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        if not self.authed():
            self.send_text(403, "forbidden\n")
            return

        if parsed.path == "/api/heartbeat":
            form = self.read_form()
            pid = trim(form.get("pid", [""])[0], 32)
            if not pid.isdigit() or pid == "0":
                self.send_text(400, "bad_pid\n")
                return

            ts = now_ts()
            status = trim(form.get("status", ["online"])[0], 16)
            with lock:
                purge_stale(ts)
                if status == "offline":
                    users.pop(pid, None)
                    self.send_text(200, "ok\n")
                    return

                row = users.get(pid, {})
                row.update({
                    "pid": pid,
                    "uid": trim(form.get("uid", [""])[0], 16),
                    "name": trim(form.get("name", [""])[0], 64),
                    "features": trim(form.get("features", [""])[0], 160),
                    "client": trim(form.get("client", [""])[0], 80),
                    "card": normalize_card(form.get("card", [""])[0]),
                    "version": trim(form.get("version", [""])[0], 64),
                    "ip": self.client_address[0],
                    "last_seen": ts,
                })
                row.setdefault("first_seen", ts)
                users[pid] = row
            self.send_text(200, "ok\n")
            return

        if parsed.path == "/api/lookup":
            form = self.read_form()
            viewer = trim(form.get("viewer", [""])[0], 32)
            viewer_client = trim(form.get("client", [""])[0], 80)
            raw_ids = form.get("ids", [""])[0]
            requested = [x.strip() for x in raw_ids.split(",") if x.strip().isdigit()]
            ts = now_ts()
            active = []
            with lock:
                purge_stale(ts)
                viewer_row = users.get(viewer)
                if not viewer_row or viewer_row.get("card") != "Z":
                    self.send_text(200, "ok\n")
                    return
                if not viewer_client or viewer_row.get("client", "") != viewer_client:
                    self.send_text(200, "ok\n")
                    return

                for pid in requested:
                    row = users.get(pid)
                    if row and ts - row.get("last_seen", 0) <= TTL_SECONDS:
                        card = row.get("card", "U")
                        if card != "Z":
                            active.append("%s|%s" % (pid, card))
            self.send_text(200, "ok\n" + "\n".join(active) + ("\n" if active else ""))
            return

        self.send_text(404, "not_found\n")


def main():
    if not TOKEN:
        raise SystemExit("ASW_USAGE_TOKEN is required")
    if not ADMIN_KEY:
        raise SystemExit("ASW_USAGE_ADMIN_KEY is required")

    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    print("ASW usage server listening on %s:%s ttl=%ss" % (HOST, PORT, TTL_SECONDS), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
