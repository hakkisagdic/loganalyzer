#!/usr/bin/env python3
"""Planner-only Docker owner; real production API and shipped Collector config.

--serve leaves a live local session for independent HTTP probes. --stop requests
owned cleanup through its control endpoint. No global container/process cleanup.
"""
import argparse
import gzip
import hashlib
import http.server as http_server
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
DOTNET = str(Path.home() / ".dotnet/dotnet") if (Path.home() / ".dotnet/dotnet").exists() else "dotnet"
IMAGE = "otel/opentelemetry-collector-contrib:0.159.0"


def http(url, body=None, headers=None, timeout=30):
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers or {}), timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers)


def wait_for(probe, timeout=180):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            value = probe()
            if value:
                return value
        except (OSError, ValueError, AssertionError) as error:
            last = error
        time.sleep(0.25)
    raise RuntimeError(f"readiness timeout: {last}")


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--serve", action="store_true")
    group.add_argument("--stop", action="store_true")
    parser.add_argument("--session-dir", type=Path, required=True)
    args = parser.parse_args()
    folder = args.session_dir.resolve()
    if args.stop:
        session = json.loads((folder / "session.json").read_text())
        status, _, _ = http(session["control"] + "/stop", b"", {"Authorization": "Bearer " + session["control_secret"]})
        assert status == 200
        return
    folder.mkdir(parents=True, exist_ok=True)
    assert not list(folder.iterdir()), "session directory must be empty"
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    project = "otlp-" + uuid.uuid4().hex[:10]
    allocated = set()
    def port():
        while True:
            with socket.socket() as s:
                s.bind(("127.0.0.1", 0))
                number = s.getsockname()[1]
            if number not in allocated:
                allocated.add(number)
                return number
    ports = {key: port() for key in ("PG_PORT", "CH_HTTP_PORT", "CH_NATIVE_PORT", "S3_PORT", "S3_CONSOLE_PORT", "KC_PORT")}
    api_port, proxy_port, collector_port, control_port = port(), port(), port(), port()
    issuer = f"http://localhost:{ports['KC_PORT']}"
    fixture_env = ports | {"KC_ISSUER": issuer, "PG_USER": "bizigo", "PG_PASSWORD": "fixture-password",
        "CH_USER": "bizigo", "CH_PASSWORD": "fixture-password", "S3_ACCESS_KEY": "fixture-access",
        "S3_SECRET_KEY": "fixture-secret-key", "KC_ADMIN_PASSWORD": "fixture-admin"}
    env_file = folder / "compose.env"
    env_file.write_text("\n".join(f"{k}={v}" for k, v in fixture_env.items()) + "\n")
    env_file.chmod(0o600)
    compose = ["docker", "compose", "--env-file", str(env_file), "-p", project, "-f", str(ROOT / "deploy/docker-compose.yml")]
    captures = folder / "captures"
    captures.mkdir()
    state = {"api": None, "api_log": None, "blocked": False}
    stopped = threading.Event()
    lock = threading.Lock()
    control_secret = uuid.uuid4().hex
    containers = []
    logs = (folder / "docker.log").open("wb")
    def run(argv):
        return subprocess.run(argv, cwd=ROOT, stdout=logs, stderr=subprocess.STDOUT, check=True, timeout=600)
    def token():
        data = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": "bizigo-collector",
                                      "client_secret": "bizigo-collector-dev-secret"}).encode()
        status, body, _ = http(issuer + "/realms/bizigo/protocol/openid-connect/token", data,
                              {"Content-Type": "application/x-www-form-urlencoded"})
        assert status == 200
        return json.loads(body)["access_token"]
    api_env = os.environ | {
        "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": f"http://127.0.0.1:{api_port}",
        "ConnectionStrings__ControlPlane": f"Host=127.0.0.1;Port={ports['PG_PORT']};Database=bizigo;Username=bizigo;Password=fixture-password",
        "ConnectionStrings__ClickHouse": f"Host=127.0.0.1;Port={ports['CH_HTTP_PORT']};Database=bizigo;Username=bizigo;Password=fixture-password",
        "Auth__Enabled": "true", "Auth__Authority": issuer + "/realms/bizigo", "Auth__Audience": "bizigo-api",
        "Auth__McpResource": f"http://127.0.0.1:{api_port}/mcp", "Auth__RequireHttpsMetadata": "false",
        "RawStore__ServiceUrl": f"http://127.0.0.1:{ports['S3_PORT']}", "RawStore__AccessKey": "fixture-access",
        "RawStore__SecretKey": "fixture-secret-key", "RawStore__Bucket": project, "RawStore__UploadInterval": "00:00:01",
        "Ingest__Wal__Directory": str(folder / "logs-wal"), "Ingest__Signals__Directory": str(folder / "signals"),
        "Ingest__Wal__MaxSegmentBytes": "1024", "Ingest__Wal__FlushToDisk": "true",
        "Alerting__Enabled": "false", "Discovery__Enabled": "false", "Changes__Connectors__Enabled": "false",
    }
    command = [DOTNET, str(ROOT / "src/Bizigo.Api/bin/Release/net10.0/Bizigo.Api.dll"),
               "--contentRoot", str(ROOT / "src/Bizigo.Api")]
    def stop_api():
        child = state["api"]
        if child is not None:
            if child.poll() is None:
                os.killpg(child.pid, signal.SIGKILL)
            child.wait(timeout=15)
            state["api_log"].close()
            state["api"] = None
    def start_api(blocked=False):
        stop_api()
        state["blocked"] = blocked
        state["api_log"] = (folder / f"api-{time.time_ns()}.log").open("wb")
        environment = api_env | {"Ingest__Wal__MaxTotalBytes": "1" if blocked else "100000000"}
        child = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=state["api_log"],
                                 stderr=subprocess.STDOUT, start_new_session=True)
        state["api"] = child
        (folder / "api.pid").write_text(str(child.pid))
        access_token = token()
        def ready():
            if child.poll() is not None:
                raise RuntimeError("production API exited; inspect api log")
            status, body, _ = http(f"http://127.0.0.1:{api_port}/internal/ingest/signals",
                                   headers={"Authorization": "Bearer " + access_token})
            return status == 200 and json.loads(body)["ready"]
        wait_for(ready)
    class Proxy(http_server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass
        def do_POST(self):
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                chunks = []
                while True:
                    length = int(self.rfile.readline().split(b";", 1)[0], 16)
                    if length == 0:
                        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                            pass
                        break
                    chunks.append(self.rfile.read(length))
                    assert self.rfile.read(2) == b"\r\n"
                wire = b"".join(chunks)
            else:
                wire = self.rfile.read(int(self.headers["Content-Length"]))
            decoded = gzip.decompress(wire) if self.headers.get("Content-Encoding", "").lower() == "gzip" else wire
            key = uuid.uuid4().hex
            (captures / (key + ".bin")).write_bytes(decoded)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "content-length", "connection", "transfer-encoding")}
            try:
                status, body, returned = http(f"http://127.0.0.1:{api_port}" + self.path, wire, headers)
            except OSError:
                status, body, returned = 503, b"", {"Retry-After": "1"}
            (captures / (key + ".json")).write_text(json.dumps({"path": self.path,
                "content_type": self.headers.get("Content-Type"), "sha256": hashlib.sha256(decoded).hexdigest(),
                "status": status, "payload": key + ".bin", "authorization_present": "Authorization" in self.headers}))
            self.send_response(status)
            for name in ("Content-Type", "Retry-After"):
                if name in returned:
                    self.send_header(name, returned[name])
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
    class Control(http_server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass
        def do_POST(self):
            if self.headers.get("Authorization") != "Bearer " + control_secret:
                self.send_error(403)
                return
            self.rfile.read(int(self.headers.get("Content-Length", "0")))
            try:
                with lock:
                    if self.path == "/stop":
                        stopped.set()
                    elif self.path == "/backpressure":
                        start_api(True)
                    elif self.path == "/restart":
                        start_api(False)
                    elif self.path == "/replay":
                        stop_api()
                        result = subprocess.run(command + ["--replay-signals"], cwd=ROOT, env=api_env,
                                                capture_output=True, timeout=120)
                        (folder / f"replay-{time.time_ns()}.log").write_bytes(result.stdout + result.stderr)
                        assert result.returncode == 0, "offline replay failed"
                        start_api(False)
                    else:
                        self.send_error(404)
                        return
                self.send_response(200); self.end_headers(); self.wfile.write(b'{"ok":true}')
            except Exception as error:
                self.send_error(500, str(error))
    # Proxy is reachable from the owned Collector container. Control stays local.
    proxy = http_server.ThreadingHTTPServer(("0.0.0.0", proxy_port), Proxy)
    try:
        control = http_server.ThreadingHTTPServer(("127.0.0.1", control_port), Control)
    except BaseException:
        proxy.server_close(); logs.close()
        raise
    for server in (proxy, control):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    def shutdown(*_):
        stopped.set()
    signal.signal(signal.SIGTERM, shutdown); signal.signal(signal.SIGINT, shutdown)
    try:
        run(compose + ["up", "-d", "--wait", "--wait-timeout", "600", "postgres", "clickhouse", "rustfs", "keycloak"])
        wait_for(token)
        start_api()
        name = project + "-collector"
        containers.append(name)
        run(["docker", "run", "-d", "--name", name, "--network", project + "_default", "--user", "0:0",
             "--add-host", "host.docker.internal:host-gateway", "-p", f"127.0.0.1:{collector_port}:4318",
             "-v", str(ROOT / "deploy/otel/collector.yaml") + ":/etc/otelcol-contrib/config.yaml:ro",
             "-e", f"BIZIGO_ENDPOINT=http://host.docker.internal:{proxy_port}",
             "-e", "KEYCLOAK_TOKEN_URL=http://keycloak:8080/realms/bizigo/protocol/openid-connect/token",
             "-e", "COLLECTOR_CLIENT_SECRET=bizigo-collector-dev-secret", IMAGE,
             "--config=/etc/otelcol-contrib/config.yaml"])
        wait_for(lambda: http(f"http://127.0.0.1:{collector_port}/v1/metrics", b"{}", {"Content-Type": "application/json"})[0] == 200)
        session = {"schema": 1, "project": project, "pid": os.getpid(), "collector_image": IMAGE,
            "collector": f"http://127.0.0.1:{collector_port}", "api": f"http://127.0.0.1:{api_port}",
            "control": f"http://127.0.0.1:{control_port}", "control_secret": control_secret,
            "token_url": issuer + "/realms/bizigo/protocol/openid-connect/token", "client_id": "bizigo-collector",
            "client_secret": "bizigo-collector-dev-secret", "signals": str(folder / "signals"),
            "logs_wal": str(folder / "logs-wal"), "captures": str(captures),
            "s3": {"url": f"http://127.0.0.1:{ports['S3_PORT']}", "bucket": project,
                   "access": "fixture-access", "secret": "fixture-secret-key"},
            "clickhouse": f"http://127.0.0.1:{ports['CH_HTTP_PORT']}", "ch_user": "bizigo", "ch_password": "fixture-password"}
        path = folder / "session.json"
        path.write_text(json.dumps(session, indent=2)); path.chmod(0o600)
        print(f"READY production API + {IMAGE}: {path}", flush=True)
        while not stopped.wait(1):
            if state["api"] is not None and state["api"].poll() is not None:
                raise RuntimeError("production API exited unexpectedly")
    finally:
        stop_api()
        for name in containers:
            subprocess.run(["docker", "logs", name], stdout=logs, stderr=subprocess.STDOUT, timeout=30)
            subprocess.run(["docker", "rm", "-f", "-v", name], stdout=logs, stderr=subprocess.STDOUT, timeout=60)
        subprocess.run(compose + ["down", "--volumes", "--remove-orphans"], stdout=logs, stderr=subprocess.STDOUT, timeout=120)
        proxy.shutdown(); control.shutdown()
        proxy.server_close(); control.server_close(); logs.close()
        (folder / "cleanup.json").write_text(json.dumps({"finished": True, "project": project}))


if __name__ == "__main__":
    main()
