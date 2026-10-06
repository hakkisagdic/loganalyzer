"""Unit tests for Operational Evidence Manifest Tool (O13, O27)."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_manifest", ROOT / "tools/operational-manifest.py")
manifest_tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest_tool)


class OperationalManifestTests(unittest.TestCase):
    def test_compute_and_verify_self_manifest(self):
        manifest = manifest_tool.compute_manifest()
        self.assertGreater(manifest["entry_count"], 50)
        self.assertIn("ui/openapi/bizigo-api.json", manifest["files"])
        self.assertIn(".github/workflows/ci.yml", manifest["files"])
        errs = manifest_tool.verify_manifest(manifest)
        self.assertEqual(0, len(errs))

    def test_stale_mutation_detections(self):
        manifest = manifest_tool.compute_manifest()
        manifest_tool.run_mutation_tests(manifest)


if __name__ == '__main__':
    unittest.main()
