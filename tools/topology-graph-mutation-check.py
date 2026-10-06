#!/usr/bin/env python3
"""Validate/dispatch the accepted 26 topology mutation variants.

--validate-only performs no build, subprocess or mutation. Execution reuses
the isolated-copy runner. Docker-dependent --mode db belongs to Planner.
This catalog validates evidence preparation, not product correctness.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "b9590c5192e324fd3a971a36d5026d967f1fac822ca68a8ea7b51dbca5b523ae"
IDS = {f"M{i:02}" for i in range(1, 26) if i != 20} | {"M20a", "M20b"}
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def verify_restored(trx, required):
    """A green subset is not proof that every promised restore class ran."""
    tree = ET.parse(trx)
    rows = tree.findall(".//t:UnitTestResult", NS)
    definitions = {test.attrib["id"]: test.find("t:TestMethod", NS).attrib["className"]
                   for test in tree.findall(".//t:UnitTest", NS)}
    if not rows or any(row.attrib.get("outcome") != "Passed" for row in rows):
        raise ValueError("restore must have nonzero passed tests with no skip/failure")
    names = [definitions.get(row.attrib.get("testId"), "") + "." + row.attrib["testName"] for row in rows]
    missing = [selector for selector in required if not any(selector in name for name in names)]
    if missing:
        raise ValueError("restore required selector did not execute: " + ",".join(missing))


def validate(plan, root=ROOT, allow_partial=False):
    if plan.get("contractSha256") != CONTRACT:
        raise ValueError("accepted contract hash required")
    entries = plan.get("mutations", [])
    ids = [entry["id"] for entry in entries]
    if not ids or len(set(ids)) != len(ids) or not set(ids) <= IDS:
        raise ValueError("unknown/duplicate/empty mutation IDs; M20a and M20b are distinct")
    if not allow_partial and set(ids) != IDS:
        raise ValueError("missing variants: " + ",".join(sorted(IDS - set(ids))))
    hashes = plan.get("sourceHashes", {})
    for entry in entries:
        project = entry.get("project")
        if project not in ("UnitTests", "IntegrationTests"):
            raise ValueError("explicit UnitTests or IntegrationTests project required")
        selectors = [entry.get("red", ""), entry.get("green", "")]
        if any(not re.fullmatch(r"[\w.]+", selector) for selector in selectors):
            raise ValueError("red and positive must be named test selectors, not filter expressions")
        if selectors[0] in selectors[1] or selectors[1] in selectors[0]:
            raise ValueError("red and positive selectors must be distinct and non-overlapping")
        restore = plan.get("restoreFilters", {}).get(project, [])
        if not restore or any(not re.fullmatch(r"[\w.]+", item) for item in restore):
            raise ValueError("explicit named restore filters required for each project")
        if entry["id"] == "M10" and not entry.get("decisionOracle"):
            raise ValueError("M10 needs a traversal/visited decision oracle beyond a watchdog timeout")
        changes = entry.get("changes", [])
        if not changes:
            raise ValueError("mutation must have concrete source changes")
        content = {}
        for change in changes:
            path = Path(change["path"])
            if path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] not in ("src", "db"):
                raise ValueError("mutations may only edit relative production src/db paths")
            resolved = (root / path).resolve()
            if not resolved.is_relative_to(root.resolve()):
                raise ValueError("mutation symlink escapes checkout")
            raw = resolved.read_bytes()
            if hashes.get(path.as_posix()) != hashlib.sha256(raw).hexdigest():
                raise ValueError("source hash mismatch or missing: " + str(path))
            before, after = change.get("before"), change.get("after")
            if not isinstance(before, str) or not before or not isinstance(after, str) or before == after:
                raise ValueError("nonempty, changing patch anchor required")
            text = content.get(path, raw.decode())
            count = text.count(before)
            occurrence = change.get("occurrence")
            if occurrence is None:
                if count != 1:
                    raise ValueError("ambiguous or stale patch anchor: " + str(path))
                content[path] = text.replace(before, after, 1)
            else:
                if type(occurrence) is not int or occurrence < 0 or occurrence >= count:
                    raise ValueError("patch occurrence out of range")
                position = -1
                for _ in range(occurrence + 1):
                    position = text.index(before, position + (len(before) if position >= 0 else 1))
                content[path] = text[:position] + after + text[position + len(before):]
    return {"status": "PLAN_VALIDATED" if set(ids) == IDS else "PARTIAL_PLAN",
            "variants": sorted(ids), "missing": sorted(IDS - set(ids)), "executed": 0}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--validate-only", action="store_true")
    parser.add_argument("--allow-partial", action="store_true")
    parser.add_argument("--mode", choices=("unit", "db"))
    parser.add_argument("--evidence-dir", type=Path)
    parser.add_argument("--checkout", type=Path, default=ROOT,
                        help="source checkout for validation only; execution uses this tool's checkout")
    args = parser.parse_args(argv)
    if args.allow_partial and not args.validate_only:
        parser.error("partial plans cannot execute the final gate")
    if not args.validate_only and args.checkout.resolve() != ROOT.resolve():
        parser.error("alternate --checkout is validation-only")
    plan = json.loads(args.plan.read_text())
    report = validate(plan, root=args.checkout, allow_partial=args.allow_partial)
    if args.validate_only:
        print(json.dumps(report, indent=2))
        return
    if args.mode is None or args.evidence_dir is None:
        parser.error("execution requires explicit --mode unit|db and --evidence-dir")
    project = "UnitTests" if args.mode == "unit" else "IntegrationTests"
    selected = [item for item in plan["mutations"] if item["project"] == project]
    if not selected:
        parser.error("selected mode contains no variants")
    spec = importlib.util.spec_from_file_location("topology_isolated_runner", ROOT / "tools/telemetry-storage-mutation-gate.py")
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    runner.MUTANTS = [dict(item, name=item["id"], changes=[dict(p, occurrence=p.get("occurrence")) for p in item["changes"]]) for item in selected]
    runner.RESTORE_FILTERS = plan["restoreFilters"]
    original = sys.argv
    try:
        sys.argv = [original[0], "--evidence-dir", str(args.evidence_dir)]
        runner.main()
        # Keep the underlying runner record, then bind its evidence to this
        # catalog and verify every requested restore selector really executed.
        result_path = args.evidence_dir / "result.json"
        raw = result_path.read_bytes()
        (args.evidence_dir / "isolated-runner-result.json").write_bytes(raw)
        result = json.loads(raw)
        try:
            verify_restored(args.evidence_dir / ("restored-" + project) / "result.trx",
                            plan["restoreFilters"][project])
        except Exception:
            result_path.write_text(json.dumps({"status": "FAIL", "reason": "RestoreCoverage",
                                              "runnerRecord": "isolated-runner-result.json"}, indent=2))
            raise
        (args.evidence_dir / "catalog.json").write_text(json.dumps(plan, indent=2))
        result.update(contractSha256=CONTRACT, variants=[item["id"] for item in selected],
                      catalogSha256=hashlib.sha256(args.plan.read_bytes()).hexdigest(),
                      requiredRestoreVerified=True)
        result_path.write_text(json.dumps(result, indent=2))
    finally:
        sys.argv = original


if __name__ == "__main__":
    main()
