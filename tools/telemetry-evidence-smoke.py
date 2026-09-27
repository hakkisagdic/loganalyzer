#!/usr/bin/env python3
"""Planner-only real API/Collector/Keycloak session; no alternate query server.

Inherits project-owned process/container/volume cleanup from Collector harness.
Fixture identities use real signed Keycloak tokens and product fleet mappings.
"""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import signal
import sys
import time
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("collector_owner", ROOT / "tools/otlp-collector-smoke.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)


class EvidenceSession:
    def configure_compose(self, compose, folder):
        reference = os.environ.get("BIZIGO_TEST_KEYCLOAK_IMAGE")
        if not reference:
            return compose
        if not reference.startswith("bizigo-test/keycloak-s04:"):
            raise ValueError("only an explicitly owned Keycloak fixture image is allowed")
        info = json.loads(subprocess.check_output(
            ["docker", "image", "inspect", reference, "--format", "{{json .}}"], text=True, timeout=15))
        if info.get("Config", {}).get("Labels", {}).get("bizigo.fixture") != "keycloak-s04":
            raise ValueError("Keycloak image missing fixture ownership label")
        image_id = info["Id"]
        if not image_id.startswith("sha256:") or len(image_id) != 71:
            raise ValueError("Keycloak immutable image ID missing")
        record = {"requested_tag": reference, "image_id": image_id, "repo_digests": info.get("RepoDigests", []),
                  "architecture": info["Architecture"], "os": info["Os"],
                  "base": info["Config"]["Labels"].get("bizigo.fixture.base"),
                  "cleanup": "session removes owned containers/volumes; Planner owns reusable fixture image"}
        (folder / "keycloak-image.json").write_text(json.dumps(record, indent=2))
        override = folder / "keycloak-optimized.json"
        override.write_text(json.dumps({"services": {"keycloak": {"image": image_id, "pull_policy": "never",
            "command": ["start", "--optimized", "--import-realm", "--http-enabled=true", "--hostname-strict=false"]}}}, indent=2))
        return [*compose, "-f", str(override)]

    def decorate(self, session, compose, state):
        self.compose = compose
        ids = subprocess.check_output(compose + ["ps", "-q"], text=True, timeout=5).split()
        self.container_ids = ids + [session["project"] + "-collector"]
        manifest = snapshot()
        manifest_path = self.folder / "tested-source-binary.json"
        manifest_path.write_text(json.dumps(manifest, indent=2))
        session.update(version=1, ready=True, productionApiUrl=session["api"], collectorUrl=session["collector"],
                       testIssuerUrl=session["token_url"].split("/protocol/")[0],
                       credentialRefs={"readers": "session.json#evidence_users", "client": "session.json#evidence_client"},
                       fixture={"window_seconds": 10, "sample_count": 5, "baseline": 10, "event": 20,
                                "source_ids": "per-probe nonce", "trace_ids": "per-probe UUID"},
                       ownedProcesses=[os.getpid(), state["api"].pid], ownedContainers=self.container_ids,
                       manifestPath=str(manifest_path))

    def start(self, environment, folder, allocate_port):
        self.folder = folder
        authority = environment["Auth__Authority"]
        issuer = authority.removesuffix("/realms/bizigo")
        self.issuer = issuer
        request = urllib.parse.urlencode({"grant_type": "password", "client_id": "admin-cli",
                                         "username": "admin", "password": "fixture-admin"}).encode()
        status, body, _ = base.http(issuer + "/realms/master/protocol/openid-connect/token", request,
                                   {"Content-Type": "application/x-www-form-urlencoded"})
        assert status == 200, "fixture Keycloak administrator login failed"
        admin_token = json.loads(body)["access_token"]
        self.client = "s04-probe-" + uuid.uuid4().hex
        self.secret = uuid.uuid4().hex
        client = {"clientId": self.client, "enabled": True, "protocol": "openid-connect", "publicClient": False,
                  "secret": self.secret, "directAccessGrantsEnabled": True, "standardFlowEnabled": False,
                  "defaultClientScopes": ["bizigo-claims"]}
        status, _, _ = base.http(issuer + "/admin/realms/bizigo/clients", json.dumps(client).encode(),
                                {"Authorization": "Bearer " + admin_token, "Content-Type": "application/json"})
        assert status == 201, "fixture token client creation failed"
        with (folder / "fleet-apply.log").open("wb") as log:
            subprocess.run([base.DOTNET, str(ROOT / "src/Bizigo.Cli/bin/Release/net10.0/bizigo.dll"),
                "fleet", "apply", str(ROOT / "catalog/simulators")], cwd=ROOT,
                env=environment | {"BIZIGO_CONTROLPLANE": environment["ConnectionStrings__ControlPlane"]},
                stdout=log, stderr=subprocess.STDOUT, check=True, timeout=60)

    def session(self):
        return {"evidence_client": {"id": self.client, "secret": self.secret},
                "evidence_users": {"A": ["analyst.core", "analyst"], "B": ["analyst.edge", "analyst"],
                                   "admin": ["admin.bizigo", "admin"]},
                "evidence_owners": {"A": "network/core", "B": "network/edge"},
                "requires_initial_api_restart": True}

    def control(self, path):
        if path.startswith("/probe-identity/"):
            return self.probe_identity(uuid.UUID(path.removeprefix("/probe-identity/")).hex)
        if path.startswith("/evidence/"):
            bundle = str(uuid.UUID(path.removeprefix("/evidence/")))
            # Fixed, read-only SQL over this session's DB; no SQL accepted from clients.
            query = "SELECT json_build_object('bundle',(SELECT payload FROM evidence_bundles WHERE id='" + bundle + "')," \
                "'reviews',(SELECT coalesce(json_agg(r),'[]'::json) FROM golden_reviews r WHERE bundle_id='" + bundle + "')," \
                "'audit',(SELECT coalesce(json_agg(a),'[]'::json) FROM audit_log a WHERE action LIKE 'telemetry.%'));"
            raw = subprocess.check_output(self.compose + ["exec", "-T", "postgres", "psql", "-U", "bizigo", "-d", "bizigo", "-At", "-c", query], timeout=15)
            return {"persisted": json.loads(raw)}
        return False

    def probe_identity(self, nonce):
        # Each independent probe gets a real Keycloak subject with the same
        # analyst role/core group. Changing data windows or Idempotency-Key
        # alone cannot isolate production debounce (subject + scope + clock bucket).
        nonce = uuid.UUID(nonce).hex
        credentials = urllib.parse.urlencode({"grant_type": "password", "client_id": "admin-cli",
            "username": "admin", "password": "fixture-admin"}).encode()
        status, raw, _ = base.http(self.issuer + "/realms/master/protocol/openid-connect/token", credentials,
            {"Content-Type": "application/x-www-form-urlencoded"})
        assert status == 200, "fixture identity administrator login failed"
        token = json.loads(raw)["access_token"]
        endpoint = self.issuer + "/admin/realms/bizigo"

        def call(path, body=None, method=None):
            request = urllib.request.Request(endpoint + path,
                data=None if body is None else json.dumps(body).encode(), method=method,
                headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = response.read()
                return response.status, json.loads(payload) if payload else None, dict(response.headers)

        _, matches, _ = call("/users?username=analyst.core&exact=true")
        assert len(matches) == 1, "fixture core analyst missing"
        _, groups, _ = call("/users/" + matches[0]["id"] + "/groups")
        assert len(groups) == 1 and groups[0]["path"] == "/network/core", "fixture scope changed"
        _, role, _ = call("/roles/analyst")
        username, password = "s04-probe-" + nonce, uuid.uuid4().hex
        status, _, headers = call("/users", {"username": username, "enabled": True,
            "emailVerified": True, "firstName": "Probe", "lastName": "Fixture",
            "email": username + "@example.local",
            "credentials": [{"type": "password", "value": password, "temporary": False}]})
        assert status == 201, "fresh probe identity required"
        location = next(value for key, value in headers.items() if key.lower() == "location")
        identity = str(uuid.UUID(location.rsplit("/", 1)[1]))
        assert call("/users/" + identity + "/role-mappings/realm", [role])[0] == 204
        assert call("/users/" + identity + "/groups/" + groups[0]["id"], {}, "PUT")[0] == 204
        # Returned only over the private owned control channel; never journal passwords.
        return {"username": username, "password": password, "subject": identity, "nonce": nonce}

    def cleanup(self, folder, project, compose, containers, state, logs):
        started = time.monotonic(); deadline = started + 28
        errors = []
        child = state["api"]
        def run(argv):
            return subprocess.run(argv, stdout=logs, stderr=subprocess.STDOUT,
                                  timeout=max(.1, deadline - time.monotonic()), check=True)
        try:
            if child is not None:
                if child.poll() is None: os.killpg(child.pid, signal.SIGKILL)
                child.wait(timeout=min(3, max(.1, deadline - time.monotonic())))
                state["api_log"].close()
        except Exception as error:
            errors.append(type(error).__name__ + ": " + str(error))
        # Each command is restricted to the uniquely named owned project/container.
        for argv in (["docker", "rm", "-f", "-v", *containers] if containers else [],
                     compose + ["down", "--timeout", "1", "--volumes", "--remove-orphans"]):
            if not argv: continue
            try: run(argv)
            except Exception as error: errors.append(type(error).__name__ + ": " + str(error))
        alive = None
        try:
            result = subprocess.run(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=" + project],
                capture_output=True, text=True, check=True, timeout=max(.1, deadline - time.monotonic()))
            alive = bool(result.stdout.strip())
            for name in containers:
                result = subprocess.run(["docker", "ps", "-aq", "--filter", "name=^/" + name + "$"],
                    capture_output=True, text=True, check=True, timeout=max(.1, deadline - time.monotonic()))
                alive = alive or bool(result.stdout.strip())
        except Exception as error: errors.append(type(error).__name__ + ": " + str(error))
        pid_alive = child is not None and child.poll() is None
        elapsed = time.monotonic() - started
        record = {"finished": not errors and alive is False and not pid_alive and elapsed < 30,
                  "project": project, "owned_pid_alive": pid_alive, "owned_container_alive": alive,
                  "elapsed_seconds": elapsed, "errors": errors, "claim_release": "Planner outer trap"}
        (folder / "cleanup.json").write_text(json.dumps(record, indent=2))
        self.close()
        if not record["finished"]: raise RuntimeError("owned cleanup failed: " + json.dumps(record))

    def close(self):
        if hasattr(self, "folder"):
            (self.folder / "evidence-cleanup.json").write_text(json.dumps({"finished": True, "extra_processes": 0}))


def snapshot():
    import hashlib
    result = {}
    for root in ("src", "sim", "deploy", "tools"):
        for path in sorted((ROOT / root).rglob("*")):
            if not path.is_file() or any(p in ("obj", "__pycache__", "node_modules") for p in path.parts): continue
            if "bin" in path.parts and "Release" not in path.parts: continue
            if path.name == "Dockerfile" or path.suffix in (".cs", ".csproj", ".json", ".yaml", ".yml", ".sql", ".py", ".dll"):
                result[str(path.relative_to(ROOT))] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def main():
    if "--session" in sys.argv:
        index = sys.argv.index("--session")
        path = Path(sys.argv[index + 1])
        assert path.name == "session.json"
        sys.argv[index:index + 2] = ["--session-dir", str(path.parent)]
    # Applied before production startup; explicit scope-bound numeric rule.
    for key, value in {"StableId": "s04-explicit", "Version": "7", "MetricName": "s04.metric", "Unit": "ms",
                       "OwnerGroups__0": "network/core", "Threshold": "20", "Operator": "gte"}.items():
        os.environ["MetricEvidence__ThresholdRules__0__" + key] = value
    base.main(extension=EvidenceSession())


if __name__ == "__main__":
    main()
