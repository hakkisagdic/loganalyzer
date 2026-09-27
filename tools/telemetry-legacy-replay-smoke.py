#!/usr/bin/env python3
"""Real child kill/restart at every legacy object/manifest upgrade boundary; no Docker."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
DOTNET = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
DLL = ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence-dir", type=Path, required=True)
    out = parser.parse_args().evidence_dir.resolve()
    out.mkdir(parents=True, exist_ok=True)
    assert not list(out.iterdir()), "new evidence directory required"
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    results = []
    cases = [(stage, "legacy", "legacy-host", "legacy") for stage in
             ("object-write", "archive-before-manifest", "archive-manifest-before-rename", "archive-manifest-after-rename")]
    cases += [("none", first, second, expected) for first, second, expected in
              (("", "legacy-host", "legacy-host"), ("   ", "legacy-host", "legacy-host"),
               ("", "", "_unknown"), ("   ", "   ", "_unknown"), ("legacy", "legacy-host", "legacy"))]
    for index, (stage, first, second, expected) in enumerate(cases):
        case = out / f"case-{index:02d}"; case.mkdir()
        runs = []
        def run(mode, *args):
            child = subprocess.Popen([DOTNET, str(DLL), "--legacy-replay", mode, str(case), *args],
                                     cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                stdout, stderr = child.communicate(timeout=30)
            finally:
                if child.poll() is None: child.kill()
                child.wait(timeout=15)
            runs.append({"mode": mode, "args": args, "pid": child.pid, "exit": child.returncode, "stdout": stdout, "stderr": stderr})
            (case / "processes.json").write_text(json.dumps(runs, indent=2))
            return child.returncode, stdout
        assert run("seed", first, second)[0] == 0
        manifest_path = next((case / "state/manifests").glob("*.json"))
        before_bytes = manifest_path.read_bytes(); before = json.loads(before_bytes)
        original_path = case / "objects" / before["object_key"]
        original = original_path.read_bytes()
        if stage != "none":
            code, stdout = run("replay", stage)
            assert code != 0 and "KILL pid=" in stdout, "owned child must actually die at checkpoint"
            assert original_path.read_bytes() == original, "UPGRADE OVERWROTE ORIGINAL OBJECT"
            if stage != "archive-manifest-after-rename": assert manifest_path.read_bytes() == before_bytes
            current = json.loads(manifest_path.read_text())
            assert hashlib.sha256((case / "objects" / current["object_key"]).read_bytes()).hexdigest() == current["object_sha256"]
        code, stdout = run("replay")
        assert code == 0, "supported restart replay failed"
        result = json.loads(stdout.strip().splitlines()[-1]); owner, = result["owners"]
        assert result["version"] == 2 and result["payload_sha256"] == before["payload_sha256"]
        assert owner["source_id"] == expected and owner["owner_group"] == "_unassigned"
        assert owner["reason"] == "legacy-owner-unknown"
        assert run("replay")[0] == 0
        assert len(list((case / "state/processed").glob("*.json"))) == 1
        results.append({"stage": stage, "first": first, "second": second, "source": expected, "status": "PASS"})
        print(f"PASS legacy {index}: {stage}, source={expected}", flush=True)
    (out / "result.json").write_text(json.dumps({"cases": results, "status": "PASS"}, indent=2))
    print(f"PASS {len(results)} legacy upgrade and candidate process cases", flush=True)


if __name__ == "__main__":
    main()
