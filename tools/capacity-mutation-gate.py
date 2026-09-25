#!/usr/bin/env python3
"""Execute capacity mutations in an isolated source copy, never in the working tree.

--legacy uses the complete existing B01/B02 mutation catalogs. Each mutation must
compile, fail every designated negative test and pass its positive controls.
TRX is required: a crashed runner, compiler failure or zero tests is NOT a kill.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
DOTNET = shutil.which("dotnet") or "dotnet"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
DEFAULT_GREEN = "Missing_or_empty_sequence_evidence_is_unknown"

def execute(argv, cwd, env=None):
    result = subprocess.run(argv, cwd=cwd, text=True, capture_output=True, env=env, timeout=600)
    if result.returncode:
        print(result.stdout[-6000:] + result.stderr[-2000:], flush=True)
    return result

def resources():
    result = execute(["bash", str(ROOT / "tools/machine-resources.sh"), "check"], ROOT)
    if result.returncode:
        raise RuntimeError("Resource gate refused mutation run")

def catalog(path):
    sys.path.insert(0, str(ROOT / "tools"))
    spec = importlib.util.spec_from_file_location(path.stem.replace("-", "_"), path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return [(m.ad, m.dosya, m.bul, m.koy, m.kirmizi_bekleniyor, m.yesil_kalmali or [DEFAULT_GREEN])
            for m in module.KUSURLAR]

def tests(copy, names, output, env):
    output.mkdir(parents=True, exist_ok=True)
    trx = output / "result.trx"
    if trx.exists():
        trx.unlink()
    result = execute([DOTNET, "test", "tests/Bizigo.UnitTests", "--no-build", "--configuration", "Release",
        "--filter", "|".join("FullyQualifiedName~" + name for name in names),
        "--logger", "trx;LogFileName=result.trx", "--results-directory", str(output), "-m:1"], copy, env)
    if not trx.exists():
        raise AssertionError("No TRX produced; runner failure is not mutation evidence")
    tree = ET.parse(trx)
    counters = tree.find(".//t:Counters", NS)
    assert counters is not None and int(counters.attrib["executed"]) > 0, "No executed tests"
    outcomes = [(r.attrib["testName"], r.attrib["outcome"]) for r in tree.findall(".//t:UnitTestResult", NS)]
    assert outcomes
    return result.returncode, outcomes

def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--legacy", action="store_true")
    mode.add_argument("--descendants", action="store_true",
                      help="Require real-PID regression to kill parent-only cleanup mutation")
    args = parser.parse_args()
    mutations = [
        ("three repeats -> one", "sim/Bizigo.Capacity/CapacityDiscovery.cs",
         "public const int RequiredPasses = 3;", "public const int RequiredPasses = 1;",
         ["Search_is_deterministic_and_requires_three_consecutive_passes"], [DEFAULT_GREEN]),
        ("remove attempt suffix", "sim/Bizigo.Capacity/CapacityDiscovery.cs",
         '$"{discoveryId}-{ordinal:D6}"', 'discoveryId',
         ["Late_events_with_same_ordinals_cannot_fill_next_attempt_gaps"], [DEFAULT_GREEN]),
        ("publish aborted capacity", "sim/Bizigo.Capacity/CapacityDiscovery.cs",
         "Verdict == CapacityVerdict.Pass ? VerifiedLowerEps : null",
         "Verdict is CapacityVerdict.Pass or CapacityVerdict.Aborted ? VerifiedLowerEps : null",
         ["Limited_or_aborted_after_passes_never_publishes_capacity"], [DEFAULT_GREEN]),
    ]
    if args.legacy:
        mutations = catalog(ROOT / "tools/b01-kirmizi-olcumu.py") + catalog(ROOT / "tools/b02-kirmizi-olcumu.py")
    assert mutations
    files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
    resources()
    with tempfile.TemporaryDirectory(prefix="capacity-mutations-") as temporary:
        copy = Path(temporary) / "repo"
        copy.mkdir()
        for name in dict.fromkeys(files):
            source = ROOT / name
            if name and source.is_file():
                target = copy / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        # Read-only git history for pre-existing repository tests; no git mutation is invoked.
        env = os.environ | {"GIT_DIR": str(ROOT / ".git"), "GIT_WORK_TREE": str(copy)}
        build_command = [DOTNET, "build", "tests/Bizigo.UnitTests", "--configuration", "Release", "-m:1"]
        build = execute(build_command, copy, env)
        assert build.returncode == 0, "Baseline must compile"
        if args.descendants:
            path = copy / "sim/Bizigo.Capacity/CapacityProcessOwnership.cs"
            original = path.read_text()
            assert original.count("kill(-id, 9)") == 1
            cases = ("generator-nonzero", "probe-nonzero")
            def regressions(mutated):
                for case in cases:
                    result = execute([sys.executable, "tools/capacity-descendant-smoke.py", "--case", case], copy, env)
                    if mutated:
                        assert result.returncode != 0 and "DESCENDANT LEAK " + case in result.stderr, \
                            "Parent-only mutation must fail on a live descendant PID, not a harness error"
                        print("KILLED parent-only cleanup: " + case + " (live PID verified)", flush=True)
                    else:
                        assert result.returncode == 0 and "PASS 1 descendant lifecycle regressions" in result.stdout
                        print("PASS owned cleanup: " + case, flush=True)
            regressions(False)
            try:
                path.write_text(original.replace("kill(-id, 9)", "kill(id, 9)", 1))
                resources()
                assert execute(build_command, copy, env).returncode == 0, "Cleanup mutant must compile"
                regressions(True)
            finally:
                path.write_text(original)
            resources()
            assert execute(build_command, copy, env).returncode == 0
            regressions(False)
            print("PASS: parent-only cleanup mutant killed for generator and probe; restored regressions green; source tree untouched")
            return
        all_names = list(dict.fromkeys(n for m in mutations for n in m[4] + m[5]))
        code, outcomes = tests(copy, all_names, Path(temporary) / "baseline", env)
        assert code == 0 and all(outcome == "Passed" for _, outcome in outcomes), "Baseline must pass"
        for number, (label, file, before, after, red, green) in enumerate(mutations, 1):
            path = copy / file
            original = path.read_text()
            assert original.count(before) == 1, f"Stale or ambiguous mutation anchor: {label}"
            try:
                path.write_text(original.replace(before, after, 1))
                assert after in path.read_text(), "Mutation was not applied"
                resources()
                assert execute(build_command, copy, env).returncode == 0, f"Mutant must compile: {label}"
                code, outcomes = tests(copy, red + green, Path(temporary) / str(number), env)
                if red:
                    assert code != 0, f"Surviving mutant: {label}"
                else:
                    # Some legacy catalog entries deliberately inject an ignored
                    # non-ticket row: their contract is a positive-only control.
                    assert code == 0, f"Positive-only catalog control failed: {label}"
                for name in red:
                    matching = [outcome for test, outcome in outcomes if name in test]
                    assert matching and "Failed" in matching, f"Required negative did not fail: {name}"
                for name in green:
                    matching = [outcome for test, outcome in outcomes if name in test]
                    assert matching and all(outcome == "Passed" for outcome in matching), f"Positive control failed: {name}"
                outcome = "KILLED" if red else "CONTROL PASSED"
                print(f"{outcome} {number}/{len(mutations)}: {label} (TRX verified)", flush=True)
            finally:
                path.write_text(original)
        resources()
        assert execute(build_command, copy, env).returncode == 0
        code, outcomes = tests(copy, ["Capacity", "Generator", "Arrival"], Path(temporary) / "restored", env)
        assert code == 0 and all(outcome == "Passed" for _, outcome in outcomes)
    killed = sum(bool(m[4]) for m in mutations)
    print(f"PASS: {killed} mutants killed, {len(mutations) - killed} positive-only controls passed; restored capacity suite green; source tree untouched")

if __name__ == "__main__":
    main()
