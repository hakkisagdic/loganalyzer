"""Unit tests for Operational Evidence Manifest Tool (O13, O27)."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_manifest", ROOT / "tools/operational-manifest.py")
manifest_tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest_tool)


class OperationalManifestTests(unittest.TestCase):
    def test_compute_and_verify_self_manifest(self):
        manifest = manifest_tool.compute_manifest()
        self.assertGreater(manifest["entry_count"], 50)
        self.assertIn("ui/openapi/loganalyzer-api.json", manifest["files"])
        self.assertIn(".github/workflows/ci.yml", manifest["files"])
        errs = manifest_tool.verify_manifest(manifest)
        self.assertEqual(0, len(errs))

    def test_stale_mutation_detections(self):
        manifest = manifest_tool.compute_manifest()
        manifest_tool.run_mutation_tests(manifest)

    def test_gitignored_content_is_not_part_of_the_manifest(self):
        # tools/obsidian-wiki is a gitignored local clone. A filesystem walk
        # hashed it, so a manifest generated where it existed could never
        # verify on a clean CI checkout.
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
            (repo / ".gitignore").write_text("tools/obsidian-wiki/\n")
            for name in ("tools/kept.py", "tools/obsidian-wiki/ignored.json", "src/App/New.cs"):
                (repo / name).parent.mkdir(parents=True, exist_ok=True)
                (repo / name).write_text(name)
            subprocess.run(["git", "add", ".gitignore", "tools/kept.py"], cwd=repo, check=True)
            with mock.patch.object(manifest_tool, "ROOT", repo):
                files = manifest_tool.compute_manifest()["files"]
        self.assertIn("tools/kept.py", files)
        self.assertIn("src/App/New.cs", files)  # untracked, not ignored: about to be committed
        self.assertNotIn("tools/obsidian-wiki/ignored.json", files)


if __name__ == '__main__':
    unittest.main()
