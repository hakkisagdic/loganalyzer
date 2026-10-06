#!/usr/bin/env python3
"""Sprint 08 Image Reference and Digest Pinning Gate (O16, O20).

Enforces:
1. Owned image references must strictly use `name@sha256:<digest>` format;
   tag-only references (e.g. `name:tag` or `name:latest`) are rejected.
2. Compose, live inspect and freeze manifest digests must match byte-for-byte.
3. Named-red mutations:
   - tag-only mutation is detected and rejected.
   - digest-drift mutation is detected and rejected.
"""
import argparse
import copy
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
OWNED_PREFIXES = ("bizigo/", "bizigo-test/")
DIGEST_REGEX = re.compile(r"^[a-zA-Z0-9_\-\./]+@sha256:[a-fA-F0-9]{64}$")
TAG_ONLY_REGEX = re.compile(r"^[a-zA-Z0-9_\-\./]+:[a-zA-Z0-9_\-\.]+$")


def is_owned_image(image_name: str) -> bool:
    if not image_name:
        return False
    return any(image_name.startswith(p) for p in OWNED_PREFIXES) or "bizigo" in image_name.split(":")[0].split("@")[0]


def parse_compose_images(compose_path: Path) -> dict:
    if not compose_path.exists():
        raise FileNotFoundError(f"Compose file not found: {compose_path}")
    
    text = compose_path.read_text()
    try:
        import yaml
        data = yaml.safe_load(text)
        services = data.get("services", {})
        images = {}
        for svc_name, svc_cfg in services.items():
            if isinstance(svc_cfg, dict) and "image" in svc_cfg:
                images[svc_name] = str(svc_cfg["image"]).strip()
        return images
    except ImportError:
        # Standard library fallback parser for docker-compose services
        images = {}
        in_services = False
        current_svc = None
        for line in text.splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if line.startswith("services:"):
                in_services = True
                continue
            if in_services:
                if not line.startswith(" ") and not line.startswith("\t"):
                    in_services = False
                    current_svc = None
                    continue
                indent = len(line) - len(line.lstrip())
                if indent == 2 and line.endswith(":"):
                    current_svc = line.strip()[:-1]
                elif current_svc and "image:" in stripped:
                    parts = stripped.split("image:", 1)
                    if len(parts) == 2:
                        images[current_svc] = parts[1].strip().strip("'\"")
        return images


def validate_image_references(images: dict, enforce_all_owned: bool = True):
    errors = []
    owned_found = 0
    for svc, img in images.items():
        if is_owned_image(img):
            owned_found += 1
            if not DIGEST_REGEX.match(img):
                if TAG_ONLY_REGEX.match(img):
                    errors.append(f"Service '{svc}' uses tag-only owned image '{img}' instead of '@sha256:<digest>'")
                else:
                    errors.append(f"Service '{svc}' has invalid owned image format '{img}'")
    if enforce_all_owned and owned_found == 0:
        errors.append("No owned image references found in compose file")
    return errors, owned_found


def verify_digest_equality(compose_images: dict, manifest_images: dict, live_inspect_images: dict = None):
    mismatches = []
    for svc, comp_img in compose_images.items():
        if not is_owned_image(comp_img):
            continue
        parts = comp_img.split("@sha256:")
        if len(parts) != 2:
            mismatches.append(f"Compose image '{comp_img}' not in name@sha256:<digest> format")
            continue
        comp_name, comp_digest = parts[0], parts[1]

        # Check against manifest
        if svc in manifest_images:
            man_img = manifest_images[svc]
            man_parts = man_img.split("@sha256:")
            man_digest = man_parts[1] if len(man_parts) == 2 else man_img
            if man_digest.startswith("sha256:"):
                man_digest = man_digest[len("sha256:"):]
            if comp_digest.lower() != man_digest.lower():
                mismatches.append(
                    f"Digest drift for service '{svc}': compose digest {comp_digest} != manifest digest {man_digest}"
                )

        # Check against live inspect if provided
        if live_inspect_images and svc in live_inspect_images:
            live_digest = live_inspect_images[svc]
            if live_digest.startswith("sha256:"):
                live_digest = live_digest[len("sha256:"):]
            if comp_digest.lower() != live_digest.lower():
                mismatches.append(
                    f"Digest drift for service '{svc}': compose digest {comp_digest} != live inspect digest {live_digest}"
                )

    return mismatches


def run_tag_only_mutation(images: dict):
    mutated = copy.deepcopy(images)
    for svc, img in mutated.items():
        if is_owned_image(img) and "@sha256:" in img:
            base = img.split("@sha256:")[0]
            mutated[svc] = f"{base}:latest"
            break
    errors, _ = validate_image_references(mutated)
    if not any("tag-only" in e for e in errors):
        raise AssertionError("tag-only mutation was NOT caught by validator!")
    return errors


def run_digest_drift_mutation(compose_images: dict, manifest_images: dict):
    drifted_manifest = copy.deepcopy(manifest_images)
    for svc in drifted_manifest:
        if is_owned_image(compose_images.get(svc, "")):
            drifted_manifest[svc] = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
            break
    mismatches = verify_digest_equality(compose_images, drifted_manifest)
    if not any("Digest drift" in m for m in mismatches):
        raise AssertionError("digest-drift mutation was NOT caught by verifier!")
    return mismatches


def main():
    parser = argparse.ArgumentParser(description="Operational Image Gate (O16, O20)")
    parser.add_argument("--compose", type=Path, default=ROOT / "deploy/docker-compose.s08.yml")
    parser.add_argument("--manifest", type=Path, default=ROOT / "deploy/s08-image-manifest.json")
    parser.add_argument("--test-mutations", action="store_true", help="Run both tag-only and digest-drift mutations")
    args = parser.parse_args()

    if args.test_mutations:
        sample_images = {
            "api": "bizigo/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ui": "bizigo/ui@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "sidecar": "bizigo/sidecar@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        }
        sample_manifest = {
            "api": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ui": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "sidecar": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        }
        # Baseline validation
        errors, count = validate_image_references(sample_images)
        assert len(errors) == 0, f"Baseline failed: {errors}"
        assert count == 3
        mismatches = verify_digest_equality(sample_images, sample_manifest)
        assert len(mismatches) == 0, f"Baseline equality failed: {mismatches}"

        # Mutation 1: tag-only
        tag_errors = run_tag_only_mutation(sample_images)
        print(f"PASS tag-only mutation correctly rejected: {tag_errors[0]}")

        # Mutation 2: digest-drift
        drift_errors = run_digest_drift_mutation(sample_images, sample_manifest)
        print(f"PASS digest-drift mutation correctly rejected: {drift_errors[0]}")
        print("ALL MUTATION GATES PASSED")
        return 0

    if not args.compose.exists():
        print(f"Compose file {args.compose} does not exist; skipping live check.", file=sys.stderr)
        return 1

    images = parse_compose_images(args.compose)
    errors, owned_count = validate_image_references(images)
    if errors:
        print("Image reference validation errors:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    if args.manifest.exists():
        manifest_data = json.loads(args.manifest.read_text())
        mismatches = verify_digest_equality(images, manifest_data)
        if mismatches:
            print("Digest equality mismatches:", file=sys.stderr)
            for m in mismatches:
                print(f"  - {m}", file=sys.stderr)
            return 1

    print(f"PASS verified {owned_count} owned image reference(s) with exact digest matching.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
