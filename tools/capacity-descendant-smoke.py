#!/usr/bin/env python3
"""E1-P1-01 regression: owned descendants cannot survive their exiting parent.

Exercises real CLI normal/nonzero exit, timeout and SIGINT for both commands.
Every fixture child redirects all inherited stdio, reproducing reparented orphans.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("capacity_smoke", ROOT / "tools/capacity-process-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

def wrapper(root, role, outcome):
    program = root / "parent.py"
    program.write_text("""
import json,subprocess,sys,time
from pathlib import Path
root=Path(ROOT)
request=sys.stdin.read() if ROLE=='probe' else None
child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(90)'],
    stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
with (root/'descendants').open('a') as file:file.write(str(child.pid)+'\\n')
if OUTCOME in ('timeout','cancel'):time.sleep(90)
if OUTCOME=='nonzero':sys.exit(17)
if ROLE=='generator':
    result=subprocess.run([DOTNET,SIMULATOR,*sys.argv[1:]])
else:
    result=subprocess.run([sys.executable,PROBE,str(root),'healthy'],input=request,text=True)
sys.exit(result.returncode)
""".replace("ROOT", repr(str(root))).replace("ROLE", repr(role)).replace("OUTCOME", repr(outcome))
        .replace("DOTNET", repr(smoke.DOTNET))
        .replace("SIMULATOR", repr(str(ROOT / "sim/Bizigo.Simulators/bin/Release/net10.0/Bizigo.Simulators.dll")))
        .replace("PROBE", repr(str(ROOT / "tools/capacity/fixture-probe.py"))))
    return program

def pids(root):
    path = root / "descendants"
    return [int(line) for line in path.read_text().splitlines()] if path.exists() else []

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", choices=[r + "-" + o for r in ("generator", "probe")
                                          for o in ("normal", "nonzero", "timeout", "cancel")])
    args = parser.parse_args()
    check = smoke.command(["bash", "tools/machine-resources.sh", "check"])
    assert check.returncode == 0, check.stdout + check.stderr
    sentinel = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(90)"],
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    completed = 0
    try:
        for role in ("generator", "probe"):
            for outcome in ("normal", "nonzero", "timeout", "cancel"):
                name = role + "-" + outcome
                if args.case and args.case != name:
                    continue
                with tempfile.TemporaryDirectory(prefix="capacity-descendant-") as temporary:
                    root = Path(temporary)
                    cli = None
                    try:
                        with smoke.receiver(root, "tcp") as port:
                            settings = smoke.config(root, port)
                            program = wrapper(root, role, outcome)
                            if role == "generator":
                                settings["Dotnet"], settings["SimulatorDll"] = sys.executable, str(program)
                            else:
                                settings["Probe"] = {"FileName": sys.executable, "Arguments": [str(program)]}
                            if outcome == "timeout":
                                settings["Discovery"]["GeneratorTimeoutSeconds"] = 1.5
                            if outcome == "cancel":
                                settings["Discovery"]["Safety"]["ProbeTimeoutSeconds"] = 5
                                settings["Discovery"]["Safety"]["MaxSampleGapSeconds"] = 5
                                path = root / "config.json"
                                path.write_text(json.dumps(settings))
                                cli = subprocess.Popen([smoke.DOTNET, str(smoke.CLI), "capacity", "auto", "--config", str(path)],
                                    cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True)
                                smoke.wait_until(lambda: bool(pids(root)))
                                before = time.monotonic()
                                cli.send_signal(signal.SIGINT)
                                stdout, stderr = cli.communicate(timeout=5)
                                result = subprocess.CompletedProcess(cli.args, cli.returncode, stdout, stderr)
                            else:
                                before = time.monotonic()
                                result = smoke.invoke(root, settings)
                            summary = json.loads(result.stdout)
                            expected = "PASS" if outcome == "normal" else "ABORTED" if outcome == "cancel" else "INCONCLUSIVE"
                            assert summary["Verdict"] == expected, result.stdout + result.stderr
                            assert result.returncode == {"PASS": 0, "ABORTED": 3, "INCONCLUSIVE": 2}[expected]
                            if outcome != "normal":
                                assert summary["CapacityEps"] is None
                                assert time.monotonic() - before < 5, "Teardown exceeded five-second bound"
                            children = pids(root)
                            assert children, "Regression never launched its descendant"
                            survivors = [pid for pid in children if smoke.alive(pid)]
                            assert not survivors, f"DESCENDANT LEAK {name}: {survivors}"
                            assert smoke.alive(sentinel.pid), "Cleanup killed an unrelated process"
                            smoke.verify_children(root)
                            print(f"PASS {name}: {len(children)} descendant PIDs terminated; unrelated sentinel alive", flush=True)
                            completed += 1
                    finally:
                        if cli is not None and cli.poll() is None:
                            cli.kill()
                            cli.wait(timeout=5)
                        for pid in pids(root):
                            if smoke.alive(pid):
                                os.kill(pid, signal.SIGKILL)
    finally:
        sentinel.kill()
        sentinel.wait(timeout=5)
    assert completed == (1 if args.case else 8)
    print(f"PASS {completed} descendant lifecycle regressions", flush=True)

if __name__ == "__main__":
    main()
