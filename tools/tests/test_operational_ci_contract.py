import importlib.util
import os
import unittest
from pathlib import Path

# Load operational_ci_contract dynamically
tools_dir = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location(
    "operational_ci_contract",
    tools_dir / "operational_ci_contract.py"
)
ci_contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci_contract)


SAMPLE_VALID_WORKFLOW = """
name: CI
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5

  operational-e2e:
    name: Operational E2E, Image Gate, Proxy & Freeze Manifest
    runs-on: ubuntu-latest
    timeout-minutes: 45
    steps:
      - uses: actions/checkout@v5
      - name: Build
        run: dotnet build Bizigo.sln -c Release -m:1
      - name: Operational E2E test suite and single-TRX verifier
        run: |
          dotnet test tests/Bizigo.IntegrationTests --logger 'trx;LogFileName=operational-e2e-report.trx'
          python3 tools/otlp-verify-trx.py TestResults/operational-e2e-report.trx --require-class OperationalE2EIntegrationTests
      - name: Operational image gate
        run: python3 tools/operational-image-gate.py --compose deploy/docker-compose.s08.yml --manifest deploy/s08-image-manifest.json
      - name: Operational proxy harness
        run: python3 tools/operational-proxy-harness.py
      - name: Operational inventory guard
        run: python3 tools/operational-inventory-guard.py
      - name: Operational freeze manifest
        run: python3 tools/operational-manifest.py --verify deploy/s08-freeze-manifest.json
"""


class TestOperationalCiContract(unittest.TestCase):
    def test_verify_valid_workflow(self):
        res = ci_contract.verify_ci_contract(SAMPLE_VALID_WORKFLOW)
        self.assertEqual(res["status"], "PASS")
        self.assertEqual(res["job"], "operational-e2e")
        self.assertIn("single-trx-zero-skip", res["gates_verified"])

    def test_mutation_missing_job(self):
        mutated = ci_contract.mutate_workflow(SAMPLE_VALID_WORKFLOW, "missing-job")
        res = ci_contract.verify_ci_contract(mutated)
        self.assertEqual(res["status"], "FAIL")
        self.assertTrue(any("Missing required job" in err for err in res["errors"]))

    def test_mutation_missing_trx_check(self):
        mutated = ci_contract.mutate_workflow(SAMPLE_VALID_WORKFLOW, "missing-trx-check")
        res = ci_contract.verify_ci_contract(mutated)
        self.assertEqual(res["status"], "FAIL")
        self.assertTrue(any("Required single-TRX verifier" in err for err in res["errors"]))

    def test_mutation_swallowed_exit(self):
        mutated = ci_contract.mutate_workflow(SAMPLE_VALID_WORKFLOW, "swallowed-exit")
        res = ci_contract.verify_ci_contract(mutated)
        self.assertEqual(res["status"], "FAIL")
        self.assertTrue(any("Forbidden swallowed exit code" in err for err in res["errors"]))

    def test_mutation_skip_allowed(self):
        mutated = ci_contract.mutate_workflow(SAMPLE_VALID_WORKFLOW, "skip-allowed")
        res = ci_contract.verify_ci_contract(mutated)
        self.assertEqual(res["status"], "FAIL")
        self.assertTrue(any("Required class" in err for err in res["errors"]))


if __name__ == "__main__":
    unittest.main()
