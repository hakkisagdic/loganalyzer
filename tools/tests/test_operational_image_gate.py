"""Unit tests for Sprint 08 Image Reference and Digest Pinning Gate (O16, O20)."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_image_gate", ROOT / "tools/operational-image-gate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class OperationalImageGateTests(unittest.TestCase):
    def setUp(self):
        self.sample_images = {
            "api": "bizigo/api@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "ui": "bizigo/ui@sha256:2222222222222222222222222222222222222222222222222222222222222222",
            "sidecar": "bizigo/sidecar@sha256:3333333333333333333333333333333333333333333333333333333333333333",
            "postgres": "postgres:18-alpine"
        }
        self.sample_manifest = {
            "api": "1111111111111111111111111111111111111111111111111111111111111111",
            "ui": "2222222222222222222222222222222222222222222222222222222222222222",
            "sidecar": "3333333333333333333333333333333333333333333333333333333333333333"
        }

    def test_valid_image_references_pass(self):
        errors, count = gate.validate_image_references(self.sample_images)
        self.assertEqual(0, len(errors))
        self.assertEqual(3, count)

    def test_digest_equality_passes_on_matching_manifest(self):
        mismatches = gate.verify_digest_equality(self.sample_images, self.sample_manifest)
        self.assertEqual(0, len(mismatches))

    def test_tag_only_mutation_rejected(self):
        errors = gate.run_tag_only_mutation(self.sample_images)
        self.assertTrue(any("tag-only" in e for e in errors))

    def test_digest_drift_mutation_rejected(self):
        mismatches = gate.run_digest_drift_mutation(self.sample_images, self.sample_manifest)
        self.assertTrue(any("Digest drift" in m for m in mismatches))

    def test_compose_parsing_with_stdlib_fallback(self):
        compose_content = """
version: '3.8'
services:
  api:
    image: bizigo/api@sha256:1111111111111111111111111111111111111111111111111111111111111111
    ports:
      - "8080:8080"
  postgres:
    image: postgres:18-alpine
"""
        with tempfile.NamedTemporaryFile("w+", suffix=".yml") as f:
            f.write(compose_content)
            f.flush()
            parsed = gate.parse_compose_images(Path(f.name))
            self.assertEqual("bizigo/api@sha256:1111111111111111111111111111111111111111111111111111111111111111", parsed.get("api"))
            self.assertEqual("postgres:18-alpine", parsed.get("postgres"))


if __name__ == '__main__':
    unittest.main()
