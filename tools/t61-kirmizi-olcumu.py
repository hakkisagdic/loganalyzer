#!/usr/bin/env python3
"""T61 §6 — the four gates can go red, measured by injection.

Procedure per CLAUDE.md §6:
  write the defect -> READ the file back and ASSERT the defect is there
  -> run -> restore from a backup file (never `git checkout`) -> assert clean
  -> and finally run the whole suite once more, because a restore that never
     reached the build looks exactly like a restore that did.

Backups keep their own name; `shutil.copy` (not copy2) plus an explicit touch,
so MSBuild never mistakes a restored file for an up-to-date one.
"""
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EPIC = os.path.join(ROOT, "docs", "epic")
TESTS = os.path.join(ROOT, "tests", "Bizigo.UnitTests")

ENV = dict(os.environ)
ENV["DOTNET_ROOT"] = os.path.expanduser("~/.dotnet")
ENV["PATH"] = os.path.expanduser("~/.dotnet") + os.pathsep + ENV["PATH"]

GATE_A = "Birlestirilmis_bir_is_baslamamis_gorunmuyor"
GATE_B = "Birlestirilmis_her_kimlik_bir_tabloda_aniliyor"
GATE_C = "Isi_baslamis_bir_story_baslamamis_gorunmuyor"
GATE_E = "Bekci_bos_kume_uzerinde_donmuyor"


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def write(path, text):
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    os.utime(path, (time.time(), time.time()))


def build():
    done = subprocess.run(
        ["dotnet", "build", TESTS, "-v", "q", "--nologo"],
        cwd=ROOT, env=ENV, capture_output=True, text=True)
    if done.returncode != 0:
        print(done.stdout[-3000:])
        raise SystemExit("build failed")


def run(gate):
    done = subprocess.run(
        ["dotnet", "test", TESTS, "--no-build",
         "--filter", f"FullyQualifiedName~EpicStatusTests.{gate}"],
        cwd=ROOT, env=ENV, capture_output=True, text=True)
    text = done.stdout
    total = re.search(r"Toplam:\s*(\d+)", text)
    if not total or int(total.group(1)) == 0:
        print(text[-2000:])
        raise SystemExit(f"{gate}: no test ran — the filter matched nothing")
    return done.returncode == 0


def measure(label, path, mutate, expect_red, expect_green, rebuild=False):
    backup = path + ".t61-backup"
    shutil.copy(path, backup)
    try:
        marker = mutate(read(path))
        write(path, marker[0])

        back = read(path)
        assert marker[1](back), f"{label}: DEFECT NOT IN FILE — the run would be a lie"
        print(f"  {label}: defect verified present in {os.path.relpath(path, ROOT)}")

        if rebuild:
            build()

        red = not run(expect_red)
        green = all(run(gate) for gate in expect_green)
        print(f"  {label}: {expect_red} red={red} | controls green={green}")
        return red and green
    finally:
        shutil.copy(backup, path)
        os.utime(path, (time.time(), time.time()))
        os.remove(backup)
        if rebuild:
            build()


def status_flip(value):
    def mutate(text):
        return (
            re.sub(r"^status:\s*\d+", f"status: {value}", text, count=1, flags=re.M),
            lambda back: re.search(rf"^status:\s*{value}\s*$", back, re.M) is not None,
        )
    return mutate


def drop_t56(text):
    lines = [line for line in text.splitlines(keepends=True)
             if not line.startswith("| T56 |")]
    return "".join(lines), lambda back: "| T56 |" not in back


def blind_merge_reader(text):
    # The git surface collapses: no merge subject parses into an id any more.
    broken = text.replace(
        r'@"^([tsmbTSMB])(\d+)-"',
        r'@"^ASLA-ESLESMEYECEK-([tsmbTSMB])(\d+)-"')
    return broken, lambda back: "ASLA-ESLESMEYECEK" in back


CASES = [
    ("D1 gate A  (M02 status 2 -> 0)",
     os.path.join(EPIC, "tickets-mcp", "komut-cekirdegi", "index.md"),
     status_flip(0), GATE_A, [GATE_B, GATE_C, GATE_E], False),

    ("D2 gate B  (T56 row removed)",
     os.path.join(EPIC, "tickets-f3", "index.md"),
     drop_t56, GATE_B, [GATE_A, GATE_C, GATE_E], False),

    ("D3 gate C  (mcp story 1 -> 0)",
     os.path.join(EPIC, "tickets-mcp", "index.md"),
     status_flip(0), GATE_C, [GATE_A, GATE_B, GATE_E], False),

    ("D4 empty set (merge id regex blinded)",
     os.path.join(TESTS, "EpicStatusTests.cs"),
     blind_merge_reader, GATE_E, [], True),
]


def main():
    build()
    results = []
    for label, path, mutate, red, green, rebuild in CASES:
        print(label)
        results.append((label, measure(label, path, mutate, red, green, rebuild)))

    print("\n=== restore reached the build? full EpicStatusTests run ===")
    ok = run("")  # empty gate name -> whole class
    print(f"whole class green after restore: {ok}")

    print("\n=== summary ===")
    for label, passed in results:
        print(f"  {'OK ' if passed else 'FAIL'} {label}")
    return 0 if all(p for _, p in results) and ok else 1


if __name__ == "__main__":
    sys.exit(main())
