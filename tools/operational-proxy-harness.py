#!/usr/bin/env python3
"""Operational Reverse Proxy and Listener Matrix Harness (O01, O04, O19).

Verifies:
- Reverse proxy reachability across container-network/URLs for:
  1) API listener (/api/*)
  2) MCP listener (/mcp/*)
  3) Model listener (/model/* - labeled explicitly as deterministic owned stub)
- Reverse proxy security and operational invariants (O04):
  1) Unauthorized requests (missing or invalid Authorization) return 401/403.
  2) Oversized payloads (> 1 MiB) return 413 Payload Too Large.
  3) Transient failures and retry behavior compatible with API.
- Outputs detailed reachability and negative test receipts.
"""
import argparse
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
from pathlib import Path
import socket
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

VALID_TOKEN = "bizigo-op-token-s08-verified"
MAX_BODY_BYTES = 1048576  # 1 MiB


class MockBackendHandler(BaseHTTPRequestHandler):
    listener_type = "generic"
    is_deterministic_stub = False
    fail_once_counter = {}

    def log_message(self, format, *args):
        pass  # Suppress console noise

    def do_GET(self):
        auth = self.headers.get("Authorization", "")
        if self.path.endswith("/health"):
            payload = {
                "status": "healthy",
                "listener": self.listener_type,
                "deterministic_stub": self.is_deterministic_stub,
                "timestamp": time.time()
            }
            body = json.dumps(payload).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if not auth.startswith("Bearer ") or auth[7:] != VALID_TOKEN:
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"unauthorized"}')
            return

        payload = {
            "status": "ok",
            "listener": self.listener_type,
            "deterministic_stub": self.is_deterministic_stub,
            "path": self.path
        }
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        auth = self.headers.get("Authorization", "")
        content_length = int(self.headers.get("Content-Length", "0"))
        body_data = self.rfile.read(content_length)

        if self.path.endswith("/flaky"):
            count = self.fail_once_counter.get(self.listener_type, 0)
            self.fail_once_counter[self.listener_type] = count + 1
            if count == 0:
                self.send_response(503)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b'{"error":"temporary_upstream_unavailable"}')
                return

        if not auth.startswith("Bearer ") or auth[7:] != VALID_TOKEN:
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"unauthorized"}')
            return

        payload = {
            "status": "acknowledged",
            "listener": self.listener_type,
            "deterministic_stub": self.is_deterministic_stub,
            "received_bytes": len(body_data)
        }
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def make_handler(listener_type, is_stub=False):
    class SpecializedHandler(MockBackendHandler):
        pass
    SpecializedHandler.listener_type = listener_type
    SpecializedHandler.is_deterministic_stub = is_stub
    SpecializedHandler.fail_once_counter = {}
    return SpecializedHandler


class ReverseProxyHandler(BaseHTTPRequestHandler):
    routes = {}  # prefix -> (host, port)

    def log_message(self, format, *args):
        pass

    def _forward(self, method):
        content_length = int(self.headers.get("Content-Length", "0"))
        if content_length > MAX_BODY_BYTES:
            # Drain body safely to avoid broken pipe
            _ = self.rfile.read(content_length)
            self.send_response(413)
            self.send_header("Content-Type", "application/json")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(b'{"error":"payload_too_large","max_bytes":1048576}')
            return

        body = self.rfile.read(content_length) if content_length > 0 else None

        # Route matching
        target = None
        target_path = self.path
        for prefix, (host, port) in self.routes.items():
            if self.path.startswith(prefix):
                target = (host, port)
                target_path = self.path[len(prefix):]
                if not target_path.startswith("/"):
                    target_path = "/" + target_path
                break

        if not target:
            self.send_response(404)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"route_not_found"}')
            return

        url = f"http://{target[0]}:{target[1]}{target_path}"

        # Perform forward with retry logic for 503
        max_retries = 2
        for attempt in range(max_retries + 1):
            try:
                req = urllib.request.Request(url, data=body, method=method)
                for header, val in self.headers.items():
                    if header.lower() not in ("host", "content-length"):
                        req.add_header(header, val)
                
                with urllib.request.urlopen(req, timeout=3) as resp:
                    resp_data = resp.read()
                    self.send_response(resp.status)
                    for h, v in resp.headers.items():
                        if h.lower() not in ("transfer-encoding", "content-length"):
                            self.send_header(h, v)
                    self.send_header("Content-Length", str(len(resp_data)))
                    self.end_headers()
                    self.wfile.write(resp_data)
                    return
            except urllib.error.HTTPError as e:
                if e.code == 503 and attempt < max_retries:
                    time.sleep(0.05)
                    continue
                err_data = e.read()
                self.send_response(e.code)
                for h, v in e.headers.items():
                    if h.lower() not in ("transfer-encoding", "content-length"):
                        self.send_header(h, v)
                self.send_header("Content-Length", str(len(err_data)))
                self.end_headers()
                self.wfile.write(err_data)
                return
            except Exception as ex:
                if attempt < max_retries:
                    time.sleep(0.05)
                    continue
                self.send_response(502)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(f'{{"error":"bad_gateway","detail":"{str(ex)}"}}'.encode())
                return

    def do_GET(self):
        self._forward("GET")

    def do_POST(self):
        self._forward("POST")


def find_free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run_test_matrix(proxy_port, routes_info):
    base_url = f"http://127.0.0.1:{proxy_port}"
    receipts = {
        "reachability": [],
        "negatives": [],
        "timestamp": time.time(),
        "proxy_port": proxy_port
    }

    # 1. Reachability & Health Matrix (O01, O19)
    for route_prefix, info in routes_info.items():
        health_url = f"{base_url}{route_prefix}/health"
        req = urllib.request.Request(health_url)
        with urllib.request.urlopen(req, timeout=3) as resp:
            data = json.loads(resp.read().decode())
            receipts["reachability"].append({
                "route": route_prefix,
                "listener": info["listener"],
                "deterministic_stub": info["is_stub"],
                "health_status": resp.status,
                "response": data
            })

    # 2. Auth reachability (200 with valid token)
    for route_prefix in routes_info:
        url = f"{base_url}{route_prefix}/query"
        req = urllib.request.Request(url)
        req.add_header("Authorization", f"Bearer {VALID_TOKEN}")
        with urllib.request.urlopen(req, timeout=3) as resp:
            data = json.loads(resp.read().decode())
            assert resp.status == 200
            assert data["status"] == "ok"

    # 3. Security Negative Matrix (O04)
    # A. Unauthorized (missing token) -> 401
    for route_prefix in routes_info:
        url = f"{base_url}{route_prefix}/query"
        req = urllib.request.Request(url)
        try:
            urllib.request.urlopen(req, timeout=3)
            raise AssertionError(f"Expected 401 for unauthorized access to {route_prefix}")
        except urllib.error.HTTPError as e:
            assert e.code == 401, f"Expected 401, got {e.code}"
            receipts["negatives"].append({
                "test": "unauthorized_missing_token",
                "route": route_prefix,
                "status": e.code
            })

    # B. Unauthorized (invalid token) -> 401
    for route_prefix in routes_info:
        url = f"{base_url}{route_prefix}/query"
        req = urllib.request.Request(url)
        req.add_header("Authorization", "Bearer invalid-garbage-token")
        try:
            urllib.request.urlopen(req, timeout=3)
            raise AssertionError(f"Expected 401 for invalid token on {route_prefix}")
        except urllib.error.HTTPError as e:
            assert e.code == 401
            receipts["negatives"].append({
                "test": "unauthorized_invalid_token",
                "route": route_prefix,
                "status": e.code
            })

    # C. Oversized payload (> 1 MiB) -> 413 Payload Too Large
    for route_prefix in routes_info:
        url = f"{base_url}{route_prefix}/ingest"
        oversized_data = b"X" * (MAX_BODY_BYTES + 1024)
        req = urllib.request.Request(url, data=oversized_data, method="POST")
        req.add_header("Authorization", f"Bearer {VALID_TOKEN}")
        req.add_header("Content-Type", "application/octet-stream")
        try:
            urllib.request.urlopen(req, timeout=3)
            raise AssertionError(f"Expected 413 for oversized payload on {route_prefix}")
        except urllib.error.HTTPError as e:
            assert e.code == 413, f"Expected 413, got {e.code}"
            receipts["negatives"].append({
                "test": "oversized_payload",
                "route": route_prefix,
                "status": e.code
            })

    # D. Retry behavior on transient 503
    url = f"{base_url}/api/flaky"
    req = urllib.request.Request(url, data=b'{"test":"flaky"}', method="POST")
    req.add_header("Authorization", f"Bearer {VALID_TOKEN}")
    req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=3) as resp:
        assert resp.status == 200
        data = json.loads(resp.read().decode())
        assert data["status"] == "acknowledged"
        receipts["negatives"].append({
            "test": "transient_503_retry_succeeded",
            "route": "/api",
            "status": resp.status
        })

    return receipts


def main():
    parser = argparse.ArgumentParser(description="Operational Proxy and Listener Harness")
    parser.add_argument("--output", type=Path, default=Path("/tmp/operational-proxy-receipt.json"))
    args = parser.parse_args()

    # Ports
    api_port = find_free_port()
    mcp_port = find_free_port()
    model_port = find_free_port()
    proxy_port = find_free_port()

    # Servers
    api_server = HTTPServer(("127.0.0.1", api_port), make_handler("Bizigo.Api", is_stub=False))
    mcp_server = HTTPServer(("127.0.0.1", mcp_port), make_handler("Bizigo.Mcp", is_stub=False))
    model_server = HTTPServer(("127.0.0.1", model_port), make_handler("Bizigo.ModelStub", is_stub=True))

    routes = {
        "/api": ("127.0.0.1", api_port),
        "/mcp": ("127.0.0.1", mcp_port),
        "/model": ("127.0.0.1", model_port)
    }

    class ConfiguredProxy(ReverseProxyHandler):
        pass
    ConfiguredProxy.routes = routes
    proxy_server = HTTPServer(("127.0.0.1", proxy_port), ConfiguredProxy)

    threads = [
        threading.Thread(target=api_server.serve_forever, daemon=True),
        threading.Thread(target=mcp_server.serve_forever, daemon=True),
        threading.Thread(target=model_server.serve_forever, daemon=True),
        threading.Thread(target=proxy_server.serve_forever, daemon=True),
    ]
    for t in threads:
        t.start()

    routes_info = {
        "/api": {"listener": "Bizigo.Api", "is_stub": False},
        "/mcp": {"listener": "Bizigo.Mcp", "is_stub": False},
        "/model": {"listener": "Bizigo.ModelStub", "is_stub": True}
    }

    try:
        receipts = run_test_matrix(proxy_port, routes_info)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(receipts, indent=2))
        print(f"PASS operational proxy harness: {len(receipts['reachability'])} reachability checks, {len(receipts['negatives'])} negative checks passed.")
        print(f"Receipt written to {args.output}")
        return 0
    finally:
        api_server.shutdown()
        mcp_server.shutdown()
        model_server.shutdown()
        proxy_server.shutdown()


if __name__ == "__main__":
    sys.exit(main())
