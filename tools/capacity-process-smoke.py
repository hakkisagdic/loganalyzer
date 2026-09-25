#!/usr/bin/env python3
"""Bounded TCP/UDP, breaker/timeout/teardown and atomic-writer process fixtures.

Synthetic acceptance evidence only; never a hardware capacity benchmark.
"""
from contextlib import contextmanager
import copy
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parent.parent
DOTNET = shutil.which("dotnet") or "dotnet"
CLI = ROOT / "src/Bizigo.Cli/bin/Release/net10.0/bizigo.dll"
HOST_PROJECT = ROOT / "tools/capacity/fixture-host"
HOST = HOST_PROJECT / "bin/Release/net10.0/CapacityFixtureHost.dll"

def command(args, **kwargs):
    result = subprocess.run(args, text=True, capture_output=True, timeout=60, cwd=ROOT, **kwargs)
    return result

def alive(pid):
    result = command(["ps", "-o", "stat=", "-p", str(pid)])
    return result.returncode == 0 and result.stdout.strip() and not result.stdout.strip().startswith("Z")

def wait_until(predicate, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.03)
    raise AssertionError("Timed out waiting for fixture condition")

@contextmanager
def receiver(root, transport, close_after=None):
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM if transport == "tcp" else socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(.1)
    if transport == "tcp":
        sock.listen(8)
    stop = threading.Event()
    (root / "received").write_bytes(b"")
    errors = []
    def run():
        try:
            with (root / "received").open("ab", buffering=0) as output:
                while not stop.is_set():
                    try:
                        if transport == "udp":
                            output.write(sock.recv(65535))
                            continue
                        client, _ = sock.accept()
                    except socket.timeout:
                        continue
                    with client:
                        client.settimeout(.1)
                        pending, count = b"", 0
                        closed = False
                        while not stop.is_set():
                            try:
                                chunk = client.recv(65535)
                            except socket.timeout:
                                continue
                            if not chunk:
                                break
                            pending += chunk
                            while b"\n" in pending:
                                line, pending = pending.split(b"\n", 1)
                                count += 1
                                if close_after is None or count <= close_after:
                                    output.write(line + b"\n")
                                if close_after is not None and count == close_after:
                                    # Leave the tail in the receive buffer while the sender
                                    # finishes, then close without draining it. Sending was
                                    # attained, but the unread tail was not received by the app.
                                    time.sleep(1.05)
                                    client.close()
                                    closed = True
                                    break
                            if closed:
                                break
        except Exception as error:
            errors.append(error)
    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    try:
        yield sock.getsockname()[1]
    finally:
        stop.set()
        thread.join(timeout=3)
        sock.close()
        assert not errors, errors

def config(root, port, transport="tcp", mode="healthy"):
    return {
        "Discovery": {"Target": "SYNTHETIC-loopback", "MinimumEps": 40, "MaximumEps": 40,
            "DurationSeconds": 1, "ObservationSeconds": .1, "GeneratorTimeoutSeconds": 4,
            "GeneratorLocation": "same", "Safety": {"SampleIntervalSeconds": .1,
                "MaxSampleGapSeconds": 2, "ProbeTimeoutSeconds": .5, "ConsecutiveSamples": 2}},
        "Host": "127.0.0.1", "Port": port, "Transport": transport, "RepositoryRoot": str(ROOT),
        "SimulatorDll": str(ROOT / "sim/Bizigo.Simulators/bin/Release/net10.0/Bizigo.Simulators.dll"),
        "Dotnet": DOTNET, "OutputDirectory": str(root / "runs"),
        "Probe": {"FileName": sys.executable, "Arguments": [str(ROOT / "tools/capacity/fixture-probe.py"), str(root), mode]},
    }

def invoke(root, settings, dry=False, extra=()):
    path = root / "config.json"
    path.write_text(json.dumps(settings))
    return command([DOTNET, str(CLI), "capacity", "auto", "--config", str(path),
                    *(["--dry-run"] if dry else []), *extra])

def records(root):
    return [json.loads(p.read_text()) for p in (root / "runs").glob("*.json") if not p.name.endswith("-discovery.json")]

def verify_children(root):
    pids = [r["GeneratorPid"] for r in records(root) if r["GeneratorPid"]]
    if (root / "probe-pids").exists():
        pids += [int(p) for p in (root / "probe-pids").read_text().splitlines()]
    if (root / "generator-descendant").exists():
        pids.append(int((root / "generator-descendant").read_text()))
    wait_until(lambda: all(not alive(pid) for pid in pids))

def main():
    check = command(["bash", "tools/machine-resources.sh", "check"])
    assert check.returncode == 0, check.stdout + check.stderr
    build = command([DOTNET, "build", str(HOST_PROJECT), "--configuration", "Release", "-m:1"])
    assert build.returncode == 0, build.stdout + build.stderr
    checks = 0
    with tempfile.TemporaryDirectory(prefix="capacity-smoke-") as temporary:
        base = Path(temporary)
        for transport in ("tcp", "udp"):
            root = base / transport
            root.mkdir()
            with receiver(root, transport) as port:
                settings = config(root, port, transport)
                dry = invoke(root, settings, dry=True)
                assert dry.returncode == 0, dry.stderr
                assert json.loads(dry.stdout)["RequiredPasses"] == 3
                assert not (root / "runs").exists() and not (root / "probe-pids").exists()
                result = invoke(root, settings)
                assert result.returncode == 0, result.stdout + result.stderr
                summary = json.loads(result.stdout)
                assert summary["Verdict"] == "PASS" and len(summary["ConfirmingRunIds"]) == 3
                attempts = records(root)
                assert len(attempts) == 3 and len({r["Attempt"]["RunId"] for r in attempts}) == 3
                assert all(r["GeneratorPid"] != os.getpid() and r["CapacityEps"] is None for r in attempts)
                verify_children(root)
            checks += 1
            print(f"PASS real {transport.upper()} external generator + dry-run + three persisted attempts", flush=True)

        for mode, expected in (("breaker", "ABORTED"), ("timeout", "INCONCLUSIVE"),
                               ("nonzero", "INCONCLUSIVE"), ("malformed", "INCONCLUSIVE"),
                               ("stale", "INCONCLUSIVE"), ("unavailable", "INCONCLUSIVE")):
            root = base / mode
            root.mkdir()
            with receiver(root, "tcp") as port:
                settings = config(root, port, mode=mode)
                settings["Discovery"]["DurationSeconds"] = 3
                settings["Discovery"]["GeneratorTimeoutSeconds"] = 5
                started = time.monotonic()
                result = invoke(root, settings)
                summary = json.loads(result.stdout)
                assert summary["Verdict"] == expected and summary["CapacityEps"] is None, result.stdout + result.stderr
                assert result.returncode == (3 if expected == "ABORTED" else 2)
                assert time.monotonic() - started < 5, "teardown exceeded bound"
                attempt = records(root)[0]
                assert attempt["Manifest"] is None and attempt["MissingEvidenceReason"]
                if mode in ("breaker", "timeout"):
                    assert attempt["GeneratorPid"] and attempt["SafetySamples"], "trip must happen during generator lifetime"
                    assert str(attempt["GeneratorPid"]) in (root / "observed-live-generator").read_text().splitlines()
                verify_children(root)
            checks += 1
            print(f"PASS {mode}: verdict, missing evidence and process-tree teardown", flush=True)

        root = base / "tail"
        root.mkdir()
        with receiver(root, "tcp", close_after=12) as port:
            result = invoke(root, config(root, port))
            assert result.returncode == 1, result.stdout + result.stderr
            assert records(root)[0]["Decision"]["Gap"] == "Tail"
        checks += 1
        print("PASS TCP close with unread receive tail produces Tail", flush=True)

        root = base / "cancel"
        root.mkdir()
        with receiver(root, "tcp") as port:
            settings = config(root, port)
            settings["Discovery"]["DurationSeconds"] = 3
            settings["Discovery"]["GeneratorTimeoutSeconds"] = 5
            path = root / "config.json"
            path.write_text(json.dumps(settings))
            result = command([DOTNET, str(HOST), "cancel", str(path), "500"])
            assert result.returncode == 0, result.stdout + result.stderr
            record = json.loads(result.stdout)
            assert record["Decision"]["Verdict"] == "Aborted" and record["CapacityEps"] is None
            assert record["GeneratorPid"] is not None
            verify_children(root)
        checks += 1
        print("PASS caller cancellation persists Aborted and kills generator", flush=True)

        for mode in ("generator-timeout", "generator-nonzero", "bad-manifest", "generator-start"):
            root = base / mode
            root.mkdir()
            settings = config(root, 9)
            settings["Dotnet"] = sys.executable
            settings["SimulatorDll"] = str(ROOT / "tools/capacity/fixture-generator.py")
            settings["Discovery"]["GeneratorTimeoutSeconds"] = 1.5
            if mode == "generator-nonzero":
                (root / "fail-generator").touch()
            if mode == "bad-manifest":
                (root / "bad-manifest").touch()
            if mode == "generator-start":
                settings["Dotnet"] = str(root / "nonexistent-generator")
            result = invoke(root, settings)
            summary = json.loads(result.stdout)
            assert summary["Verdict"] == "INCONCLUSIVE" and summary["CapacityEps"] is None, result.stdout
            assert result.returncode == 2
            assert records(root)[0]["MissingEvidenceReason"]
            verify_children(root)
            checks += 1
            print(f"PASS {mode} persists missing evidence and cleans process tree", flush=True)

        root = base / "unknown-location"
        root.mkdir()
        with receiver(root, "udp") as port:
            settings = config(root, port, "udp")
            settings["Discovery"]["GeneratorLocation"] = "unknown"
            result = invoke(root, settings)
            summary = json.loads(result.stdout)
            assert result.returncode == 2 and summary["Verdict"] == "INCONCLUSIVE" and summary["CapacityEps"] is None
            assert records(root)[0]["Manifest"]["GeneratorOnSameHost"] is None
            verify_children(root)
        checks += 1
        print("PASS unknown generator location never publishes capacity", flush=True)

        root = base / "invalid"
        root.mkdir()
        settings = config(root, 9)
        invalid = [("MinimumEps", 0), ("MaximumEps", -1), ("DurationSeconds", 0),
                   ("DurationSeconds", -1), ("DurationSeconds", "NaN"), ("DurationSeconds", "Infinity"),
                   ("MaxLatencyMilliseconds", 0), ("GeneratorLocation", "elsewhere"),
                   ("MaximumEps", 2147483647)]
        for name, value in invalid:
            changed = copy.deepcopy(settings)
            changed["Discovery"][name] = value
            if name == "MaximumEps" and value > 0:
                changed["Discovery"]["DurationSeconds"] = 2
            for dry in (False, True):
                result = invoke(root, changed, dry)
                assert result.returncode != 0
        for patch in ({"Transport": "sctp"}, {"Port": -1}):
            assert invoke(root, settings | patch).returncode != 0
        for name, value in (("MaxCpuPercent", 0), ("MaxCpuPercent", "NaN"), ("MaxCpuPercent", 101),
                            ("MaxRecvQueue", -1), ("MaxNewWireDrops", -1), ("ConsecutiveSamples", 0),
                            ("SampleIntervalSeconds", 0), ("ProbeTimeoutSeconds", 0), ("MaxSampleGapSeconds", 0)):
            changed = copy.deepcopy(settings)
            changed["Discovery"]["Safety"][name] = value
            for dry in (False, True):
                assert invoke(root, changed, dry).returncode != 0
        assert invoke(root, settings, extra=("--unknown-option",)).returncode != 0
        assert not (root / "runs").exists() and not (root / "probe-pids").exists()
        checks += 1
        print("PASS invalid inputs share dry-run validator with no child/output", flush=True)

        root = base / "atomic"
        root.mkdir()
        first = command([DOTNET, str(HOST), "write", str(root), "existing"])
        assert first.returncode == 0, first.stderr
        old = (root / "existing.json").read_bytes()
        marker = root / "flushed"
        child = subprocess.Popen([DOTNET, str(HOST), "write", str(root), "killed", str(marker)], cwd=ROOT)
        try:
            wait_until(marker.exists)
            child.kill()
            child.wait(timeout=5)
            assert not (root / "killed.json").exists()
            assert (root / "existing.json").read_bytes() == old
            assert Path(marker.read_text()).exists(), "fixture must die after temporary fsync"
            reopened = command([DOTNET, str(HOST), "read", str(root)])
            assert reopened.returncode == 0 and reopened.stdout.strip() == "42"
            assert command([DOTNET, str(HOST), "write", str(root), "complete"]).returncode == 0
            assert command([DOTNET, str(HOST), "read", str(root)]).stdout.splitlines() == ["42", "42"]
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        checks += 1
        print("PASS writer SIGKILL after fsync, prior record preservation and fresh-process reopen", flush=True)
    assert checks >= 12, "No empty smoke success"
    print(f"PASS {checks} process scenarios (SYNTHETIC, no hardware capacity claim)")

if __name__ == "__main__":
    main()
