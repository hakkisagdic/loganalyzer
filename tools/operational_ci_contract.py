#!/usr/bin/env python3
"""
Sprint 08 Operational CI Contract Verifier & Mutation Gate (O10, O26).
Ensures that the operational-e2e job is present in .github/workflows/ci.yml,
binds the single-TRX zero-skip verifier with required classes, enforces
image, proxy, inventory and manifest verification steps, and tests mutations fail-closed.
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path

REQUIRED_JOB = "operational-e2e:"
REQUIRED_TRX_FILE = "operational-e2e-report.trx"
REQUIRED_VERIFIER = "tools/otlp-verify-trx.py"
REQUIRED_CLASS = "OperationalE2EIntegrationTests"
REQUIRED_IMAGE_GATE = "tools/operational-image-gate.py"
REQUIRED_PROXY_HARNESS = "tools/operational-proxy-harness.py"
REQUIRED_INVENTORY_GUARD = "tools/operational-inventory-guard.py"
REQUIRED_MANIFEST = "tools/operational-manifest.py"


def verify_ci_contract(workflow_content: str) -> dict:
    errors = []
    
    if REQUIRED_JOB not in workflow_content:
        errors.append(f"Missing required job '{REQUIRED_JOB}' in workflow")

    # Extract the operational-e2e job block
    job_match = re.search(r"\n  operational-e2e:(.*?)(?=\n  [a-zA-Z0-9_\-]+:|\Z)", workflow_content, re.DOTALL)
    if not job_match:
        errors.append("Unable to extract operational-e2e job section")
        return {"status": "FAIL", "errors": errors}

    job_text = job_match.group(1)

    if "continue-on-error: true" in job_text.lower():
        errors.append("Forbidden 'continue-on-error: true' found in operational-e2e job")

    if re.search(r"\|\s*(true|:)(\s+.*)?$", job_text, re.MULTILINE):
        errors.append("Forbidden swallowed exit code ('|| true' or '|| :') found in operational-e2e job")

    if REQUIRED_TRX_FILE not in job_text:
        errors.append(f"Required TRX log file '{REQUIRED_TRX_FILE}' not referenced in operational-e2e job")

    if REQUIRED_VERIFIER not in job_text:
        errors.append(f"Required single-TRX verifier '{REQUIRED_VERIFIER}' missing in operational-e2e job")

    verifier_line = re.search(r"otlp-verify-trx\.py.*$", job_text, re.MULTILINE)
    if not verifier_line or f"--require-class {REQUIRED_CLASS}" not in verifier_line.group(0):
        errors.append(f"Required class '{REQUIRED_CLASS}' not passed with --require-class in verifier step")

    if REQUIRED_IMAGE_GATE not in job_text:
        errors.append(f"Required image gate '{REQUIRED_IMAGE_GATE}' missing in operational-e2e job")

    if REQUIRED_PROXY_HARNESS not in job_text:
        errors.append(f"Required proxy harness '{REQUIRED_PROXY_HARNESS}' missing in operational-e2e job")

    if REQUIRED_INVENTORY_GUARD not in job_text:
        errors.append(f"Required inventory guard '{REQUIRED_INVENTORY_GUARD}' missing in operational-e2e job")

    if REQUIRED_MANIFEST not in job_text:
        errors.append(f"Required freeze manifest '{REQUIRED_MANIFEST}' missing in operational-e2e job")

    if errors:
        return {"status": "FAIL", "errors": errors}

    return {
        "status": "PASS",
        "job": "operational-e2e",
        "trx_file": REQUIRED_TRX_FILE,
        "required_class": REQUIRED_CLASS,
        "gates_verified": [
            "single-trx-zero-skip",
            "operational-image-gate",
            "operational-proxy-harness",
            "operational-inventory-guard",
            "operational-manifest"
        ]
    }


def mutate_workflow(workflow_content: str, mutation_type: str) -> str:
    if mutation_type == "missing-job":
        return re.sub(r"\n  operational-e2e:.*?(?=\n  [a-zA-Z0-9_\-]+:|\Z)", "", workflow_content, flags=re.DOTALL)
    elif mutation_type == "missing-trx-check":
        return workflow_content.replace(REQUIRED_VERIFIER, "echo skipping-trx-check #")
    elif mutation_type == "swallowed-exit":
        if "--results-directory TestResults" in workflow_content:
            return workflow_content.replace("--results-directory TestResults", "--results-directory TestResults || true")
        return workflow_content.replace("operational-e2e-report.trx'", "operational-e2e-report.trx' || true")
    elif mutation_type == "skip-allowed":
        return workflow_content.replace(f"--require-class {REQUIRED_CLASS}", "")
    else:
        raise ValueError(f"Unknown mutation type: {mutation_type}")


def run_mutation_check(workflow_content: str, mutation_type: str) -> bool:
    mutated = mutate_workflow(workflow_content, mutation_type)
    result = verify_ci_contract(mutated)
    if result["status"] == "FAIL":
        print(f"PASS: Mutation '{mutation_type}' successfully caught with errors: {result['errors']}")
        return True
    else:
        print(f"FAIL: Mutation '{mutation_type}' was NOT caught!")
        return False


def main():
    parser = argparse.ArgumentParser(description="Operational CI Contract Verifier & Mutation Gate")
    parser.add_argument("--workflow", default=".github/workflows/ci.yml", help="Path to ci.yml")
    parser.add_argument("--verify", action="store_true", help="Verify CI contract in workflow")
    parser.add_argument("--mutation", choices=["missing-job", "missing-trx-check", "swallowed-exit", "skip-allowed"],
                        help="Run named mutation test")
    parser.add_argument("--receipt", help="Path to write JSON receipt")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    workflow_path = Path(args.workflow)
    if not workflow_path.is_absolute():
        workflow_path = repo_root / workflow_path

    if not workflow_path.is_file():
        print(f"Workflow file not found: {workflow_path}", file=sys.stderr)
        sys.exit(1)

    with open(workflow_path, "r", encoding="utf-8") as f:
        content = f.read()

    if args.mutation:
        success = run_mutation_check(content, args.mutation)
        sys.exit(0 if success else 1)

    result = verify_ci_contract(content)
    if args.receipt:
        with open(args.receipt, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=2)

    if result["status"] != "PASS":
        print("CI Contract Verification Failed:", file=sys.stderr)
        for err in result["errors"]:
            print(f" - {err}", file=sys.stderr)
        sys.exit(1)

    print("CI Contract Verification PASSED:")
    print(json.dumps(result, indent=2))
    sys.exit(0)


if __name__ == "__main__":
    main()
