"""Catalog rejection tests only; no product mutation is executed here."""
import copy
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("topology_plan_test", ROOT / "tools/topology-graph-mutation-check.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class TopologyMutationPlanTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "src").mkdir()
        (self.root / "src/fixture.cs").write_text("original statement")
        self.plan = {"contractSha256": gate.CONTRACT,
            "sourceHashes": {"src/fixture.cs": hashlib.sha256(b"original statement").hexdigest()},
            "restoreFilters": {"UnitTests": ["TopologyFixtureTests"]},
            "mutations": [{"id": "M01", "project": "UnitTests", "red": "Identity_negative", "green": "Allowed_positive",
                "changes": [{"path": "src/fixture.cs", "before": "original", "after": "changed"}]}]}

    def test_partial_catalog_cannot_be_called_complete(self):
        with self.assertRaisesRegex(ValueError, "missing variants"):
            gate.validate(self.plan, self.root)
        result = gate.validate(self.plan, self.root, allow_partial=True)
        self.assertEqual(result["status"], "PARTIAL_PLAN")
        self.assertEqual(result["executed"], 0)
        self.assertIn("M20a", result["missing"])
        self.assertIn("M20b", result["missing"])

    def test_complete_26_catalog_is_only_plan_validation_not_a_test_pass(self):
        entry = self.plan["mutations"][0]
        self.plan["mutations"] = [dict(copy.deepcopy(entry), id=identity, decisionOracle="Visited_decision") for identity in sorted(gate.IDS)]
        result = gate.validate(self.plan, self.root)
        self.assertEqual(result["status"], "PLAN_VALIDATED")
        self.assertEqual(len(result["variants"]), 26)
        self.assertEqual(result["executed"], 0)
        self.assertEqual((self.root / "src/fixture.cs").read_text(), "original statement")

    def test_duplicate_ids_and_unsplit_m20_rejected(self):
        for identity in ("M01", "M20"):
            plan = copy.deepcopy(self.plan)
            plan["mutations"].append(dict(plan["mutations"][0], id=identity))
            with self.assertRaisesRegex(ValueError, "mutation IDs"):
                gate.validate(plan, self.root, allow_partial=True)

    def test_source_drift_and_missing_hash_rejected(self):
        for hashes in ({}, {"src/fixture.cs": "0" * 64}):
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                gate.validate(self.plan | {"sourceHashes": hashes}, self.root, allow_partial=True)

    def test_test_source_and_escaping_patch_cannot_weaken_oracle(self):
        for path in ("tests/Oracle.cs", "../src/fixture.cs", "/tmp/source.cs"):
            plan = copy.deepcopy(self.plan); plan["mutations"][0]["changes"][0]["path"] = path
            with self.assertRaisesRegex(ValueError, "production src/db"):
                gate.validate(plan, self.root, allow_partial=True)

    def test_stale_anchor_and_noop_rejected(self):
        for before, after in (("absent", "new"), ("original", "original")):
            plan = copy.deepcopy(self.plan)
            plan["mutations"][0]["changes"][0].update(before=before, after=after)
            with self.assertRaises(ValueError): gate.validate(plan, self.root, allow_partial=True)

    def test_unrelated_positive_and_plain_named_selector_required(self):
        for green in ("Identity_negative", "Identity_negative_extra", "Other|Any", ""):
            plan = copy.deepcopy(self.plan); plan["mutations"][0]["green"] = green
            with self.assertRaises(ValueError): gate.validate(plan, self.root, allow_partial=True)

    def test_visited_timeout_without_decision_oracle_is_insufficient(self):
        self.plan["mutations"][0]["id"] = "M10"
        with self.assertRaisesRegex(ValueError, "decision oracle"):
            gate.validate(self.plan, self.root, allow_partial=True)

    def test_explicit_restore_filters_required(self):
        self.plan["restoreFilters"] = {}
        with self.assertRaisesRegex(ValueError, "restore filters"):
            gate.validate(self.plan, self.root, allow_partial=True)

    def test_green_subset_does_not_cover_missing_restore_class(self):
        trx = self.root / "restored.trx"
        trx.write_text('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <TestDefinitions><UnitTest id="one"><TestMethod className="Tests.PresentClass"/></UnitTest></TestDefinitions>
          <Results><UnitTestResult testId="one" testName="Works" outcome="Passed"/></Results>
        </TestRun>''')
        gate.verify_restored(trx, ["PresentClass"])
        with self.assertRaisesRegex(ValueError, "AbsentClass"):
            gate.verify_restored(trx, ["PresentClass", "AbsentClass"])

    def test_skipped_restore_is_not_green(self):
        trx = self.root / "restored.trx"
        trx.write_text('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testName="Works" outcome="NotExecuted"/></Results>
        </TestRun>''')
        with self.assertRaisesRegex(ValueError, "no skip/failure"):
            gate.verify_restored(trx, ["RequiredClass"])

    def test_other_checkout_cannot_accidentally_execute_current_tree(self):
        with patch("sys.stderr", io.StringIO()), self.assertRaises(SystemExit):
            gate.main(["--plan", "unread-plan.json", "--checkout", str(self.root),
                       "--mode", "unit", "--evidence-dir", str(self.root / "evidence")])
        self.assertFalse((self.root / "evidence").exists())


if __name__ == "__main__":
    unittest.main()
