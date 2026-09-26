#!/usr/bin/env python3
"""Actual Kestrel child crashes against production endpoint/WAL/archive/replay.

No Docker. Only the explicit fixture executable supplies fault gates and a
filesystem object-store adapter. The Collector harness covers real API startup.
"""
import argparse
import base64
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import struct
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zlib

REPO = Path(__file__).resolve().parents[1]
DLL = REPO / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"
DOTNET = str(Path.home() / ".dotnet/dotnet") if (Path.home() / ".dotnet/dotnet").exists() else "dotnet"
PAYLOAD = b''' {"resourceMetrics":[{"resource":{"attributes":[{"key":"owner_group","value":{"stringValue":"admin"}}]},"scopeMetrics":[{"metrics":[{"name":"durable","gauge":{"dataPoints":[{"asInt":"42"}]}},{"name":"","gauge":{"dataPoints":[{"asInt":"99"}]}}]}]}]} '''


def wait_for(predicate, seconds=30):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.03)
    raise AssertionError("condition did not become true")


def request(port, path, data=None):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data,
                                 headers={"Authorization": "Bearer fixture-token", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=40) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


class Child:
    def __init__(self, root, gate="none"):
        self.root = root
        with socket.socket() as bound:
            bound.bind(("127.0.0.1", 0))
            self.port = bound.getsockname()[1]
        self.log = (root / f"child-{time.time_ns()}.log").open("wb")
        self.process = subprocess.Popen([DOTNET, str(DLL), str(root), str(self.port), gate],
                                        cwd=REPO, stdout=self.log, stderr=subprocess.STDOUT,
                                        start_new_session=True)
        (root / "child.pid").write_text(str(self.process.pid))

    def ready(self, expect_ready=True):
        def poll():
            assert self.process.poll() is None, f"fixture exited: {self.root}"
            try:
                status, body = request(self.port, "/internal/ingest/signals")
                report = json.loads(body)
                return report if status == 200 and bool(report["ready"]) == expect_ready else None
            except (OSError, ValueError):
                return None
        return wait_for(poll)

    def stop(self):
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL)
        self.process.wait(timeout=10)
        self.log.close()


def envelopes(root):
    result = []
    for path in sorted((root / "wal").glob("wal-*.log")):
        data = path.read_bytes()
        offset = 0
        while offset < len(data):
            assert len(data) - offset >= 12
            magic, length, crc = struct.unpack_from(">III", data, offset)
            body = data[offset + 12:offset + 12 + length]
            assert magic == 0x425A4731 and len(body) == length and zlib.crc32(body) == crc
            result.append(json.loads(body))
            offset += length + 12
    return result


def verify(root, count=1):
    files = wait_for(lambda: list((root / "processed").glob("*.json")))
    assert len(files) == count, files
    outputs = [json.loads(path.read_text()) for path in files]
    leaves = [leaf for output in outputs for leaf in output["leaves"]]
    assert len(leaves) == count
    assert len({leaf["logical_id"] for leaf in leaves}) == count
    assert all(leaf["owner_group"] == "_unassigned" for leaf in leaves)
    assert all(leaf["metric"]["gauge"]["dataPoints"][0]["asInt"] == "42" for leaf in leaves)
    assert all(output["rejected_count"] == 1 for output in outputs)
    raw = envelopes(root)
    assert len(raw) == count
    for envelope in raw:
        assert base64.b64decode(envelope["payload"]) == PAYLOAD
        assert envelope["payload_sha256"] == hashlib.sha256(PAYLOAD).hexdigest()
        assert envelope["accepted_keys"] == ["m/0/0/0/0"]
    return {leaf["logical_id"] for leaf in leaves}


def crash_case(base, gate):
    root = base / gate
    root.mkdir()
    child = Child(root, gate)
    executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)
    try:
        child.ready()
        future = executor.submit(request, child.port, "/v1/metrics", PAYLOAD)
        wait_for(lambda: (root / (gate + ".entered")).exists())
        if gate in ("fsync", "after-wal-before-ack"):
            assert not future.done(), "2xx must not precede the fsync/response gate"
        else:
            status, _ = future.result(timeout=10)
            assert status == 200
        child.stop()
        if gate == "fsync":
            # A pre-fsync process crash may leave OS-cached bytes. Model the
            # permitted incomplete EOF, without claiming these bytes were ACKed.
            wal = list((root / "wal").glob("*.log"))
            assert wal
            data = wal[0].read_bytes()
            if data:
                wal[0].write_bytes(data[:max(1, len(data) // 2)])
        try:
            future.result(timeout=10)
        except (OSError, ConnectionError):
            pass
        child = Child(root)
        child.ready()
        if gate == "fsync":
            assert envelopes(root) == []
            assert not list((root / "processed").glob("*.json"))
        else:
            ids = verify(root)
            assert request(child.port, "/fixture/replay", b"")[0] == 200
            assert request(child.port, "/fixture/replay", b"")[0] == 200
            assert verify(root) == ids
        print(f"PASS crash/restart {gate}", flush=True)
    finally:
        child.stop()
        executor.shutdown(wait=True)


def acknowledged_restart(base):
    root = base / "ack-restart"
    root.mkdir()
    child = Child(root)
    try:
        child.ready()
        for _ in range(3):
            assert request(child.port, "/v1/metrics", PAYLOAD)[0] == 200
        child.stop()
        child = Child(root)
        child.ready()
        before = verify(root, 3)
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            responses = list(pool.map(lambda _: request(child.port, "/fixture/replay", b""), range(2)))
        assert all(status == 200 for status, _ in responses)
        assert verify(root, 3) == before
        print("PASS three observed ACKs survive kill and concurrent replay", flush=True)
    finally:
        child.stop()


def corrupt_middle(base):
    root = base / "middle-corruption"
    root.mkdir()
    # Gate consumer result so three acknowledged frames occupy one segment.
    child = Child(root, "before-rename")
    try:
        child.ready()
        assert request(child.port, "/v1/metrics", PAYLOAD)[0] == 200
        assert request(child.port, "/v1/metrics", PAYLOAD)[0] == 200
        assert request(child.port, "/v1/metrics", PAYLOAD)[0] == 200
        child.stop()
        path = next((root / "wal").glob("*.log"))
        data = path.read_bytes()
        length = struct.unpack_from(">I", data, 4)[0] + 12
        assert len(envelopes(root)) == 3
        corrupted = bytearray(data)
        corrupted[length + 8] ^= 1
        path.write_bytes(corrupted)
        for _ in range(2):
            child = Child(root)
            report = child.ready(expect_ready=False)
            assert report["corrupt_segments"] == 1 and report["failure"]
            assert request(child.port, "/v1/metrics", PAYLOAD)[0] == 503
            child.stop()
            assert path.read_bytes() == corrupted, "quarantine must retain good suffix bytes"
        print("PASS middle CRC corruption remains visible and byte-preserving across restarts", flush=True)
    finally:
        child.stop()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence-dir", type=Path)
    args = parser.parse_args()
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=REPO, check=True)
    assert DLL.exists(), "Build solution / fixture before smoke"
    root = args.evidence_dir or Path(tempfile.mkdtemp(prefix="bizigo-otlp-smoke-"))
    root.mkdir(parents=True, exist_ok=True)
    assert not list(root.iterdir()), "evidence directory must be new"
    stages = ["fsync", "after-wal-before-ack", "archive-before-manifest", "before-rename", "after-rename"]
    for stage in stages:
        crash_case(root, stage)
    acknowledged_restart(root)
    corrupt_middle(root)
    (root / "result.json").write_text(json.dumps({"passed": 7, "failed": 0, "evidence": str(root)}, indent=2))
    print(f"PASS 7 real process durability cases: {root}")


if __name__ == "__main__":
    main()
