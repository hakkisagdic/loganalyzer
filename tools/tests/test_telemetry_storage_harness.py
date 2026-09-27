"""Docker-free checks of ownership cleanup and the independent wire oracle."""
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value)
    return value


harness = module("telemetry_harness", "tools/telemetry-storage-smoke.py")
probe = module("telemetry_probe", "tools/telemetry-storage-probe.py")
mutations = module("telemetry_mutations", "tools/telemetry-storage-mutation-gate.py")


class TelemetryHarnessTests(unittest.TestCase):
    def test_startup_failure_closes_extension_and_owned_compose(self):
        extension = Mock()
        calls = []
        def run(argv, **kwargs):
            calls.append(argv)
            if "up" in argv: raise RuntimeError("fixture startup fault")
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as directory, patch.object(harness.base.subprocess, "run", side_effect=run), \
             patch.object(harness.base.signal, "signal"), patch("sys.argv", ["fixture", "--serve", "--session-dir", directory]):
            with self.assertRaisesRegex(RuntimeError, "fixture startup fault"):
                harness.base.main(extension=extension)
            extension.close.assert_called_once()
            self.assertEqual(1, sum("down" in argv for argv in calls))
            self.assertTrue(json.loads((Path(directory) / "cleanup.json").read_text())["finished"])

    def test_stop_never_starts_or_deletes_unrelated_session(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "session.json").write_text(json.dumps({"control": "http://127.0.0.1:1234", "control_secret": "owned"}))
            extension = Mock()
            with patch.object(harness.base, "http", return_value=(200, b"", {})), patch.object(harness.base.subprocess, "run") as run, \
                 patch("sys.argv", ["fixture", "--stop", "--session-dir", directory]):
                harness.base.main(extension=extension)
            run.assert_not_called(); extension.start.assert_not_called(); extension.close.assert_not_called()

    def test_query_child_cleanup_is_bounded_and_targets_exact_owned_group(self):
        owner = harness.QuerySession(); child = Mock(pid=4321); child.poll.return_value = None
        owner.child = child; owner.log = Mock(); log = owner.log
        with patch.object(harness.os, "killpg") as kill:
            owner.close_child()
        kill.assert_called_once_with(4321, harness.signal.SIGKILL)
        child.wait.assert_called_once_with(timeout=15); log.close.assert_called_once(); self.assertIsNone(owner.child)

    def test_oracle_detects_nested_and_exact_integer_loss(self):
        probe.contains({"asInt": 9007199254740993}, {"asInt": "9007199254740993"})
        for actual in ({"asInt": 9007199254740992}, {"asInt": "9007199254740992"}):
            with self.assertRaises(AssertionError): probe.contains(actual, {"asInt": "9007199254740993"})
        with self.assertRaises(AssertionError): probe.contains({"events": []}, {"events": [{"name": "event"}]})

    def test_two_probes_receive_distinct_roots_after_archive_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            owner = harness.QuerySession(); owner.folder = Path(directory)
            owner.root = owner.folder / "initial"; owner.root.mkdir()
            def restarted(_): owner.child = SimpleNamespace(pid=1234)
            with patch.object(owner, "close_child"), patch.object(owner, "restart", side_effect=restarted):
                first = owner.control("/db-crash-arm")
                first_root = Path(first["query_signals"])
                (first_root / "processed").mkdir(); (first_root / "processed/old.json").write_text("{}")
                # Simulate the first probe's archive-only restore changing root.
                owner.root = owner.folder / "restored"; owner.root.mkdir()
                second = owner.control("/db-crash-arm")
            self.assertNotEqual(first["query_signals"], second["query_signals"])
            self.assertFalse((Path(second["query_signals"]) / "processed").exists())
            self.assertEqual(1234, second["query_pid"])
            self.assertTrue((first_root / "processed/old.json").exists())

    def test_mutation_catalog_anchors_are_unique_and_single_csv_are_separate(self):
        names = [m["name"] for m in mutations.MUTANTS]
        self.assertEqual(12, len(names)); self.assertIn("M09-history-single", names); self.assertIn("M09-history-csv", names)
        for mutant in mutations.MUTANTS:
            texts = {}
            for item in mutant["changes"]:
                original = texts.get(item["path"], (ROOT / item["path"]).read_text())
                altered = mutations.change(original, item)
                self.assertNotEqual(original, altered)
                self.assertIn(item["after"], altered)
                texts[item["path"]] = altered


if __name__ == "__main__":
    unittest.main()
