"""Docker-free ownership and freeze gates; not live topology evidence."""
import importlib.util
import base64
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("topology_owner_test", ROOT / "tools/topology-graph-smoke.py")
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)
probe_spec = importlib.util.spec_from_file_location("topology_probe_test", ROOT / "tools/topology-graph-probe.py")
probe = importlib.util.module_from_spec(probe_spec)
probe_spec.loader.exec_module(probe)


class TopologyHarnessTests(unittest.TestCase):
    def test_schema_change_addition_and_deletion_each_invalidate_freeze(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "db/clickhouse").mkdir(parents=True)
            sql = root / "db/clickhouse/0008_topology.sql"
            sql.write_text("original schema")
            manifest = root / "manifest.json"
            session = {"topology_contract_sha256": owner.CONTRACT_SHA256, "manifestPath": str(manifest)}
            with patch.object(owner, "ROOT", root):
                manifest.write_text(json.dumps(owner.snapshot()))
                owner.require_frozen(session)
                sql.write_text("changed predicate")
                with self.assertRaisesRegex(ValueError, "changed since"): owner.require_frozen(session)
                sql.write_text("original schema")
                extra = sql.with_name("0009_extra.sql"); extra.write_text("new schema")
                with self.assertRaisesRegex(ValueError, "changed since"): owner.require_frozen(session)
                extra.unlink(); sql.unlink()
                with self.assertRaisesRegex(ValueError, "changed since"): owner.require_frozen(session)

    def test_wrong_contract_cannot_be_used_as_live_evidence(self):
        with patch.object(owner, "snapshot") as read:
            with self.assertRaisesRegex(ValueError, "accepted topology contract"):
                owner.require_frozen({"topology_contract_sha256": "different"})
            read.assert_not_called()

    def test_stop_routes_only_to_selected_session_parent(self):
        with patch.object(owner.parent.base, "main") as run:
            def check(*, extension):
                self.assertIsInstance(extension, owner.TopologySession)
                self.assertEqual(owner.sys.argv[1:], ["--stop", "--session-dir", "/tmp/owned-topology"])
            run.side_effect = check
            original = owner.sys.argv
            owner.main(["--stop", "--session", "/tmp/owned-topology/session.json"])
            self.assertIs(owner.sys.argv, original)
            run.assert_called_once()

    def test_invalid_mode_does_not_start_processes(self):
        with patch.object(owner.parent.base, "main") as run, patch("sys.stderr", io.StringIO()):
            for args in (["--serve", "--session", "/tmp/session.json"],
                         ["--stop", "--output", "/tmp/owned"],
                         ["--stop", "--session", "/tmp/unrelated.json"]):
                with self.assertRaises(SystemExit): owner.main(args)
            run.assert_not_called()

    def cleanup(self, folder, execute):
        extension = owner.TopologySession(); extension.folder = Path(folder)
        with patch.object(owner.parent.subprocess, "run", side_effect=execute):
            extension.cleanup(Path(folder), "otlp-owned-test",
                ["docker", "compose", "-p", "otlp-owned-test"], ["otlp-owned-test-collector"],
                {"api": None, "api_log": None}, io.BytesIO())

    def test_cleanup_is_bounded_and_only_targets_owned_project(self):
        calls = []
        def execute(argv, **kwargs):
            calls.append(argv)
            self.assertLessEqual(kwargs["timeout"], 28)
            self.assertTrue(any("otlp-owned-test" in arg for arg in argv))
            self.assertNotIn("prune", argv)
            return SimpleNamespace(stdout="", returncode=0)
        with tempfile.TemporaryDirectory() as directory:
            self.cleanup(directory, execute)
            record = json.loads((Path(directory) / "cleanup.json").read_text())
            self.assertTrue(record["finished"])
            self.assertFalse(record["owned_pid_alive"])
            self.assertFalse(record["owned_container_alive"])
            self.assertLess(record["elapsed_seconds"], 30)
            self.assertTrue(calls)

    def test_cleanup_failure_still_attempts_down_and_cannot_pass(self):
        calls = []
        def execute(argv, **kwargs):
            calls.append(argv)
            if "rm" in argv: raise subprocess.TimeoutExpired(argv, 1)
            return SimpleNamespace(stdout="", returncode=0)
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "owned cleanup failed"):
                self.cleanup(directory, execute)
            self.assertFalse(json.loads((Path(directory) / "cleanup.json").read_text())["finished"])
            self.assertTrue(any("down" in argv for argv in calls))

    def test_remaining_owned_container_prevents_cleanup_success(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "owned cleanup failed"):
                self.cleanup(directory, lambda *a, **k: SimpleNamespace(stdout="still-alive", returncode=0))
            self.assertFalse(json.loads((Path(directory) / "cleanup.json").read_text())["finished"])


class TopologyProbeTransportTests(unittest.TestCase):
    def test_pagination_rejects_duplicate_drift_hidden_and_looping_cursor(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            first = {"nodes": [{"id": "a"}], "cursor": "opaque-one", "partial": True,
                     "published_sequence": "1"}
            last = {"nodes": [{"id": "b"}], "cursor": None, "partial": False,
                    "published_sequence": "1"}
            with patch.object(instance, "request", side_effect=[first, last]) as call:
                self.assertEqual(instance.read_pages("/v1/topology/nodes", "nodes", as_of="2026-01-01T00:00:00Z",
                    hidden_markers=["hidden"]), [{"id": "a"}, {"id": "b"}])
                self.assertIn("cursor=opaque-one", call.call_args.args[0])
            for broken in (last | {"nodes": [{"id": "a"}]}, last | {"published_sequence": "2"},
                           last | {"nodes": [{"id": "hidden"}]}, last | {"cursor": "opaque-one", "partial": True}):
                with patch.object(instance, "request", side_effect=[first, broken]):
                    with self.assertRaises(AssertionError):
                        instance.read_pages("/v1/topology/nodes", "nodes", as_of="2026-01-01T00:00:00Z",
                                            hidden_markers=["hidden"])

    def test_declared_read_runner_checks_seven_routes_and_exact_twenty_target_proofs(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            plan = probe.fixture_plan(instance.nonce)
            fixture, seq = {}, 1
            for group in ("directed", "hidden_bridge", "fanout"):
                section = plan[group]
                labels = section.get("nodes") or [section["root"], *section["targets"]]
                nodes = {}
                for label in labels:
                    nodes[label] = {"id": "service:" + str(probe.uuid.UUID(int=seq)), "displayName": group+label}
                    seq += 1
                fixture[group] = {"nodes": nodes, "ids": {k:v["id"] for k,v in nodes.items()},
                    "edges": [{"from": a,"to": b,"id": f"{group}-edge-{i}"}
                              for i,(a,b) in enumerate(section["edges"])]}
            anonymous, bad_targets = [], []
            def edge_ids(group, labels):
                lookup={(x["from"],x["to"]):x["id"] for x in fixture[group]["edges"]}
                return [lookup[x] for x in zip(labels,labels[1:])]
            def request(path, **kw):
                split=probe.urllib.parse.urlsplit(path); route=split.path
                query=probe.urllib.parse.parse_qs(split.query)
                if kw.get("principal", "A") is None:
                    self.assertEqual(kw["expected"],(401,)); anonymous.append(route); return None
                if kw.get("principal") == "ingest":
                    self.assertEqual(kw["expected"],(403,)); return None
                if kw.get("expected") in ((400,),(404,)):
                    bad_targets.append((kw["case"],query.get("nodeId"))); return None
                graph=fixture["directed"]; hidden=fixture["hidden_bridge"]
                if route.endswith("/nodes"):
                    values=[{"id":n["id"]} for g,part in fixture.items() for k,n in part["nodes"].items()
                            if not (g=="hidden_bridge" and k=="b")]; field="nodes"
                elif route.endswith("/edges"):
                    values=[{"id":e["id"]} for g in ("directed","fanout") for e in fixture[g]["edges"]]; field="edges"
                else: values=None
                if values is not None:
                    values.sort(key=lambda x:x["id"]); offset=int(query.get("cursor",["0"])[0]); limit=int(query["limit"][0])
                    more=offset+limit<len(values)
                    return {field:values[offset:offset+limit],"cursor":str(offset+limit) if more else None,
                            "partial":more,"published_sequence":"1"}
                if route.endswith("/neighbors"): return {"neighbors":[],"outside_neighbor_count":"1"}
                if "/nodes/" in route: return {"node":{"id":graph["ids"]["r"],"display_name":"directedr"}}
                if "/edges/" in route: return {"edge":{"id":graph["edges"][0]["id"],"provenance":"declared"}}
                if route.endswith("/path"):
                    for oracle in plan["directed"]["paths"]:
                        if query["fromNode"]==[graph["ids"][oracle["from"]]] and query["toNode"]==[graph["ids"][oracle["to"]]]:
                            return {"status":oracle["status"],"nodes":[graph["ids"][x] for x in oracle["nodes"]],
                                    "edge_ids":edge_ids("directed",oracle["nodes"]),"partial":False,"cursor":None}
                    self.assertEqual(query["fromNode"],[hidden["ids"]["r"]])
                    return {"status":"NotVerified","nodes":[],"edge_ids":[],"partial":False,"cursor":None}
                cases=[("directed",o["targets"],o["node"],o["paths"]) for o in plan["directed"]["ancestors"]]
                cases.append(("fanout",plan["fanout"]["targets"],plan["fanout"]["root"],plan["fanout"]["expected_paths"]))
                for group,targets,node,paths in cases:
                    ids=fixture[group]["ids"]
                    if query["nodeId"]==[ids[x] for x in targets]:
                        return {"status":"Found","node_id":ids[node],"partial":False,"paths":[{
                            "target_node_id":ids[p[-1]],"nodes":[ids[x] for x in p],"edge_ids":edge_ids(group,p)} for p in paths]}
                raise AssertionError("unexpected probe request: "+path)
            with patch.object(instance,"request",side_effect=request):
                instance.read_declared_graphs(fixture,as_of="2026-01-01T00:00:00Z")
            self.assertEqual(len(anonymous),7)
            self.assertEqual(len(bad_targets),8)
            self.assertTrue(any(targets and len(targets)==21 for _,targets in bad_targets))
            self.assertEqual(json.loads((instance.output/"declared-read-checkpoint.json").read_text())["status"],"CHECKPOINT_ONLY")
            self.assertFalse((instance.output/"result.json").exists())

    def test_declared_seed_uses_registry_ids_and_same_name_ordinal_pair(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            instance.session["evidence_owners"] = {"A": "owner-a", "B": "owner-b"}
            calls, nodes, edges = [], [], []
            def request(path, **kwargs):
                calls.append((path, kwargs))
                body = kwargs["body"]
                self.assertEqual(kwargs["method"], "POST")
                self.assertEqual(kwargs["principal"], "admin")
                if path.endswith("/nodes"):
                    # Descending IDs force the a/b assignment to ignore creation order.
                    item = body | {"id": "service:" + str(probe.uuid.UUID(int=100-len(nodes))),
                                   "version": "1", "validFromUnixNano": "9007199254740993"}
                    nodes.append(item)
                    return item
                item = body | {"id": "edge-" + str(len(edges)), "version": "1",
                               "directed": True, "provenance": "declared", "confidence": 1}
                edges.append(item)
                return item
            with patch.object(instance, "request", side_effect=request):
                result = instance.seed_declared_graphs()
            self.assertEqual((len(nodes), len(edges), len(calls)), (31, 30, 61))
            graph = result["directed"]
            self.assertLess(graph["ids"]["a"], graph["ids"]["b"])
            self.assertEqual(graph["nodes"]["a"]["displayName"], graph["nodes"]["b"]["displayName"])
            self.assertEqual(result["hidden_bridge"]["nodes"]["b"]["ownerGroup"], "owner-b")
            self.assertEqual([(x["from"], x["to"]) for x in graph["edges"]],
                             [tuple(x) for x in probe.fixture_plan(instance.nonce)["directed"]["edges"]])
            self.assertTrue((instance.output / "declared-fixture.json").exists())
            self.assertFalse((instance.output / "result.json").exists())

    def test_registry_seed_rejects_numeric_version_and_noncanonical_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            instance.session["evidence_owners"] = {"A": "owner-a"}
            valid = {"id": "service:00000000-0000-0000-0000-000000000001", "version": "1",
                     "validFromUnixNano": "9007199254740993"}
            for broken in (valid | {"version": 1}, valid | {"id": "service:display-name"},
                           valid | {"id": "Service:00000000-0000-0000-0000-000000000001"}):
                with patch.object(instance, "request", return_value=broken):
                    with self.assertRaises((AssertionError, ValueError)):
                        instance.create_service("label")

    def test_declared_seed_rejects_endpoint_provenance_and_direction_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            valid = {"id": "edge", "fromNodeId": "a", "toNodeId": "b", "relation": "depends_on",
                     "provenance": "declared", "directed": True, "version": "1", "confidence": 1}
            for broken in (valid | {"fromNodeId": "b"}, valid | {"provenance": "observed"},
                           valid | {"directed": False}):
                with patch.object(instance, "request", return_value=broken):
                    with self.assertRaises(AssertionError): instance.create_declared_edge("a", "b")

    def test_wire_rejects_numeric_bigints_pascal_enums_and_nonfinite_confidence(self):
        for body in ({"version": 9007199254740993}, {"event_time_unix_nano": 100},
                     {"relation": "DependsOn"}, {"provenance": "Observed"},
                     {"confidence": float("nan")}, {"confidence": True}):
            with self.assertRaises(AssertionError): probe.assert_wire(body)
        probe.assert_wire({"version": "9007199254740993", "outside_neighbor_count": None,
                           "relation": "depends_on", "provenance": "observed", "confidence": .5})

    def test_hidden_marker_and_partial_shortest_claim_fail_the_probe(self):
        with self.assertRaisesRegex(AssertionError, "hidden"):
            probe.assert_no_hidden({"cursor": "service%3Asecret"}, ["service:secret"])
        path = {"status": "Found", "nodes": ["r", "a", "c"], "edge_ids": ["ra", "ac"],
                "partial": False, "cursor": None, "published_sequence": "1"}
        probe.assert_path(path, ["r", "a", "c"], ["ra", "ac"])
        with self.assertRaisesRegex(AssertionError, "partial graph"):
            probe.assert_path(path | {"partial": True}, ["r", "a", "c"], ["ra", "ac"])
        with self.assertRaisesRegex(AssertionError, "tie-break"):
            probe.assert_path(path | {"nodes": ["r", "b", "c"]}, ["r", "a", "c"], ["ra", "ac"])

    def test_fixture_runs_are_isolated_while_oracles_are_identical(self):
        first = probe.fixture_plan("1" * 32)
        second = probe.fixture_plan("2" * 32)
        self.assertTrue(set(first["sources"].values()).isdisjoint(second["sources"].values()))
        self.assertNotEqual(first["service_alias"], second["service_alias"])
        for section in ("directed", "hidden_bridge", "fanout", "ancestor_http_matrix"):
            self.assertEqual(first[section], second[section])

    def test_oracle_rejects_wrong_id_order_and_duplicate_registry_identity(self):
        ids = {"r": "service:root", "a": "service:001", "b": "service:002", "c": "service:003"}
        self.assertEqual(probe.bind_path_oracle(["r", "a", "c"], ids),
                         ["service:root", "service:001", "service:003"])
        with self.assertRaisesRegex(ValueError, "a < b"):
            probe.bind_path_oracle(["r", "a", "c"], ids | {"a": ids["b"], "b": ids["a"]})
        with self.assertRaisesRegex(ValueError, "distinct"):
            probe.bind_path_oracle(["r", "a", "c"], ids | {"c": ids["a"]})

    def new_probe(self, directory):
        with patch.object(probe.owner, "require_frozen", return_value={"file": "hash"}):
            return probe.Probe({"api": "http://fixture.invalid"}, directory)

    def test_existing_output_is_rejected_without_network_or_overwrite(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(probe, "http") as http:
            marker = Path(directory) / "prior.json"; marker.write_text("evidence")
            with self.assertRaises(FileExistsError): self.new_probe(directory)
            self.assertEqual(marker.read_text(), "evidence")
            http.assert_not_called()

    def test_failed_put_records_exact_wire_and_never_authorization(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            instance.tokens["A"] = "secret-bearer"
            response = b'{"reason":"Conflict"}'
            with patch.object(probe, "http", return_value=(409, response)) as call:
                with self.assertRaisesRegex(AssertionError, "returned 409"):
                    instance.request("/v1/topology/nodes/service:id", method="PUT",
                                     body={"version": "9007199254740993"}, case="H02", expected=(200,))
            self.assertEqual(call.call_args.kwargs["method"], "PUT")
            wire = call.call_args.args[1]
            journal = json.loads((instance.output / "http.jsonl").read_text())
            self.assertEqual(journal["request_sha256"], hashlib.sha256(wire).hexdigest())
            self.assertEqual(journal["response_sha256"], hashlib.sha256(response).hexdigest())
            self.assertEqual((instance.output / "http-0001-request.bin").read_bytes(), wire)
            self.assertEqual(journal["status"], 409)
            self.assertNotIn("secret-bearer", "".join(p.read_text() for p in instance.output.iterdir()))

    def test_anonymous_delete_does_not_add_token_and_accepts_empty_204(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            with patch.object(probe, "http", return_value=(204, b"")) as call:
                self.assertIsNone(instance.request("/v1/topology/nodes/service:id", method="DELETE",
                    principal=None, expected=(204,), case="transport-empty-body"))
            self.assertNotIn("Authorization", call.call_args.args[2])
            self.assertEqual(call.call_args.kwargs["method"], "DELETE")

    def test_two_nonces_authenticate_distinct_real_subjects_without_persisting_secrets(self):
        subjects = []
        def token(subject):
            payload = base64.urlsafe_b64encode(json.dumps({"sub": subject}).encode()).decode().rstrip("=")
            return "header." + payload + ".signature"
        def http(url, body=None, headers=None, method=None, **kwargs):
            if "/probe-identity/" in url:
                nonce = url.rsplit("/", 1)[1]; subject = "subject-" + nonce; subjects.append(subject)
                return 200, json.dumps({"nonce": nonce, "subject": subject,
                    "username": subject, "password": "private-password"}).encode()
            form = probe.urllib.parse.parse_qs(body.decode())
            if form["grant_type"] == ["client_credentials"]:
                return 200, json.dumps({"access_token": token("ingest-only")}).encode()
            return 200, json.dumps({"access_token": token(form["username"][0])}).encode()
        with tempfile.TemporaryDirectory() as directory, patch.object(probe, "http", side_effect=http):
            for name in ("first", "second"):
                instance = self.new_probe(Path(directory) / name)
                instance.session.update(control="http://control.invalid", control_secret="control-secret",
                    token_url="http://issuer.invalid/token", evidence_users={"B": ["subject-B", "private-B"]},
                    evidence_client={"id": "client", "secret": "client-secret"},
                    client_id="ingest", client_secret="ingest-secret")
                instance.authenticate()
                journal = "".join(p.read_text() for p in instance.output.iterdir())
                for secret in ("private-password", "private-B", "client-secret", "control-secret", ".signature"):
                    self.assertNotIn(secret, journal)
            self.assertEqual(len(subjects), 2)
            self.assertNotEqual(subjects[0], subjects[1])

    def test_observed_fixture_has_distinct_trace_and_parent_child_source_binding(self):
        sources = {"parent": "s-parent", "child": "s-child", "B": "s-B"}
        payload = probe.observed_exports("1" * 32, sources, "namespace", 9007199254740993)
        other = probe.observed_exports("2" * 32, sources, "namespace", 9007199254740993)
        def span(label): return payload[label]["resourceSpans"][0]["scopeSpans"][0]["spans"][0]
        self.assertEqual(span("child")["parentSpanId"], span("parent")["spanId"])
        self.assertEqual(span("cross_child")["parentSpanId"], span("cross_parent")["spanId"])
        self.assertEqual(span("child")["traceId"], span("parent")["traceId"])
        self.assertNotEqual(payload["trace"], payload["cross_trace"])
        self.assertNotEqual(payload["trace"], other["trace"])
        self.assertEqual(span("child")["startTimeUnixNano"], "9007199254740993")
        attrs = payload["cross_child"]["resourceSpans"][0]["resource"]["attributes"]
        values = {a["key"]: a["value"]["stringValue"] for a in attrs}
        self.assertEqual(values["bizigo.source_key"], sources["B"])
        self.assertEqual(values["owner_group"], "forged-admin")

    def test_nano_clock_preserves_all_nine_digits(self):
        self.assertEqual(probe.iso_nano(1_000_000_001), "1970-01-01T00:00:01.000000001Z")
        self.assertEqual(probe.iso_nano(1_999_999_999), "1970-01-01T00:00:01.999999999Z")

    def test_retry_oracle_rejects_changed_identity_duplicate_proof_and_partial(self):
        edge = {"id": "stable", "from_node_id": "a", "to_node_id": "b", "relation": "depends_on",
                "provenance": "observed", "directed": True}
        refs = [{"id": "raw-" + key, "trace_logical_id": "trace", "span_logical_id": key,
                 "event_time_unix_nano": "1000"} for key in ("parent", "child")]
        original = {"edge": edge, "evidence": refs, "evidence_cursor": None}
        expanded = original | {"evidence": refs + [r | {"id": r["id"] + "-retry"} for r in refs]}
        probe.assert_retry_proof(original, expanded, [edge])
        for changed, edges in ((expanded | {"edge": edge | {"id": "changed"}}, [{}]),
                               (expanded | {"evidence": refs * 2}, [{}]),
                               (expanded | {"evidence_cursor": "more"}, [{}]),
                               (expanded | {"edge": edge | {"from_node_id": "b"}}, [{}]),
                               (expanded, [{}, {}])):
            with self.assertRaises(AssertionError): probe.assert_retry_proof(original, changed, edges)

    def test_failed_collector_is_journaled_and_cannot_be_a_topology_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            instance = self.new_probe(Path(directory) / "run")
            instance.session["collector"] = "http://collector.invalid"
            with patch.object(probe, "http", return_value=(503, b"unavailable")):
                with self.assertRaisesRegex(AssertionError, "Collector rejected"):
                    instance.publish("first", {"resourceSpans": []})
            self.assertEqual(json.loads((instance.output / "collector-first.json").read_text())["status"], 503)
            self.assertEqual((instance.output / "collector-first-response.bin").read_bytes(), b"unavailable")
            self.assertFalse((instance.output / "result.json").exists())


if __name__ == "__main__":
    unittest.main()
