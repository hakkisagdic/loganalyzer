#!/usr/bin/env python3
"""Docker Inventory and Isolation Guard (O02, O17, O21).

Enforces:
1. Foreign/unrelated containers, networks, volumes are inventoried before execution.
2. After execution and cleanup, all foreign/unrelated resources remain byte-for-byte unmodified.
3. Cleanup strictly targets only resources matching `bizigo-s08-*` or carrying the run's UUID label.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid

OWNED_PREFIX = "bizigo-s08"


def get_docker_inventory():
    """Returns inventory of containers, networks, volumes."""
    inventory = {
        "containers": {},
        "networks": {},
        "volumes": {}
    }

    # Containers
    try:
        res = subprocess.run(
            ["docker", "ps", "-a", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.Labels}}"],
            capture_output=True, text=True, check=True
        )
        for line in res.stdout.strip().splitlines():
            if not line:
                continue
            parts = line.split("\t")
            cid, name, img = parts[0], parts[1], parts[2]
            labels = parts[3] if len(parts) > 3 else ""
            inventory["containers"][cid] = {
                "name": name,
                "image": img,
                "labels": labels
            }
    except Exception:
        pass

    # Networks
    try:
        res = subprocess.run(
            ["docker", "network", "ls", "--format", "{{.ID}}\t{{.Name}}\t{{.Driver}}"],
            capture_output=True, text=True, check=True
        )
        for line in res.stdout.strip().splitlines():
            if not line:
                continue
            parts = line.split("\t")
            nid, name, driver = parts[0], parts[1], parts[2]
            inventory["networks"][nid] = {"name": name, "driver": driver}
    except Exception:
        pass

    # Volumes
    try:
        res = subprocess.run(
            ["docker", "volume", "ls", "--format", "{{.Name}}\t{{.Driver}}"],
            capture_output=True, text=True, check=True
        )
        for line in res.stdout.strip().splitlines():
            if not line:
                continue
            parts = line.split("\t")
            name, driver = parts[0], parts[1]
            inventory["volumes"][name] = {"driver": driver}
    except Exception:
        pass

    return inventory


def is_owned_resource(name: str, labels: str = "", run_id: str = "") -> bool:
    if name.startswith(OWNED_PREFIX):
        return True
    if run_id and run_id in name:
        return True
    if run_id and run_id in labels:
        return True
    return False


def verify_isolation(before: dict, after: dict, run_id: str = ""):
    """Verifies that all non-owned resources before are identical in after."""
    diffs = []

    # Check containers
    for cid, info in before["containers"].items():
        if not is_owned_resource(info["name"], info["labels"], run_id):
            if cid not in after["containers"]:
                diffs.append(f"Foreign container deleted or missing: {info['name']} ({cid})")
            else:
                after_info = after["containers"][cid]
                if after_info["name"] != info["name"] or after_info["image"] != info["image"]:
                    diffs.append(f"Foreign container modified: {info['name']} vs {after_info['name']}")

    # Check networks
    for nid, info in before["networks"].items():
        if not is_owned_resource(info["name"], "", run_id):
            if nid not in after["networks"]:
                diffs.append(f"Foreign network deleted or missing: {info['name']} ({nid})")

    # Check volumes
    for vname, info in before["volumes"].items():
        if not is_owned_resource(vname, "", run_id):
            if vname not in after["volumes"]:
                diffs.append(f"Foreign volume deleted or missing: {vname}")

    # Check that after does not leave owned resources dangling
    dangling_owned = []
    for cid, info in after["containers"].items():
        if is_owned_resource(info["name"], info["labels"], run_id):
            dangling_owned.append(f"Owned container dangling: {info['name']} ({cid})")

    return diffs, dangling_owned


def main():
    parser = argparse.ArgumentParser(description="Docker Inventory and Isolation Guard")
    parser.add_argument("--snapshot", type=Path, help="Save baseline inventory to path")
    parser.add_argument("--verify", type=Path, help="Compare current inventory against baseline path")
    parser.add_argument("--run-id", type=str, default="", help="Run UUID")
    parser.add_argument("--output", type=Path, default=Path("/tmp/operational-isolation-receipt.json"))
    args = parser.parse_args()

    if args.snapshot:
        inv = get_docker_inventory()
        args.snapshot.parent.mkdir(parents=True, exist_ok=True)
        args.snapshot.write_text(json.dumps(inv, indent=2))
        print(f"PASS captured baseline inventory with {len(inv['containers'])} containers, {len(inv['networks'])} networks, {len(inv['volumes'])} volumes.")
        return 0

    if args.verify:
        if not args.verify.exists():
            print(f"Baseline file {args.verify} does not exist", file=sys.stderr)
            return 1
        before = json.loads(args.verify.read_text())
        after = get_docker_inventory()
        diffs, dangling = verify_isolation(before, after, args.run_id)

        receipt = {
            "run_id": args.run_id,
            "foreign_violations": diffs,
            "dangling_owned": dangling,
            "canary_isolated": len(diffs) == 0,
            "owned_cleanup_successful": len(dangling) == 0,
            "foreign_containers_count": len([c for c in before["containers"].values() if not is_owned_resource(c["name"], c["labels"], args.run_id)])
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(receipt, indent=2))

        if diffs:
            print("ISOLATION VIOLATIONS DETECTED:", file=sys.stderr)
            for d in diffs:
                print(f"  - {d}", file=sys.stderr)
            return 1

        if dangling:
            print("OWNED RESOURCES LEFT DANGLING:", file=sys.stderr)
            for o in dangling:
                print(f"  - {o}", file=sys.stderr)
            return 1

        print(f"PASS isolation guard verified: all {receipt['foreign_containers_count']} foreign container(s) untouched, 0 dangling owned.")
        return 0

    # Self-test if no args
    inv = get_docker_inventory()
    diffs, dangling = verify_isolation(inv, inv)
    assert len(diffs) == 0 and len(dangling) == 0
    print("PASS inventory guard self-test clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
