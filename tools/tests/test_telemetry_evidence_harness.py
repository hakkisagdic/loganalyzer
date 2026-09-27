"""Docker-free session ownership, independent journals and cleanup failure gates."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import uuid
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / filename)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


owner = load("evidence_harness_test", "telemetry-evidence-smoke.py")
probe = load("evidence_probe_test", "telemetry-evidence-probe.py")


class EvidenceHarnessTests(unittest.TestCase):
    def test_optimized_image_override_is_owned_pinned_and_keeps_default_untouched(self):
        original = ["docker", "compose", "-p", "owned", "-f", "deploy/docker-compose.yml"]
        image_id = "sha256:" + "a" * 64
        info = {"Id": image_id, "Architecture": "arm64", "Os": "linux", "Config": {"Labels": {
            "bizigo.fixture": "keycloak-s04", "bizigo.fixture.base": "quay.io/keycloak/keycloak:26.7.1"}}}
        with tempfile.TemporaryDirectory() as folder, patch.dict(owner.os.environ, {}, clear=True), \
             patch.object(owner.subprocess, "check_output", return_value=json.dumps(info)) as inspect:
            extension = owner.EvidenceSession()
            self.assertEqual(extension.configure_compose(original, Path(folder)), original)
            inspect.assert_not_called()
            with patch.dict(owner.os.environ, {"BIZIGO_TEST_KEYCLOAK_IMAGE": "bizigo-test/keycloak-s04:26.7.1"}):
                command = extension.configure_compose(original, Path(folder))
            self.assertEqual(command[:-2], original)
            service = json.loads(Path(command[-1]).read_text())["services"]["keycloak"]
            self.assertEqual(service["image"], image_id); self.assertEqual(service["pull_policy"], "never")
            self.assertEqual(service["command"], ["start", "--optimized", "--import-realm", "--http-enabled=true", "--hostname-strict=false"])
            self.assertEqual(json.loads((Path(folder) / "keycloak-image.json").read_text())["image_id"], image_id)

    def test_optimized_override_rejects_unowned_image_without_docker(self):
        with tempfile.TemporaryDirectory() as folder, \
             patch.dict(owner.os.environ, {"BIZIGO_TEST_KEYCLOAK_IMAGE": "quay.io/keycloak/keycloak:26.7.1"}), \
             patch.object(owner.subprocess, "check_output") as inspect:
            with self.assertRaises(ValueError): owner.EvidenceSession().configure_compose([], Path(folder))
            inspect.assert_not_called()

    def test_two_sequential_nonces_get_distinct_admission_subjects_with_identical_scope(self):
        extension = owner.EvidenceSession(); extension.issuer = "http://fixture.invalid"
        users, memberships, assignments = {}, {}, {}
        core_id, group_id = str(uuid.uuid4()), str(uuid.uuid4())
        role = {"id": str(uuid.uuid4()), "name": "analyst"}

        def respond(request, **kwargs):
            path = request.full_url.split("/admin/realms/bizigo", 1)[1]
            method = request.get_method(); headers = {}; status = 200; payload = None
            if path == "/users?username=analyst.core&exact=true": payload = [{"id": core_id}]
            elif path == "/users/" + core_id + "/groups": payload = [{"id": group_id, "path": "/network/core"}]
            elif path == "/roles/analyst": payload = role
            elif path == "/users" and method == "POST":
                body = json.loads(request.data); user_id = str(uuid.uuid4())
                self.assertNotIn(body["username"], users)
                users[body["username"]] = user_id
                status = 201; headers = {"Location": extension.issuer + "/users/" + user_id}
            elif path.endswith("/role-mappings/realm"):
                self.assertEqual(method, "POST")
                assignments[path.split("/")[2]] = json.loads(request.data); status = 204
            elif "/groups/" in path:
                self.assertEqual(method, "PUT")
                memberships[path.split("/")[2]] = path.rsplit("/", 1)[1]; status = 204
            else: self.fail("unexpected identity request: " + path)
            response = Mock(status=status, headers=headers)
            response.read.return_value = b"" if payload is None else json.dumps(payload).encode()
            response.__enter__ = Mock(return_value=response); response.__exit__ = Mock(return_value=False)
            return response

        with patch.object(owner.base, "http", return_value=(200, b'{"access_token":"fixture"}', {})), \
             patch.object(owner.urllib.request, "urlopen", side_effect=respond):
            first = extension.control("/probe-identity/" + "1" * 32)
            second = extension.control("/probe-identity/" + "2" * 32)
        self.assertNotEqual(first["subject"], second["subject"])
        # Production manual debounce includes the subject even in one clock bucket.
        self.assertNotEqual("manual|" + first["subject"] + "|*|0", "manual|" + second["subject"] + "|*|0")
        for identity in (first, second):
            self.assertEqual(memberships[identity["subject"]], group_id)
            self.assertEqual(assignments[identity["subject"]], [role])
            self.assertEqual(users[identity["username"]], identity["subject"])

    def test_probe_identity_rejects_invalid_nonce_before_network(self):
        with patch.object(owner.base, "http") as request:
            with self.assertRaises(ValueError): owner.EvidenceSession().control("/probe-identity/not-a-uuid")
            request.assert_not_called()

    def test_stop_session_alias_uses_only_existing_control(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "session.json"
            path.write_text(json.dumps({"control": "http://127.0.0.1:1234", "control_secret": "private"}))
            with patch("sys.argv", ["smoke", "--stop", "--session", str(path)]), \
                 patch.object(owner.base, "http", return_value=(200, b"", {})) as request, \
                 patch.object(owner.subprocess, "run") as run:
                owner.main()
            request.assert_called_once(); run.assert_not_called()

    def test_journals_are_independent_and_hash_exact_wire(self):
        with tempfile.TemporaryDirectory() as folder:
            first, second = Path(folder) / "a", Path(folder) / "b"
            first.mkdir(); second.mkdir()
            probe.journal(first, 1, "/v1/metrics", "A", None, 200, b'{"count":"1"}', "V2-H11-01")
            probe.journal(second, 1, "/v1/metrics", "B", None, 200, b'{"count":"0"}', "V2-H11-01")
            a = json.loads((first / "http.jsonl").read_text())
            b = json.loads((second / "http.jsonl").read_text())
            self.assertNotEqual(a["response_sha256"], b["response_sha256"])
            self.assertEqual(a["response_sha256"], hashlib.sha256((first / "http-0001-response.bin").read_bytes()).hexdigest())
            self.assertNotIn("Authorization", (first / "http.jsonl").read_text())
            self.assertEqual(a["caseId"], "V2-H11-01")

    def cleanup(self, directory, execute):
        extension = owner.EvidenceSession(); extension.folder = Path(directory)
        state = {"api": None, "api_log": None}
        with patch.object(owner.subprocess, "run", side_effect=execute):
            extension.cleanup(Path(directory), "owned-123", ["docker", "compose", "-p", "owned-123"],
                              ["owned-123-collector"], state, io.BytesIO())

    def test_cleanup_success_requires_absence_and_owned_commands(self):
        calls = []
        def execute(argv, **kwargs):
            calls.append(argv); self.assertLessEqual(kwargs["timeout"], 28)
            return SimpleNamespace(returncode=0, stdout="")
        with tempfile.TemporaryDirectory() as folder:
            self.cleanup(folder, execute)
            result = json.loads((Path(folder) / "cleanup.json").read_text())
            self.assertTrue(result["finished"])
            self.assertFalse(result["owned_container_alive"])
            self.assertTrue(all(any("owned-123" in word for word in argv) for argv in calls))

    def test_cleanup_timeout_writes_failure_and_still_attempts_down(self):
        calls = []
        def execute(argv, **kwargs):
            calls.append(argv)
            if "rm" in argv: raise subprocess.TimeoutExpired(argv, 1)
            return SimpleNamespace(returncode=0, stdout="")
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(RuntimeError, "owned cleanup failed"): self.cleanup(folder, execute)
            self.assertFalse(json.loads((Path(folder) / "cleanup.json").read_text())["finished"])
            self.assertTrue(any("down" in argv for argv in calls))

    def test_remaining_owned_container_is_not_success(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(RuntimeError, "owned cleanup failed"):
                self.cleanup(folder, lambda *a, **k: SimpleNamespace(returncode=0, stdout="remaining\n"))
            self.assertTrue(json.loads((Path(folder) / "cleanup.json").read_text())["owned_container_alive"])

    def test_invalid_bundle_control_cannot_accept_sql(self):
        with patch.object(owner.subprocess, "check_output") as execute:
            with self.assertRaises(ValueError): owner.EvidenceSession().control("/evidence/not-a-uuid'")
            execute.assert_not_called()

    def test_source_snapshot_detects_changed_file(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); (root / "tools").mkdir()
            source = root / "tools/a.py"; source.write_text("old")
            with patch.object(owner, "ROOT", root):
                before = owner.snapshot(); source.write_text("new"); after = owner.snapshot()
            self.assertNotEqual(before, after)
            self.assertEqual(set(before), {"tools/a.py"})


if __name__ == "__main__":
    unittest.main()
