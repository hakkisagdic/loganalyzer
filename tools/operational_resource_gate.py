#!/usr/bin/env python3
"""Operational Resource Gate (O12).

Enforces:
1. Resource claim/check runs before heavy runs.
2. Normal check verifies adequate free memory and disk.
3. Forced RED gate negative proves that insufficient resources halts execution,
   does NOT run any heavy tests, and produces a `RESOURCE_BLOCKED` receipt.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools/machine-resources.sh"


def check_resources(min_free_gb=None, min_free_pct=None):
    env = os.environ.copy()
    if min_free_gb is not None:
        env["MIN_FREE_GB"] = str(min_free_gb)
    if min_free_pct is not None:
        env["MIN_FREE_PCT"] = str(min_free_pct)

    res = subprocess.run(["bash", str(SCRIPT), "check"], capture_output=True, text=True, env=env)
    return res.returncode == 0, res.stdout, res.stderr


def run_gate(forced_negative=False, output_receipt=Path("/tmp/operational-resource-receipt.json")):
    receipt = {
        "forced_negative": forced_negative,
        "status": "UNKNOWN",
        "detail": ""
    }

    if forced_negative:
        # Force refusal by demanding 99999 GB disk
        passed, out, err = check_resources(min_free_gb=99999)
        if passed:
            raise AssertionError("Forced negative resource check did not fail as expected!")
        receipt["status"] = "RESOURCE_BLOCKED"
        receipt["detail"] = err.strip()
        output_receipt.parent.mkdir(parents=True, exist_ok=True)
        output_receipt.write_text(json.dumps(receipt, indent=2))
        return False, receipt

    passed, out, err = check_resources()
    if not passed:
        receipt["status"] = "RESOURCE_BLOCKED"
        receipt["detail"] = err.strip()
        output_receipt.parent.mkdir(parents=True, exist_ok=True)
        output_receipt.write_text(json.dumps(receipt, indent=2))
        return False, receipt

    receipt["status"] = "RESOURCE_ALLOWED"
    receipt["detail"] = "All system resources within operational safety thresholds"
    output_receipt.parent.mkdir(parents=True, exist_ok=True)
    output_receipt.write_text(json.dumps(receipt, indent=2))
    return True, receipt


def main():
    parser = argparse.ArgumentParser(description="Operational Resource Gate (O12)")
    parser.add_argument("--forced-negative", action="store_true", help="Simulate resource shortage and verify RESOURCE_BLOCKED")
    parser.add_argument("--output", type=Path, default=Path("/tmp/operational-resource-receipt.json"))
    args = parser.parse_args()

    allowed, receipt = run_gate(forced_negative=args.forced_negative, output_receipt=args.output)
    if args.forced_negative:
        if receipt["status"] == "RESOURCE_BLOCKED":
            print(f"PASS forced negative correctly produced RESOURCE_BLOCKED: {receipt['detail']}")
            return 0
        else:
            print("FAIL forced negative did not produce RESOURCE_BLOCKED", file=sys.stderr)
            return 1

    if not allowed:
        print(f"RESOURCE_BLOCKED: {receipt['detail']}", file=sys.stderr)
        return 2

    print(f"PASS resource gate: {receipt['status']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
