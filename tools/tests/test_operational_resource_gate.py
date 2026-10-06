"""Unit tests for Operational Resource Gate (O12)."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_resource_gate", ROOT / "tools/operational_resource_gate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class OperationalResourceGateTests(unittest.TestCase):
    def test_forced_negative_produces_resource_blocked(self):
        with tempfile.NamedTemporaryFile("w+", suffix=".json") as f:
            allowed, receipt = gate.run_gate(forced_negative=True, output_receipt=Path(f.name))
            self.assertFalse(allowed)
            self.assertEqual("RESOURCE_BLOCKED", receipt["status"])
            self.assertIn("machine has", receipt["detail"])

    def test_normal_gate_evaluates(self):
        with tempfile.NamedTemporaryFile("w+", suffix=".json") as f:
            allowed, receipt = gate.run_gate(forced_negative=False, output_receipt=Path(f.name))
            self.assertTrue(allowed)
            self.assertEqual("RESOURCE_ALLOWED", receipt["status"])


if __name__ == '__main__':
    unittest.main()
