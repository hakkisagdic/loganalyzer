"""Exercise the real shell gate with OS fixtures; no Docker or heavy work starts."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/machine-resources.sh"


class MachineResourcesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.fixture = {
            "os": "Linux", "disk": "8388608", "memory": "MemTotal: 1000 kB\nMemAvailable: 150 kB\n",
            "pressure": "System-wide memory free percentage: 15%\n",
            "swapins": [100, 1600, 3100, 4600, 6100],
        }
        # Only replace measurement commands. Parsing and shell control flow stay real.
        stub = "#!" + sys.executable + "\n" + '''
import json, os, sys
from pathlib import Path
root = Path(os.environ["RESOURCE_FIXTURE"])
f = json.loads((root / "fixture.json").read_text())
name = Path(sys.argv[0]).name
if name == f.get("fail"):
    sys.exit(1)
if name == "uname":
    print(f["os"])
elif name == "df":
    assert sys.argv[1:] == ["-Pk", os.environ["HOME"]], sys.argv
    print("Filesystem 1024-blocks Used Available Capacity Mounted on")
    print("/dev/disk 99999999 1 " + f["disk"] + " 1% /home with spaces")
elif name == "memory_pressure":
    assert f["os"] == "Darwin", "macOS command used on Linux"
    print(f["pressure"])
elif name == "sleep":
    assert sys.argv[1:] == ["1"]
elif name == "dotnet":
    (root / "heavy-work-started").touch()
    sys.exit(90)
elif name == "cat" and sys.argv[1:] == ["/proc/meminfo"]:
    assert f["os"] == "Linux"
    print(f["memory"])
elif name == "vm_stat" or (name == "cat" and sys.argv[1:] == ["/proc/vmstat"]):
    assert (name == "vm_stat") == (f["os"] == "Darwin")
    state = root / "sample"
    index = int(state.read_text()) if state.exists() else 0
    state.write_text(str(index + 1))
    value = f["swapins"][min(index, len(f["swapins"]) - 1)]
    if value is not None:
        print("Swapins: " + str(value) + "." if name == "vm_stat" else "pswpin " + str(value))
else:
    raise AssertionError((name, sys.argv))
if name == f.get("fail_after"):
    sys.exit(1)
'''
        for name in ("uname", "df", "cat", "memory_pressure", "vm_stat", "sleep", "dotnet"):
            path = self.bin / name
            path.write_text(stub)
            path.chmod(0o755)
        self.env = dict(os.environ, HOME=str(self.root), PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        RESOURCE_FIXTURE=str(self.root), MIN_FREE_GB="8", MIN_FREE_PCT="15", MAX_SWAPIN_RATE="1500")

    def run_gate(self, *args, command=None):
        (self.root / "fixture.json").write_text(json.dumps(self.fixture))
        (self.root / "sample").unlink(missing_ok=True)
        return subprocess.run(command or ["bash", str(SCRIPT), "check", *args], env=self.env,
                              cwd=ROOT, text=True, capture_output=True, timeout=15)

    def assert_refused(self, message, *args):
        result = self.run_gate(*args)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn(message, result.stderr)

    def test_linux_and_macos_exact_thresholds_pass(self):
        for platform in ("Linux", "Darwin"):
            with self.subTest(platform=platform):
                self.fixture["os"] = platform
                result = self.run_gate()
                self.assertEqual(0, result.returncode, result.stderr)

    def test_disk_below_floor_refuses_on_both_platforms(self):
        self.fixture["disk"] = str(8 * 1048576 - 1)
        for platform in ("Linux", "Darwin"):
            with self.subTest(platform=platform):
                self.fixture["os"] = platform
                self.assert_refused("7 GiB free disk")

    def test_linux_uses_available_memory_not_free_memory(self):
        self.fixture["memory"] += "MemFree: 0 kB\n"
        self.assertEqual(0, self.run_gate().returncode)
        self.fixture["memory"] = "MemTotal: 1000 kB\nMemAvailable: 149 kB\nMemFree: 900 kB\n"
        self.assert_refused("14% memory free")

    def test_memory_override_and_macos_pressure_still_apply(self):
        for platform in ("Linux", "Darwin"):
            with self.subTest(platform=platform):
                self.fixture["os"] = platform
                self.assert_refused("wanted 16%", "16")

    def test_sustained_paging_refuses_but_single_burst_passes(self):
        for platform in ("Linux", "Darwin"):
            with self.subTest(platform=platform):
                self.fixture["os"] = platform
                self.fixture["swapins"] = [100, 1601, 3102, 4603, 6104]
                self.assert_refused("1501 swap-ins/sec")
                self.fixture["swapins"] = [100, 200, 300, 400, 10400]
                self.assertEqual(0, self.run_gate().returncode)

    def test_failed_measurement_commands_refuse(self):
        for platform, command in (("Linux", "df"), ("Linux", "cat"),
                                  ("Darwin", "memory_pressure"), ("Darwin", "vm_stat")):
            with self.subTest(platform=platform, command=command):
                self.fixture.update(os=platform, fail=command)
                self.assert_refused("cannot measure")

    def test_command_failure_with_valid_output_is_not_masked_by_awk(self):
        for platform, command in (("Linux", "df"), ("Linux", "cat"), ("Darwin", "memory_pressure")):
            with self.subTest(platform=platform, command=command):
                self.fixture.update(os=platform, fail_after=command)
                self.assert_refused("cannot measure")

    def test_empty_or_malformed_disk_refuses(self):
        for value in ("", "unknown", "-1"):
            with self.subTest(value=value):
                self.fixture["disk"] = value
                self.assert_refused("cannot measure free_gb")

    def test_missing_or_invalid_linux_memory_refuses(self):
        for memory in ("", "MemTotal: 1000 kB\n", "MemTotal: 0 kB\nMemAvailable: 100 kB\n",
                       "MemTotal: 1000 kB\nMemAvailable: unknown kB\n",
                       "MemTotal: 1000 kB\nMemAvailable: 1001 kB\n"):
            with self.subTest(memory=memory):
                self.fixture["memory"] = memory
                self.assert_refused("cannot measure free_pct")

    def test_missing_macos_pressure_refuses(self):
        self.fixture.update(os="Darwin", pressure="unavailable")
        self.assert_refused("cannot measure free_pct")

    def test_missing_malformed_or_reset_swap_counter_refuses(self):
        for platform in ("Linux", "Darwin"):
            for samples in ([None], ["unknown"], [100, None], [100, 99]):
                with self.subTest(platform=platform, samples=samples):
                    self.fixture.update(os=platform, swapins=samples)
                    self.assert_refused("cannot measure")

    def test_unsupported_platform_refuses(self):
        self.fixture["os"] = "UnsupportedOS"
        self.assert_refused("cannot measure free_pct")

    def test_capacity_entrypoints_stop_when_linux_memory_is_low(self):
        self.fixture["memory"] = "MemTotal: 1000 kB\nMemAvailable: 10 kB\n"
        for script, args in (("capacity-process-smoke.py", []), ("capacity-descendant-smoke.py", []),
                             ("capacity-mutation-gate.py", []), ("capacity-mutation-gate.py", ["--descendants"]),
                             ("capacity-mutation-gate.py", ["--legacy"])):
            with self.subTest(script=script, args=args):
                result = self.run_gate(command=[sys.executable, str(ROOT / "tools" / script), *args])
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("1% memory free", result.stdout + result.stderr)
                self.assertFalse((self.root / "heavy-work-started").exists())


if __name__ == "__main__":
    unittest.main()
