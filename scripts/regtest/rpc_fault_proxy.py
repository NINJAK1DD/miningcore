#!/usr/bin/env python3
"""Small deterministic JSON-RPC fault proxy for daemon-backed regtest.

The control file is reloaded whenever its timestamp changes. Example:

{
  "rules": [
    {"method": "submitauxblock", "action": "drop_after_forward", "count": 1}
  ]
}

Supported actions:
  drop_after_forward  Forward the request, then close without returning its response.
  replace_response    Forward, then merge `response` into matching response object(s).
  reverse_batch       Forward, then reverse a JSON-RPC batch response array.
  delay_response      Forward, then wait `milliseconds` before returning.
  strip_transactions  Forward, then replace result.tx with an empty array.
  freeze_response     Cache the first matching response and replay it without forwarding.

Every request and upstream response is written as JSON Lines when --log is supplied.
The proxy deliberately does not log HTTP authorization headers.

Rules may include `params_min_length` to avoid matching parameterless capability
probes. For example, a submitblock fault intended only for real submissions uses
`"params_min_length": 1`.
"""

from __future__ import annotations

import argparse
import json
import socket
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any


class FaultState:
    def __init__(self, control_path: Path | None, log_path: Path | None):
        self.control_path = control_path
        self.log_path = log_path
        self.lock = threading.Lock()
        self.control_mtime_ns: int | None = None
        self.rules: list[dict[str, Any]] = []
        self.remaining: list[int | None] = []
        self.cached_responses: dict[str, Any] = {}

    def _reload_locked(self) -> None:
        if self.control_path is None:
            return

        try:
            mtime_ns = self.control_path.stat().st_mtime_ns
        except FileNotFoundError:
            mtime_ns = None

        if mtime_ns == self.control_mtime_ns:
            return

        self.control_mtime_ns = mtime_ns
        document = {}
        if mtime_ns is not None:
            document = json.loads(self.control_path.read_text(encoding="utf-8"))

        self.rules = list(document.get("rules", []))
        self.remaining = [
            int(rule["count"]) if rule.get("count") is not None else None
            for rule in self.rules
        ]
        self.cached_responses = {}

    def acquire_rules(self, payload: Any) -> list[dict[str, Any]]:
        result: list[dict[str, Any]] = []
        with self.lock:
            self._reload_locked()
            for index, rule in enumerate(self.rules):
                if not any(rule_matches_request(rule, request)
                           for request in request_items(payload)):
                    continue

                remaining = self.remaining[index]
                if remaining is not None:
                    if remaining <= 0:
                        continue
                    self.remaining[index] = remaining - 1

                result.append(dict(rule))

        return result

    def get_cached_response(self, key: str) -> Any:
        with self.lock:
            return self.cached_responses.get(key)

    def set_cached_response(self, key: str, response: Any) -> None:
        with self.lock:
            self.cached_responses[key] = response

    def log(self, event: dict[str, Any]) -> None:
        if self.log_path is None:
            return

        event = {"time": time.time(), **event}
        line = json.dumps(event, separators=(",", ":"), sort_keys=True)
        with self.lock:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
            with self.log_path.open("a", encoding="utf-8") as stream:
                stream.write(line + "\n")


def request_methods(payload: Any) -> set[str]:
    requests = request_items(payload)
    return {
        str(item.get("method"))
        for item in requests
        if isinstance(item, dict) and item.get("method") is not None
    }


def request_items(payload: Any) -> list[dict[str, Any]]:
    requests = payload if isinstance(payload, list) else [payload]
    return [item for item in requests if isinstance(item, dict)]


def rule_matches_request(rule: dict[str, Any], request: dict[str, Any]) -> bool:
    method = rule.get("method", "*")
    if method != "*" and request.get("method") != method:
        return False

    params_min_length = rule.get("params_min_length")
    if params_min_length is None:
        return True

    params = request.get("params")
    if not isinstance(params, (list, dict)):
        return False

    return len(params) >= int(params_min_length)


def matching_responses(payload: Any, response: Any, method: str) -> list[dict[str, Any]]:
    requests = payload if isinstance(payload, list) else [payload]
    responses = response if isinstance(response, list) else [response]
    request_ids = {
        item.get("id")
        for item in requests
        if isinstance(item, dict) and (method == "*" or item.get("method") == method)
    }
    return [
        item for item in responses
        if isinstance(item, dict) and item.get("id") in request_ids
    ]


class ProxyHandler(BaseHTTPRequestHandler):
    server: "FaultProxyServer"

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        payload = json.loads(body.decode("utf-8"))
        methods = request_methods(payload)
        rules = self.server.state.acquire_rules(payload)
        self.server.state.log({"event": "request", "methods": sorted(methods), "body": payload})

        for index, rule in enumerate(rules):
            if rule.get("action") != "freeze_response":
                continue

            key = str(rule.get("key", f"{rule.get('method', '*')}:{index}"))
            cached = self.server.state.get_cached_response(key)
            if cached is not None:
                self.server.state.log({"event": "fault", "action": "freeze_response_replay",
                                       "methods": sorted(methods)})
                self._send_json(200, "application/json", cached)
                return

        upstream_request = urllib.request.Request(
            self.server.upstream_url,
            data=body,
            method="POST",
            headers={
                "Content-Type": self.headers.get("Content-Type", "application/json"),
                **({"Authorization": self.headers["Authorization"]}
                   if self.headers.get("Authorization") else {}),
            },
        )

        try:
            with urllib.request.urlopen(upstream_request, timeout=self.server.upstream_timeout) as upstream:
                status = upstream.status
                response_body = upstream.read()
                content_type = upstream.headers.get("Content-Type", "application/json")
        except urllib.error.HTTPError as error:
            status = error.code
            response_body = error.read()
            content_type = error.headers.get("Content-Type", "application/json")

        response = json.loads(response_body.decode("utf-8"))
        self.server.state.log({"event": "upstream_response", "methods": sorted(methods),
                               "status": status, "body": response})

        for rule in rules:
            action = rule.get("action")
            method = rule.get("method", "*")

            if action == "drop_after_forward":
                self.server.state.log({"event": "fault", "action": action,
                                       "methods": sorted(methods)})
                self.close_connection = True
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                self.connection.close()
                return

            if action == "replace_response":
                for item in matching_responses(payload, response, method):
                    item.update(rule.get("response", {}))

            elif action == "reverse_batch" and isinstance(response, list):
                response.reverse()

            elif action == "delay_response":
                time.sleep(max(0, int(rule.get("milliseconds", 0))) / 1000)

            elif action == "strip_transactions":
                for item in matching_responses(payload, response, method):
                    result = item.get("result")
                    if isinstance(result, dict):
                        result["tx"] = []

            elif action == "freeze_response":
                key = str(rule.get("key", f"{method}:0"))
                self.server.state.set_cached_response(key, response)

            if action in {"replace_response", "reverse_batch", "delay_response",
                          "strip_transactions", "freeze_response"}:
                self.server.state.log({"event": "fault", "action": action,
                                       "method": method, "methods": sorted(methods)})

        self._send_json(status, content_type, response)

    def _send_json(self, status: int, content_type: str, response: Any) -> None:
        output = json.dumps(response, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(output)))
        self.end_headers()
        self.wfile.write(output)

    def log_message(self, format: str, *args: Any) -> None:
        return


class FaultProxyServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address: tuple[str, int], upstream_url: str,
                 upstream_timeout: float, state: FaultState):
        super().__init__(address, ProxyHandler)
        self.upstream_url = upstream_url
        self.upstream_timeout = upstream_timeout
        self.state = state


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen-host", default="127.0.0.1")
    parser.add_argument("--listen-port", type=int, required=True)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--upstream-timeout", type=float, default=30)
    parser.add_argument("--control", type=Path)
    parser.add_argument("--log", type=Path)
    args = parser.parse_args()

    state = FaultState(args.control, args.log)
    server = FaultProxyServer((args.listen_host, args.listen_port), args.upstream,
                              args.upstream_timeout, state)
    server.serve_forever(poll_interval=0.2)


if __name__ == "__main__":
    main()
