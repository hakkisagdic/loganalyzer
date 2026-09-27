#!/usr/bin/env python3
"""Planner-only owned Collector/API/DB session plus production IScopedQuery adapter.

Reuses the shipped Collector harness and its exact cleanup ownership. No product
query endpoint is introduced by this fixture executable.
"""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("collector_owner", ROOT / "tools/otlp-collector-smoke.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)


class QuerySession:
    def __init__(self):
        self.child = None
        self.log = None
        self.folder = None

    def start(self, environment, folder, allocate_port):
        self.folder = folder
        self.environment = environment
        self.port = allocate_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.tokens = {owner: uuid.uuid4().hex for owner in ("A", "B", "_unassigned", "admin")}
        self.root = folder / "query-signals"
        self.root.mkdir()
        self.restart("none")

    def restart(self, gate):
        self.close_child()
        config = {"port": self.port, "root": str(self.root), "gate": gate,
                  "tokens": {token: owner for owner, token in self.tokens.items()}}
        path = self.folder / "query-config.json"
        path.write_text(json.dumps(config)); path.chmod(0o600)
        self.log = (self.folder / f"query-{time.time_ns()}.log").open("wb")
        self.child = subprocess.Popen([base.DOTNET, str(ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"),
                                      "--telemetry-query", str(path)], cwd=ROOT, env=self.environment,
                                     stdout=self.log, stderr=subprocess.STDOUT, start_new_session=True)
        (self.folder / "query.pid").write_text(str(self.child.pid))
        def ready():
            if self.child.poll() is not None:
                raise RuntimeError("query fixture exited; inspect query log")
            status, body, _ = base.http(self.url + "/fixture/ready", headers={"Authorization": "Bearer " + self.tokens["admin"]})
            return status == 200 and json.loads(body)["ready"]
        base.wait_for(ready)

    def session(self):
        return {"telemetry_query": self.url, "query_tokens": self.tokens,
                "query_signals": str(self.root), "query_pid_file": str(self.folder / "query.pid"),
                "query_subjects": {owner: "fixture-" + owner for owner in self.tokens}}

    def control(self, path):
        if path == "/db-crash-arm":
            # Each independent probe owns fresh WAL/checkpoint state, including
            # when a previous probe restored archives into a different root.
            self.close_child()
            self.root = self.folder / ("query-crash-" + uuid.uuid4().hex[:12])
            self.root.mkdir()
            self.restart("after-telemetry-db-before-checkpoint")
        elif path == "/db-crash-restart":
            old_pid = self.child.pid
            self.restart("none")
            (self.folder / "db-crash.json").write_text(json.dumps({"old_pid": old_pid, "new_pid": self.child.pid,
                "old_process_exited": True, "checkpoint": "after-telemetry-db-before-checkpoint"}))
        elif path == "/restore-query-archive":
            self.close_child()
            restore = self.folder / ("query-restore-" + uuid.uuid4().hex[:8])
            # Exact documented restore set: archived objects remain in owned S3,
            # only their verified manifest metadata goes to the fresh local root.
            shutil.copytree(self.root / "manifests", restore / "manifests")
            self.root = restore
            self.restart("none")
            status, body, _ = base.http(self.url + "/fixture/replay", b"", {"Authorization": "Bearer " + self.tokens["admin"]}, timeout=120)
            assert status == 200, body
            (self.folder / "restore.json").write_text(json.dumps({"root": str(restore), "wal_copied": False, "processed_copied": False, "pid": self.child.pid}))
        else:
            return False
        return {"query_signals": str(self.root), "query_pid": self.child.pid}

    def close_child(self):
        if self.child is not None:
            if self.child.poll() is None:
                os.killpg(self.child.pid, signal.SIGKILL)
            self.child.wait(timeout=15)
            self.child = None
        if self.log is not None:
            self.log.close(); self.log = None

    def close(self):
        self.close_child()
        if self.folder is not None:
            (self.folder / "query-cleanup.json").write_text(json.dumps({"finished": True}))


if __name__ == "__main__":
    base.main(extension=QuerySession())
