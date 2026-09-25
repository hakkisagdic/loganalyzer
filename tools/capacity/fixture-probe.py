#!/usr/bin/env python3
"""Synthetic probe for process contract tests ONLY. No hardware/product capacity evidence."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

root, mode = Path(sys.argv[1]), sys.argv[2]
request = json.load(sys.stdin)
attempt, phase = request["Attempt"], request["Phase"]
run = attempt["RunId"]
if request.get("GeneratorPid") is not None:
    try:
        os.kill(request["GeneratorPid"], 0)
        with (root / "observed-live-generator").open("a") as file:
            file.write(f'{request["GeneratorPid"]}\n')
    except ProcessLookupError:
        pass
with (root / "probe-pids").open("a") as file:
    file.write(f"{os.getpid()}\n")
if mode == "nonzero":
    sys.exit(9)
if mode == "malformed":
    print("not JSON")
    sys.exit(0)
if mode == "timeout" and phase != "baseline":
    descendant = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(90)"])
    with (root / "probe-pids").open("a") as file:
        file.write(f"{descendant.pid}\n")
    time.sleep(90)

sample = {
    "RunId": run, "Target": attempt["Options"]["Target"],
    "MonotonicSeconds": time.monotonic(),
    "CapturedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "WireDrops": 100, "CpuPercent": 99 if mode == "breaker" and phase != "baseline" else 1,
    "RecvQueue": 0,
}
if mode == "stale":
    sample["CapturedAt"] = "2000-01-01T00:00:00Z"
if mode == "unavailable":
    sample["WireDrops"] = None
    sample["UnavailableReason"] = "fixture unavailable"
observation = None
if phase == "final":
    manifest = json.loads(Path(request["ManifestPath"]).read_text())
    identities = []
    for line in (root / "received").read_bytes().splitlines():
        match = re.search(rb'bizigo_run_id="([^"]+)" bizigo_seq=(\d+)', line)
        if match:
            identities.append({"RunId": match[1].decode(), "Sequence": int(match[2]),
                               "Digest": hashlib.sha256(line).hexdigest()})
    current = [identity for identity in identities if identity["RunId"] == run]
    expected = manifest["Expected"]
    count = len(current)
    def reading(layer, value):
        return {"Layer": layer, "Source": "SYNTHETIC-loopback-fixture", "Value": value, "LimitReason": None}
    ledger = {"RunId": run, "Expected": expected,
              "WireDrops": reading("Wire", 0), "CollectorAccepted": reading("Collector", expected),
              "CollectorRefused": reading("Collector", 0), "ProductArchived": reading("Product", count),
              "ProductSearchable": reading("Product", count)}
    observation = {"RunId": run, "Target": attempt["Options"]["Target"], "Ledger": ledger,
                   "Archived": identities, "Searchable": identities, "LatencyMilliseconds": 1}
print(json.dumps({"Sample": sample, "Observation": observation}))
