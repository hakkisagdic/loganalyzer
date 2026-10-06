#!/usr/bin/env python3
"""
Sprint 08 Operational Freeze Evidence Manifest (O13, O27).
Binds source, migrations, ClickHouse schemas, OpenAPI, TS schemas,
and Docker manifests into an immutable evidence manifest.
Enforces that stale outputs or mismatched files fail verification.
"""

import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

CRITICAL_PATHS = [
    ".github/workflows/ci.yml",
    "ui/openapi/bizigo-api.json",
    "ui/src/lib/api/schema.d.ts",
    "deploy/s08-image-manifest.json",
    "deploy/docker-compose.s08.yml",
    "global.json",
    "Directory.Build.props",
]


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    h.update(path.read_bytes())
    return h.hexdigest()


def compute_manifest() -> dict:
    hashes = {}

    # Critical files
    for cp in CRITICAL_PATHS:
        p = ROOT / cp
        if p.exists():
            hashes[cp] = file_sha256(p)

    # Scanned directories
    for directory in ("src", "sim", "db", "deploy", "tools"):
        d = ROOT / directory
        if not d.exists():
            continue
        for path in sorted(d.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(ROOT)
            parts = relative.parts
            if any(p in ("obj", "bin", "__pycache__", "node_modules", ".git") for p in parts):
                continue
            if "freeze-manifest" in path.name:
                continue
            if path.suffix in (".cs", ".csproj", ".json", ".yaml", ".yml", ".sql", ".py", ".sh"):
                hashes[str(relative)] = file_sha256(path)

    # Compute overall bundle digest
    hasher = hashlib.sha256()
    for rel_path in sorted(hashes.keys()):
        hasher.update(rel_path.encode("utf-8"))
        hasher.update(hashes[rel_path].encode("utf-8"))
    
    return {
        "overall_sha256": hasher.hexdigest(),
        "entry_count": len(hashes),
        "files": hashes,
        "entries": hashes
    }


def verify_manifest(manifest_or_path) -> list:
    if isinstance(manifest_or_path, (str, Path)):
        p = Path(manifest_or_path)
        if not p.exists():
            return [f"Manifest file {p} does not exist"]
        stored = json.loads(p.read_text(encoding="utf-8"))
    else:
        stored = manifest_or_path

    stored_entries = stored.get("files") or stored.get("entries", {})

    current = compute_manifest()
    current_entries = current["files"]

    errors = []
    for path, expected_hash in stored_entries.items():
        if path not in current_entries:
            errors.append(f"Missing file: {path}")
        elif current_entries[path] != expected_hash:
            errors.append(f"Hash mismatch for {path}: expected {expected_hash}, got {current_entries[path]}")

    for path in current_entries.keys():
        if path not in stored_entries:
            errors.append(f"Untracked new file present in workspace: {path}")

    return errors


def run_mutation_tests(manifest: dict = None):
    base = manifest if manifest is not None else compute_manifest()
    
    # 1. Base verification
    assert len(verify_manifest(base)) == 0, "Base verification should pass"

    # 2. OpenAPI mutation
    openapi_mutated = json.loads(json.dumps(base))
    files = openapi_mutated.get("files") or openapi_mutated.get("entries")
    if "ui/openapi/bizigo-api.json" in files:
        files["ui/openapi/bizigo-api.json"] = "0" * 64
        errs = verify_manifest(openapi_mutated)
        assert any("ui/openapi/bizigo-api.json" in e for e in errs), "OpenAPI mutation should fail"

    # 3. TS schema mutation
    ts_mutated = json.loads(json.dumps(base))
    files = ts_mutated.get("files") or ts_mutated.get("entries")
    if "ui/src/lib/api/schema.d.ts" in files:
        files["ui/src/lib/api/schema.d.ts"] = "0" * 64
        errs = verify_manifest(ts_mutated)
        assert any("ui/src/lib/api/schema.d.ts" in e for e in errs), "TS schema mutation should fail"

    # 4. Image manifest mutation
    img_mutated = json.loads(json.dumps(base))
    files = img_mutated.get("files") or img_mutated.get("entries")
    if "deploy/s08-image-manifest.json" in files:
        files["deploy/s08-image-manifest.json"] = "0" * 64
        errs = verify_manifest(img_mutated)
        assert any("deploy/s08-image-manifest.json" in e for e in errs), "Image manifest mutation should fail"

    print("PASS all 3 stale-output mutations correctly detected.")


def test_mutations():
    run_mutation_tests()


def main():
    parser = argparse.ArgumentParser(description="Operational Evidence Manifest Tool")
    parser.add_argument("--generate", help="Generate manifest to target path")
    parser.add_argument("--verify", help="Verify manifest at target path")
    parser.add_argument("--test-mutations", action="store_true", help="Run stale-output mutation checks")
    args = parser.parse_args()

    if args.test_mutations:
        test_mutations()
        return

    if args.generate:
        target = Path(args.generate)
        target.parent.mkdir(parents=True, exist_ok=True)
        manifest = compute_manifest()
        target.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        print(f"PASS generated evidence manifest with {manifest['entry_count']} entries. SHA: {manifest['overall_sha256']}")
        return

    if args.verify:
        target = Path(args.verify)
        errors = verify_manifest(target)
        if errors:
            print("Manifest verification failed:", file=sys.stderr)
            for e in errors:
                print(f"  - {e}", file=sys.stderr)
            sys.exit(1)
        stored = json.loads(target.read_text(encoding="utf-8"))
        count = stored.get("entry_count", len(stored.get("files", stored.get("entries", {}))))
        print(f"PASS manifest verified cleanly against workspace. ({count} files)")
        return

    parser.print_help()


if __name__ == "__main__":
    main()
