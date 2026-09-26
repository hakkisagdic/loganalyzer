#!/usr/bin/env python3
"""Reject absent/zero/skipped/error TRX evidence and missing required classes."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("files", nargs="+", type=Path)
parser.add_argument("--require-class", nargs="*", default=[])
args = parser.parse_args()
classes = set()
total = 0
for path in args.files:
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    counter = root.find(".//t:Counters", ns)
    assert counter is not None, f"missing counters: {path}"
    stats = {k: int(v) for k, v in counter.attrib.items()}
    assert stats["total"] == stats["executed"] == stats["passed"] > 0, (path, stats)
    assert all(stats.get(k, 0) == 0 for k in ("failed", "error", "timeout", "aborted", "notExecuted")), (path, stats)
    results = root.findall(".//t:UnitTestResult", ns)
    assert len(results) == stats["total"] and all(r.attrib["outcome"] == "Passed" for r in results)
    executed = {r.attrib["testId"] for r in results}
    for test in root.findall(".//t:UnitTest", ns):
        if test.attrib["id"] in executed:
            method = test.find("t:TestMethod", ns)
            assert method is not None
            classes.add(method.attrib["className"].split(",")[0].split(".")[-1])
    total += stats["total"]
for required in args.require_class:
    assert required in classes, f"required class did not execute: {required}"
print(f"PASS TRX: {total} executed/passed; {len(classes)} classes; zero failures/skips")
