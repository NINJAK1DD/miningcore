#!/usr/bin/env python3
"""Loopback-only lab fault: drop one successful RPC reply after upstream completes.

Place between the lab pool and Oyster to exercise an uncertain send. Never print
credentials, wallet passwords, request bodies or arbitrary RPC errors.
"""
import argparse
import http.server
import ipaddress
import json
import pathlib
import socket
import threading
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--listen-port", type=int, required=True)
parser.add_argument("--upstream", required=True)
parser.add_argument("--method", choices=("sendmany", "submitblock"), default="sendmany")
parser.add_argument("--evidence", type=pathlib.Path, required=True)
args = parser.parse_args()
url = urllib.parse.urlsplit(args.upstream)
if url.scheme != "http" or url.username or url.password or not ipaddress.ip_address(url.hostname).is_loopback:
    parser.error("upstream must be an explicit loopback HTTP address without URL credentials")
if args.evidence.exists():
    parser.error("evidence file already exists; choose a new test output")
lock = threading.Lock()
dropped = False

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        global dropped
        self.connection.settimeout(60)
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 9_000_000:
                self.send_error(413)
                return
            payload = self.rfile.read(length)
            request = json.loads(payload)
            forwarded = urllib.request.Request(args.upstream, payload, {
                "Authorization": self.headers.get("Authorization", ""), "Content-Type": "application/json"})
            class NoRedirect(urllib.request.HTTPRedirectHandler):
                def redirect_request(self, *_):
                    return None
            with urllib.request.build_opener(NoRedirect).open(forwarded, timeout=60) as response:
                result = response.read(2_000_001)
            if len(result) > 2_000_000:
                self.send_error(502)
                return
            parsed = json.loads(result)
            with lock:
                if not dropped and request.get("method") == args.method and parsed.get("error") is None:
                    value = parsed.get("result")
                    success = value is None if args.method == "submitblock" else (
                        isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value))
                    if success:
                        args.evidence.write_text(json.dumps({"method": args.method,
                            "reply_lost_after_upstream_success": True,
                            "transaction": value if args.method == "sendmany" else None}, indent=2) + "\n")
                        dropped = True
                        self.connection.shutdown(socket.SHUT_RDWR)
                        self.connection.close()
                        return
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(result)))
            self.end_headers()
            self.wfile.write(result)
        except (OSError, ValueError):
            self.close_connection = True

server = http.server.ThreadingHTTPServer(("127.0.0.1", args.listen_port), Handler)
server.daemon_threads = True
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
