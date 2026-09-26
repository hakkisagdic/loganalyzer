"""No Docker: verify fixture setup and owned early-failure cleanup wiring."""
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("otlp_harness", ROOT / "tools/otlp-collector-smoke.py")
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)


class CollectorHarnessTests(unittest.TestCase):
    def test_setup_error_still_closes_servers_and_owned_compose_project(self):
        calls = []
        def run(argv, **kwargs):
            calls.append(argv)
            if "up" in argv:
                raise RuntimeError("injected compose startup failure")
            return SimpleNamespace(returncode=0)
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(harness.subprocess, "run", side_effect=run), patch.object(harness.signal, "signal"), \
                 patch("sys.argv", ["harness", "--serve", "--session-dir", directory]):
                with self.assertRaisesRegex(RuntimeError, "injected compose startup failure"):
                    harness.main()
            cleanup = json.loads((Path(directory) / "cleanup.json").read_text())
            self.assertTrue(cleanup["finished"])
            downs = [args for args in calls if "down" in args]
            self.assertEqual(len(downs), 1)
            self.assertIn(cleanup["project"], downs[0])
            self.assertIn("--volumes", downs[0])
            self.assertFalse((Path(directory) / "session.json").exists())

    def test_stop_calls_only_existing_session_control(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "session.json").write_text(json.dumps({"control": "http://127.0.0.1:1234", "control_secret": "fixture"}))
            with patch.object(harness, "http", return_value=(200, b"", {})) as request, \
                 patch.object(harness.subprocess, "run") as execute, \
                 patch("sys.argv", ["harness", "--stop", "--session-dir", directory]):
                harness.main()
            request.assert_called_once_with("http://127.0.0.1:1234/stop", b"", {"Authorization": "Bearer fixture"})
            execute.assert_not_called()


if __name__ == "__main__":
    unittest.main()
