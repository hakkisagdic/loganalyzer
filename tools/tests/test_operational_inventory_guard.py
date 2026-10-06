"""Unit tests for Docker Inventory and Isolation Guard (O02, O17, O21)."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("operational_inventory_guard", ROOT / "tools/operational-inventory-guard.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class OperationalInventoryGuardTests(unittest.TestCase):
    def setUp(self):
        self.before = {
            "containers": {
                "c1": {"name": "foreign-app-1", "image": "nginx:latest", "labels": "canary=true"},
                "c2": {"name": "foreign-db", "image": "postgres:15", "labels": ""}
            },
            "networks": {
                "n1": {"name": "bridge", "driver": "bridge"},
                "n2": {"name": "foreign-net", "driver": "bridge"}
            },
            "volumes": {
                "v1": {"driver": "local"}
            }
        }

    def test_identical_inventory_passes(self):
        diffs, dangling = guard.verify_isolation(self.before, self.before)
        self.assertEqual(0, len(diffs))
        self.assertEqual(0, len(dangling))

    def test_deleted_foreign_container_detected(self):
        after = {
            "containers": {
                "c1": {"name": "foreign-app-1", "image": "nginx:latest", "labels": "canary=true"}
                # c2 deleted!
            },
            "networks": self.before["networks"],
            "volumes": self.before["volumes"]
        }
        diffs, dangling = guard.verify_isolation(self.before, after)
        self.assertEqual(1, len(diffs))
        self.assertIn("Foreign container deleted or missing: foreign-db", diffs[0])

    def test_modified_foreign_container_detected(self):
        after = {
            "containers": {
                "c1": {"name": "foreign-app-1", "image": "nginx:modified", "labels": "canary=true"},
                "c2": {"name": "foreign-db", "image": "postgres:15", "labels": ""}
            },
            "networks": self.before["networks"],
            "volumes": self.before["volumes"]
        }
        diffs, dangling = guard.verify_isolation(self.before, after)
        self.assertEqual(1, len(diffs))
        self.assertIn("Foreign container modified", diffs[0])

    def test_owned_resource_dangling_detected(self):
        after = {
            "containers": {
                "c1": {"name": "foreign-app-1", "image": "nginx:latest", "labels": "canary=true"},
                "c2": {"name": "foreign-db", "image": "postgres:15", "labels": ""},
                "c3": {"name": "bizigo-s08-api", "image": "bizigo/api@sha256:...", "labels": ""}
            },
            "networks": self.before["networks"],
            "volumes": self.before["volumes"]
        }
        diffs, dangling = guard.verify_isolation(self.before, after)
        self.assertEqual(0, len(diffs))
        self.assertEqual(1, len(dangling))
        self.assertIn("Owned container dangling", dangling[0])


if __name__ == '__main__':
    unittest.main()
