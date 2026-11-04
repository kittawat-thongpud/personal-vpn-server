"""Bounded, authenticated HTTPS rendezvous. Never forwards application payloads."""
import argparse
import base64
import hashlib
import http.server
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socketserver
import sqlite3
import ssl
import subprocess
import threading
import time
from urllib.parse import urlsplit, parse_qs

TTL = 600
MAX_BODY = 65536
MAX_ROOMS = 16
MAX_ROOM_BYTES = 262144
CODE = re.compile(r"^[0-9]{8}$")


class ApiError(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


def check(value, status, code):
    if not value:
        raise ApiError(status, code)


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def audit(event):
    print(json.dumps({"time": int(time.time()), "event": event}), flush=True)


class Store:
    def __init__(self, directory, base_url):
        self.directory, self.base_url = Path(directory), base_url
        self.db = sqlite3.connect(self.directory / "devices.sqlite", check_same_thread=False)
        self.db.execute("CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, hash TEXT UNIQUE, role TEXT)")
        self.db.execute("CREATE TABLE IF NOT EXISTS invites (hash TEXT PRIMARY KEY, expires INTEGER, role TEXT, room TEXT)")
        self.lock = threading.Condition(threading.RLock())
        self.rooms, self.rate = {}, {}
        self.pin = hashlib.sha256(ssl.PEM_cert_to_DER_cert((self.directory / "server.crt").read_text())).hexdigest()

    def prune(self):
        now = time.time()
        self.rooms = {k: v for k, v in self.rooms.items() if v["expires"] > now}
        self.db.execute("DELETE FROM invites WHERE expires < ?", (int(now),))
        self.db.commit()
        self.rate = {k: v for k, v in self.rate.items() if v[0] > now - 60}

    def invitation(self, role="member", room=""):
        self.prune()
        check(self.db.execute("SELECT count(*) FROM invites").fetchone()[0] < 32, 429, "invite.limit")
        ticket = secrets.token_urlsafe(24)
        self.db.execute("INSERT INTO invites VALUES (?, ?, ?, ?)", (digest(ticket), int(time.time()) + TTL, role, room))
        self.db.commit()
        data = {"v": 2, "url": self.base_url, "pin": self.pin, "ticket": ticket, "room": room}
        return "pvp2:" + base64.urlsafe_b64encode(json.dumps(data, separators=(",", ":")).encode()).decode().rstrip("=")

    def device(self, token):
        check(isinstance(token, str) and 20 <= len(token) <= 128, 401, "auth.required")
        row = self.db.execute("SELECT id, role FROM devices WHERE hash=?", (digest(token),)).fetchone()
        check(row, 401, "auth.invalid")
        return row

    def enroll(self, data):
        ticket = data.get("ticket", "")
        check(isinstance(ticket, str) and 20 <= len(ticket) <= 128, 400, "invite.invalid")
        row = self.db.execute("SELECT expires, role, room FROM invites WHERE hash=?", (digest(ticket),)).fetchone()
        check(row and row[0] > time.time(), 403, "invite.expired_or_used")
        if row[2]:
            check(row[2] in self.rooms, 410, "room.expired")
        check(self.db.execute("SELECT count(*) FROM devices").fetchone()[0] < 16, 429, "device.limit")
        token, device = secrets.token_urlsafe(32), secrets.token_hex(16)
        self.db.execute("INSERT INTO devices VALUES (?, ?, ?)", (device, digest(token), row[1]))
        self.db.execute("DELETE FROM invites WHERE hash=?", (digest(ticket),))
        self.db.commit()
        audit("device.enrolled")
        return {"token": token, "role": row[1], "device": device, "room": row[2]}

    def create(self, device):
        self.prune()
        # A restarted client must not be locked out by its abandoned pairing
        # leases. Supersede only this device's old signaling room; never touch
        # another device's room or an established peer data channel.
        for code in [k for k, r in self.rooms.items() if r["owner"] == device]:
            self.rooms[code]["expires"] = 0
            del self.rooms[code]
            self.db.execute("DELETE FROM invites WHERE room=?", (code,))
        self.db.commit()
        self.lock.notify_all()
        check(len(self.rooms) < MAX_ROOMS, 429, "room.limit")
        while True:
            code = f"{secrets.randbelow(100000000):08d}"
            if code not in self.rooms:
                break
        room = {"code": code, "owner": device, "guest": None, "expires": int(time.time()) + TTL,
                "messages": [], "bytes": 0, "seq": 0}
        self.rooms[code] = room
        audit("room.created")
        return {"code": code, "expires": room["expires"], "invite": self.invitation(room=code), "role": "owner"}

    def room(self, code, device, join=False):
        check(CODE.fullmatch(code), 400, "room.code")
        room = self.rooms.get(code)
        check(room and room["expires"] > time.time(), 410, "room.expired")
        if join:
            check(device != room["owner"], 409, "room.same_device")
            check(room["guest"] in (None, device), 409, "room.full")
            if room["guest"] is None:
                room["guest"] = device
                room["seq"] += 1
                self.lock.notify_all()
                audit("room.joined")
        check(device in (room["owner"], room["guest"]), 403, "room.not_member")
        return room

    def post(self, room, device, data):
        check(isinstance(data, dict) and set(data) <= {"kind", "signal", "candidate", "session", "messageId"}, 400, "message.fields")
        message_id = data.get("messageId")
        if message_id:
            check(isinstance(message_id, str) and re.fullmatch(r"[a-f0-9-]{36}", message_id), 400, "message.id")
            for previous in room["messages"]:
                if previous["sender"] == device and previous["data"].get("messageId") == message_id:
                    check(previous["data"] == data, 409, "message.id_conflict")
                    return {"seq": previous["seq"]}
        kind = data.get("kind")
        check(kind in ("offer", "answer", "candidate", "complete", "retry", "leave"), 400, "message.kind")
        if kind in ("offer", "answer"):
            check((kind == "offer") == (device == room["owner"]), 403, "message.role")
            s = data.get("signal")
            check(isinstance(s, dict) and s.get("v") == 1 and s.get("kind") == kind, 400, "message.signal")
            sdp = s.get("sdp", "")
            check(isinstance(sdp, str) and len(sdp) < 64000, 400, "message.sdp")
            media = [line for line in sdp.splitlines() if line.startswith("m=")]
            check(len(media) == 1 and re.fullmatch(r"m=application \d+ UDP/DTLS/SCTP webrtc-datachannel", media[0]), 400, "message.data_only")
            check(not re.search(r"\btyp relay\b", sdp, re.I), 400, "message.relay_forbidden")
        elif kind == "candidate":
            c = data.get("candidate")
            check(isinstance(c, dict) and set(c) <= {"candidate", "sdpMid", "sdpMLineIndex", "usernameFragment"}, 400, "candidate.fields")
            text = c.get("candidate", "")
            check(isinstance(text, str) and text.startswith("candidate:") and len(text) <= 1500, 400, "candidate.invalid")
            check(not re.search(r"\btyp relay\b", text, re.I), 400, "message.relay_forbidden")
        size = len(json.dumps(data).encode())
        check(len(room["messages"]) < 128 and room["bytes"] + size <= MAX_ROOM_BYTES, 429, "message.limit")
        room["seq"] += 1
        room["bytes"] += size
        room["messages"].append({"seq": room["seq"], "sender": device, "data": data})
        self.lock.notify_all()
        return {"seq": room["seq"]}

    def dispatch(self, method, path, token, data, ip):
        url = urlsplit(path)
        with self.lock:
            now = time.time()
            rate_key = digest(ip)
            start, n = self.rate.get(rate_key, (now, 0))
            if start < now - 60:
                start, n = now, 0
            check(n < 180, 429, "request.rate")
            if rate_key not in self.rate and len(self.rate) >= 1024:
                self.prune()
                check(len(self.rate) < 1024, 429, "request.limit")
            self.rate[rate_key] = start, n + 1
            if method == "GET" and url.path == "/health":
                return {"ok": True, "service": "p2p-signaling", "relay": False}
            if method == "POST" and url.path == "/v1/enroll":
                return self.enroll(data)
            device, _role = self.device(token)
            if method == "GET" and url.path == "/v1/me":
                return {"device": device, "role": _role}
            if method == "POST" and url.path == "/v1/rooms":
                return self.create(device)
            match = re.fullmatch(r"/v1/rooms/([0-9]{8})/(join|messages|poll)", url.path)
            check(match, 404, "endpoint.unknown")
            code, action = match.groups()
            room = self.room(code, device, method == "POST" and action == "join")
            role = "owner" if room["owner"] == device else "guest"
            if method == "POST" and action == "join":
                return {"code": code, "expires": room["expires"], "role": role}
            if method == "POST" and action == "messages":
                return self.post(room, device, data)
            check(method == "GET" and action == "poll", 405, "endpoint.method")
            try:
                after = int(parse_qs(url.query).get("after", ["0"])[0])
            except (ValueError, TypeError):
                raise ApiError(400, "poll.cursor") from None
            check(0 <= after <= room["seq"], 400, "poll.cursor")
            deadline = time.monotonic() + 8
            while room["seq"] <= after and room["expires"] > time.time() and time.monotonic() < deadline:
                self.lock.wait(min(1, deadline - time.monotonic()))
            check(room["expires"] > time.time(), 410, "room.expired")
            return {"cursor": room["seq"], "joined": room["guest"] is not None, "expires": room["expires"],
                    "messages": [m for m in room["messages"] if m["seq"] > after and m["sender"] != device]}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        self.request.settimeout(5)
        if isinstance(self.request, ssl.SSLSocket):
            self.request.do_handshake()
        self.request.settimeout(15)
        super().setup()

    def log_message(self, *_args):
        pass  # Never log URLs, auth headers, room codes or signaling payloads.

    def do_GET(self):
        self.respond()

    def do_POST(self):
        self.respond()

    def respond(self):
        self.close_connection = True
        try:
            check(len(self.path) <= 256, 414, "request.path")
            check(not self.headers.get("Transfer-Encoding"), 400, "request.encoding")
            length = int(self.headers.get("Content-Length", "0"))
            check(0 <= length <= MAX_BODY, 413, "request.size")
            body = self.rfile.read(length) if length else b"{}"
            check(not length or len(body) == length, 400, "request.truncated")
            data = json.loads(body)
            check(isinstance(data, dict), 400, "request.object")
            auth = self.headers.get("Authorization", "")
            result = self.server.store.dispatch(self.command, self.path, auth[7:] if auth.startswith("Bearer ") else "", data, self.client_address[0])
            status = 200
        except ApiError as error:
            status, result = error.status, {"error": error.code}
        except (ValueError, UnicodeError):
            status, result = 400, {"error": "request.invalid"}
        except Exception:
            status, result = 500, {"error": "server.failure"}
            audit("request.failed")
        payload = json.dumps(result, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    request_queue_size = 12

    def __init__(self, address, store):
        self.store = store
        self.slots = threading.BoundedSemaphore(12)
        super().__init__(address, Handler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(False):
            request.close()
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def handle_error(self, *_args):
        audit("connection.rejected")


def init(directory, endpoint):
    directory.mkdir(parents=True, exist_ok=True)
    parsed = urlsplit(endpoint)
    check(parsed.scheme == "https" and parsed.hostname and not parsed.username and not parsed.password and parsed.path in ("", "/"), 400, "endpoint.invalid")
    host = str(ipaddress.ip_address(parsed.hostname))
    check(not (directory / "server.key").exists(), 409, "server.already_initialized")
    openssl = shutil.which("openssl") or r"C:\Program Files\Git\usr\bin\openssl.exe"
    subprocess.run([openssl, "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256", "-nodes", "-sha256", "-days", "365", "-subj", "/CN=Personal VPN P2P Signaling", "-addext", f"subjectAltName=IP:{host},IP:127.0.0.1", "-keyout", str(directory / "server.key"), "-out", str(directory / "server.crt")], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    (directory / "server.key").chmod(0o600)
    (directory / "endpoint.txt").write_text(endpoint.rstrip("/"))
    store = Store(directory, endpoint.rstrip("/"))
    with store.lock:
        invitation = store.invitation("owner")
    (directory / "owner-invitation.txt").write_text(invitation)
    (directory / "owner-invitation.txt").chmod(0o600)
    print("Private owner enrollment (one use, expires in 10 minutes; paste into Windows P2P Lab, do not post in chat):")
    print(invitation)
    print("Public certificate SHA-256:", store.pin)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("init", "serve", "owner-invite"))
    parser.add_argument("--state", type=Path, default=Path("/var/lib/p2p-signal"))
    parser.add_argument("--endpoint")
    parser.add_argument("--port", type=int, default=9443)
    args = parser.parse_args()
    if args.command == "init":
        init(args.state, args.endpoint)
        return
    store = Store(args.state, (args.state / "endpoint.txt").read_text().strip())
    if args.command == "owner-invite":
        with store.lock:
            print(store.invitation("owner"))
        return
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.state / "server.crt", args.state / "server.key")
    server = Server(("0.0.0.0", args.port), store)
    server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    audit("server.ready")
    server.serve_forever()


if __name__ == "__main__":
    main()
