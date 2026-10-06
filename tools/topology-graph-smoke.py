#!/usr/bin/env python3
"""Planner-owned topology live infrastructure, using the real product API.

READY means infrastructure is available, never that topology assertions passed.
The independent probe supplies its own nonce and Keycloak admission subject.
No alternate graph reader or in-memory topology server is installed here.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CONTRACT_SHA256 = "b9590c5192e324fd3a971a36d5026d967f1fac822ca68a8ea7b51dbca5b523ae"
spec = importlib.util.spec_from_file_location("topology_session_parent", ROOT / "tools/telemetry-evidence-smoke.py")
parent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(parent)


def snapshot():
    """Include both stores' schemas, production and fixture sources and binaries."""
    hashes = {}
    for directory in ("src", "sim", "deploy", "db", "tools"):
        for path in sorted((ROOT / directory).rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(ROOT)
            if any(part in ("obj", "__pycache__", "node_modules") for part in relative.parts):
                continue
            if "bin" in relative.parts and "Release" not in relative.parts:
                continue
            if path.name == "Dockerfile" or path.suffix in (
                ".cs", ".csproj", ".json", ".yaml", ".yml", ".sql", ".py", ".dll", ".sh"
            ):
                hashes[str(relative)] = hashlib.sha256(path.read_bytes()).hexdigest()
    return hashes


def require_frozen(session):
    if session.get("topology_contract_sha256") != CONTRACT_SHA256:
        raise ValueError("session is not bound to the accepted topology contract")
    actual = snapshot()
    expected = json.loads(Path(session["manifestPath"]).read_text())
    if actual != expected:
        raise ValueError("topology source/schema/binary changed since session READY")
    return actual


class TopologySession(parent.EvidenceSession):
    # Reuse real Keycloak identity creation, immutable owned image override,
    # production Collector/API lifecycle and bounded owned-project cleanup.
    def decorate(self, session, compose, state):
        super().decorate(session, compose, state)
        manifest = self.folder / "topology-source-binary.json"
        manifest.write_text(json.dumps(snapshot(), indent=2))
        session.update(
            topology_contract_sha256=CONTRACT_SHA256,
            topology_assertions="NOT_RUN",
            manifestPath=str(manifest),
            fixture={"source_ids": "per-probe nonce", "trace_ids": "per-probe UUID",
                     "admission_identity": "fresh real Keycloak subject per probe",
                     "graph_seed": "created through production registry REST"},
        )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--serve", action="store_true")
    mode.add_argument("--stop", action="store_true")
    location = parser.add_mutually_exclusive_group(required=True)
    location.add_argument("--output", type=Path)
    location.add_argument("--session", type=Path)
    args = parser.parse_args(argv)
    if args.serve and args.output is None:
        parser.error("--serve requires --output <new-empty-directory>")
    if args.stop and (args.session is None or args.session.name != "session.json"):
        parser.error("--stop requires --session <owned-session.json>")
    folder = args.output if args.serve else args.session.parent
    original = sys.argv
    try:
        sys.argv = [original[0], "--serve" if args.serve else "--stop", "--session-dir", str(folder)]
        parent.base.main(extension=TopologySession())
    finally:
        sys.argv = original


if __name__ == "__main__":
    main()
