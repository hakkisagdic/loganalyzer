#!/usr/bin/env python3
"""Independent real REST/Collector/RCA topology probe. Never starts Docker.

Docker-free transport/oracle tests are not H07 live evidence. Each live run
requires a frozen Planner-owned session and its own fresh Keycloak subject.
"""
import argparse
import base64
import datetime
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("topology_probe_owner", ROOT / "tools/topology-graph-smoke.py")
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


def fixture_plan(nonce):
    """Contract §4 symbolic oracle, independent of product responses/algorithms.

    Actual immutable IDs are assigned by production registry writes. The runner
    must bind a/b in ordinal ID order before creating edges; names are not IDs.
    The hidden-bridge and fanout graphs are separate seed groups.
    """
    nonce = uuid.UUID(nonce).hex
    targets = [f"t{index:02}" for index in range(20)]
    edges = [["r", "a"], ["r", "b"], ["a", "c"], ["b", "c"],
             ["c", "d"], ["d", "a"], ["x", "y"], ["c", "c"]]
    return {
        "nonce": nonce,
        "sources": {key: f"s05-{nonce}-{key}" for key in ("A", "B", "unknown")},
        "service_alias": {"namespace": "s05-" + nonce, "name": "checkout"},
        "ordinal_precondition": ["a", "b"],
        "directed": {"nodes": ["r", "a", "b", "c", "d", "x", "y"], "edges": edges,
            "paths": [
                {"from": "r", "to": "c", "status": "Found", "nodes": ["r", "a", "c"]},
                {"from": "r", "to": "d", "status": "Found", "nodes": ["r", "a", "c", "d"]},
                {"from": "c", "to": "r", "status": "Unreachable", "nodes": []},
                {"from": "x", "to": "d", "status": "Unreachable", "nodes": []}],
            "ancestors": [
                {"targets": ["a", "b"], "node": "r", "paths": [["r", "a"], ["r", "b"]]},
                {"targets": ["c", "d"], "node": "a", "paths": [["a", "c"], ["a", "c", "d"]]}]},
        "hidden_bridge": {"nodes": {"r": "A", "b": "B", "c": "A"},
            "edges": [["r", "b"], ["b", "c"]], "from": "r", "to": "c",
            "status_A": "NotVerified", "proof_A": [], "outside_r_A": "1"},
        "fanout": {"root": "fanout-root", "targets": targets,
            "edges": [["fanout-root", target] for target in targets],
            "expected_ancestor": "fanout-root",
            "expected_paths": [["fanout-root", target] for target in targets]},
        "ancestor_http_matrix": {"one": 400, "two": 200, "twenty": 200,
            "twenty_one": 400, "duplicate": 400, "comma_list": 400, "hidden": 404, "unknown": 404},
    }


def bind_path_oracle(symbolic_nodes, ids):
    """Map only fixture labels; never infer an expected path from server output."""
    if ids["a"] >= ids["b"]:
        raise ValueError("fixture requires immutable ID a < b; seed in this order")
    if len(set(ids.values())) != len(ids):
        raise ValueError("fixture node identities must be distinct")
    return [ids[symbol] for symbol in symbolic_nodes]


def iso_nano(nano):
    seconds, fraction = divmod(nano, 1_000_000_000)
    return datetime.datetime.fromtimestamp(seconds, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S") + f".{fraction:09d}Z"


def observed_exports(nonce, sources, namespace, start):
    """Fixed parent→child proof, independent of the projector/query response."""
    trace = uuid.uuid5(uuid.UUID(nonce), "same-owner-trace").hex
    cross = uuid.uuid5(uuid.UUID(nonce), "cross-owner-trace").hex
    def export(source, trace_id, span_id, parent):
        attributes = [{"key": k, "value": {"stringValue": v}} for k, v in {
            "bizigo.source_key": source, "service.namespace": namespace, "service.name": "checkout",
            "test.run": nonce, "owner_group": "forged-admin"}.items()]
        span = {"traceId": trace_id, "spanId": span_id, "parentSpanId": parent,
                "name": "topology-live", "kind": 2, "startTimeUnixNano": str(start),
                "endTimeUnixNano": str(start + 100), "status": {"code": 2}}
        return {"resourceSpans": [{"resource": {"attributes": attributes}, "scopeSpans": [{"spans": [span]}]}]}
    return {"parent": export(sources["parent"], trace, "0000000000000001", ""),
            "child": export(sources["child"], trace, "0000000000000002", "0000000000000001"),
            "cross_parent": export(sources["parent"], cross, "0000000000000001", ""),
            "cross_child": export(sources["B"], cross, "0000000000000002", "0000000000000001"),
            "trace": trace, "cross_trace": cross}


def assert_retry_proof(original, retried, own_edges):
    """B03 §85: one semantic edge; separately accepted raw references may grow."""
    if (len(own_edges) != 1 or own_edges[0].get("id") != original["edge"]["id"]
            or retried["edge"]["id"] != original["edge"]["id"]):
        raise AssertionError("topology-only retry/replay multiplied or changed public edge identity")
    for key in ("from_node_id", "to_node_id", "relation", "provenance", "directed"):
        if retried["edge"][key] != original["edge"][key]:
            raise AssertionError("retry changed semantic graph adjacency/provenance")
    old, new = original["evidence"], retried["evidence"]
    ids = {r["id"] for r in new}
    semantic = lambda rows: {(r["trace_logical_id"], r["span_logical_id"], r["event_time_unix_nano"]) for r in rows}
    if (retried["evidence_cursor"] is not None or len(old) != 2 or len(new) != 4 or len(ids) != 4
            or not {r["id"] for r in old}.issubset(ids) or semantic(old) != semantic(new)):
        raise AssertionError("bounded raw references lost identity or changed semantic span proof")


def assert_wire(value):
    """Check accepted public wire invariants without accepting enum fallbacks."""
    decimal_keys = {"version", "published_sequence", "publication_sequence", "outside_neighbor_count"}
    if isinstance(value, dict):
        for key, item in value.items():
            if key in decimal_keys or key.endswith("_unix_nano"):
                if item is not None and (not isinstance(item, str) or not re.fullmatch(r"[0-9]+", item)):
                    raise AssertionError("lossy/nondecimal topology wire value: " + key)
            if key == "relation" and item not in ("depends_on", "contains", "connects_to"):
                raise AssertionError("unknown topology relation wire value")
            if key == "provenance" and item not in ("declared", "observed"):
                raise AssertionError("unknown topology provenance wire value")
            if key == "confidence" and (type(item) not in (int, float) or not math.isfinite(item)):
                raise AssertionError("confidence must be a finite JSON number")
            assert_wire(item)
    elif isinstance(value, list):
        for item in value:
            assert_wire(item)


def assert_no_hidden(value, markers):
    text = json.dumps(value, ensure_ascii=False)
    for marker in markers:
        if not marker:
            raise ValueError("empty hidden marker would not be an independent oracle")
        if marker in text or urllib.parse.quote(marker, safe="") in text:
            raise AssertionError("hidden identity/attribute/proof leaked in public topology response")


def assert_path(value, expected_nodes, expected_edges, status="Found"):
    assert_wire(value)
    if value["status"] != status or value["nodes"] != expected_nodes or value["edge_ids"] != expected_edges:
        raise AssertionError("directed shortest path or deterministic tie-break differs from fixed fixture")
    if status == "Found" and (value["partial"] or value["cursor"] is not None):
        raise AssertionError("partial graph cannot verify a shortest path")


def http(url, body=None, headers=None, method="GET", timeout=30):
    request = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        with error:
            return error.code, error.read()


class Probe:
    """One private nonce/subject and one exact-wire evidence journal per run."""
    def __init__(self, session, output):
        self.session = session
        self.output = Path(output).resolve()
        # Atomic creation rejects reuse, including two concurrent invocations.
        self.output.mkdir(parents=True, exist_ok=False)
        self.output.chmod(0o700)
        self.nonce = uuid.uuid4().hex
        self.tokens = {}
        self.calls = 0
        self.before = owner.require_frozen(session)
        self.save("source-before", self.before)
        self.save("expected-fixture", fixture_plan(self.nonce))

    def save(self, name, value):
        (self.output / (name + ".json")).write_text(json.dumps(value, indent=2))

    def authenticate(self):
        session = self.session
        code, raw = http(session["control"] + "/probe-identity/" + self.nonce, b"",
                         {"Authorization": "Bearer " + session["control_secret"]}, method="POST")
        if code != 200:
            raise AssertionError("fresh Keycloak probe identity creation failed")
        identity = json.loads(raw)
        if identity["nonce"] != self.nonce:
            raise AssertionError("probe identity belongs to another invocation")
        users = dict(session["evidence_users"])
        users["A"] = [identity["username"], identity["password"]]
        client = session["evidence_client"]
        subjects = {}
        for principal, (username, password) in users.items():
            form = {"grant_type": "password", "client_id": client["id"],
                    "client_secret": client["secret"], "username": username, "password": password}
            code, raw = http(session["token_url"], urllib.parse.urlencode(form).encode(),
                             {"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
            if code != 200:
                raise AssertionError("real Keycloak reader token issuance failed")
            token = json.loads(raw)["access_token"]
            payload = token.split(".")[1]
            claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
            subjects[principal] = claims["sub"]
            self.tokens[principal] = token
        if subjects["A"] != identity["subject"] or subjects["A"] == subjects["B"]:
            raise AssertionError("expected distinct real A/B subjects and fresh A admission identity")
        code, raw = http(session["token_url"], urllib.parse.urlencode({"grant_type": "client_credentials",
            "client_id": session["client_id"], "client_secret": session["client_secret"]}).encode(),
            {"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
        if code != 200:
            raise AssertionError("ingest-only Keycloak token issuance failed")
        self.tokens["ingest"] = json.loads(raw)["access_token"]
        # Credentials and bearer tokens are never persisted in evidence.
        self.save("probe-identity", {"nonce": self.nonce, "subjects": subjects})

    def request(self, path, *, method="GET", body=None, principal="A", expected=(200,), case):
        if not path.startswith("/v1/") or path.startswith("//"):
            raise ValueError("probe requests must target the session product API")
        payload = None if body is None else json.dumps(body, separators=(",", ":")).encode()
        headers = {"Content-Type": "application/json"}
        if principal is not None:
            headers["Authorization"] = "Bearer " + self.tokens[principal]
        code, raw = http(self.session["api"] + path, payload, headers, method=method)
        self.calls += 1
        name = f"http-{self.calls:04}"
        (self.output / (name + "-request.bin")).write_bytes(payload or b"")
        (self.output / (name + "-response.bin")).write_bytes(raw)
        record = {"number": self.calls, "method": method, "path": path, "principal": principal,
                  "caseId": case, "status": code, "expected": list(expected),
                  "request_sha256": hashlib.sha256(payload or b"").hexdigest(),
                  "response_sha256": hashlib.sha256(raw).hexdigest()}
        with (self.output / "http.jsonl").open("a") as journal:
            journal.write(json.dumps(record) + "\n")
        if code not in expected:
            raise AssertionError(f"{case}: {method} {path} returned {code}; see {name}-response.bin")
        if method == "GET" and code == 200 and len(raw) > 1024 * 1024:
            raise AssertionError("topology response exceeds accepted serialized UTF8 byte cap")
        if not raw:
            return None
        return json.loads(raw)

    def verify_freeze(self):
        after = owner.require_frozen(self.session)
        self.save("source-after", after)
        if after != self.before:
            raise AssertionError("source or binary changed during independent probe")

    def create_service(self, label, principal_owner="A", bindings=None):
        """Seed through the real registry, never by writing fixture database rows."""
        body = {"kind": 2, "displayName": f"s05-{self.nonce}-{label}",
                "ownerGroup": self.session["evidence_owners"][principal_owner],
                "enabled": True, "bindings": [] if bindings is None else bindings}
        node = self.request("/v1/topology/nodes", method="POST", body=body,
                            principal="admin", expected=(201,), case="D01/H07-registry-seed")
        node_id = node["id"]
        prefix, separator, suffix = node_id.partition(":")
        if prefix != "service" or separator != ":" or str(uuid.UUID(suffix)) != suffix:
            raise AssertionError("registry did not return a canonical immutable service ID")
        if uuid.UUID(suffix).int == 0:
            raise AssertionError("registry returned an empty identity")
        # Do not normalize a numeric JSON version; that would conceal wire loss.
        assert_wire({"version": node["version"], "valid_from_unix_nano": node["validFromUnixNano"]})
        return node

    def create_declared_edge(self, from_id, to_id, relation="depends_on"):
        if relation not in ("depends_on", "contains", "connects_to"):
            raise ValueError("invalid declared relation")
        edge = self.request("/v1/topology/edges", method="POST",
            body={"fromNodeId": from_id, "toNodeId": to_id, "relation": relation},
            principal="admin", expected=(201,), case="D04/H07-declared-seed")
        assert_wire(edge)
        if (edge["fromNodeId"], edge["toNodeId"], edge["relation"], edge["provenance"]) != (
                from_id, to_id, relation, "declared"):
            raise AssertionError("declared edge endpoints/relation/provenance changed during write")
        if edge["directed"] is not (relation != "connects_to"):
            raise AssertionError("declared edge direction differs from relation")
        return edge

    def seed_declared_graphs(self):
        """Bind fixed expected proofs to server IDs before any graph read.

        The a/b labels are assigned by immutable ordinal identity. Display names
        are deliberately equal for these two nodes and never choose a path.
        """
        plan = fixture_plan(self.nonce)
        result = {}
        all_ids, all_edges = set(), set()
        for name in ("directed", "hidden_bridge", "fanout"):
            section = plan[name]
            labels = section.get("nodes")
            if labels is None:
                labels = [section["root"], *section["targets"]]
            nodes = {label: self.create_service(
                name + "-" + ("same-name" if name == "directed" and label in ("a", "b") else label),
                section["nodes"][label] if name == "hidden_bridge" else "A") for label in labels}
            if name == "directed":
                nodes["a"], nodes["b"] = sorted((nodes["a"], nodes["b"]), key=lambda node: node["id"])
            ids = {label: node["id"] for label, node in nodes.items()}
            if len(set(ids.values())) != len(ids) or all_ids.intersection(ids.values()):
                raise AssertionError("registry merged distinct fixture nodes")
            all_ids.update(ids.values())
            edges = []
            for source, target in section["edges"]:
                edge = self.create_declared_edge(ids[source], ids[target])
                if not edge["id"] or edge["id"] in all_edges:
                    raise AssertionError("registry merged distinct declared fixture edges")
                all_edges.add(edge["id"])
                edges.append({"from": source, "to": target, "id": edge["id"]})
            result[name] = {"nodes": nodes, "ids": ids, "edges": edges}
        # This is seed evidence only; it never sets a live result/PASS.
        self.save("declared-fixture", result)
        return result

    def read_pages(self, route, field, *, as_of, hidden_markers, limit=17):
        """Follow only opaque server cursors; detect loops, duplicates and drift."""
        rows, cursors, ids = [], set(), set()
        sequence = None
        cursor = None
        for _ in range(20):
            parameters = [("asOf", as_of), ("limit", str(limit))]
            if cursor is not None:
                parameters.append(("cursor", cursor))
            page = self.request(route + "?" + urllib.parse.urlencode(parameters), case="H03/Q04-pages")
            assert_wire(page)
            assert_no_hidden(page, hidden_markers)
            if sequence is None:
                sequence = page["published_sequence"]
            if page["published_sequence"] != sequence:
                raise AssertionError("page sequence changed without restart response")
            items = page[field]
            if not isinstance(items, list) or len(items) > limit:
                raise AssertionError("page violated requested item bound")
            for row in items:
                if row["id"] in ids or rows and row["id"] <= rows[-1]["id"]:
                    raise AssertionError("pagination duplicated or reordered immutable identity")
                ids.add(row["id"])
                rows.append(row)
            cursor = page["cursor"]
            if page["partial"] is not (cursor is not None):
                raise AssertionError("page completeness disagrees with continuation")
            if cursor is None:
                return rows
            if not isinstance(cursor, str) or not cursor or cursor in cursors or not items:
                raise AssertionError("pagination cursor did not make progress")
            cursors.add(cursor)
        raise AssertionError("fixture pagination exceeded bounded probe page budget")

    def read_declared_graphs(self, fixture, *, as_of):
        """Seven production GET surfaces checked against independent seed oracles.

        This checkpoint excludes observed projection, replay and RCA assertions;
        completing it cannot produce a final H07 PASS.
        """
        plan = fixture_plan(self.nonce)
        graph, hidden, fanout = (fixture[k] for k in ("directed", "hidden_bridge", "fanout"))
        markers = [hidden["ids"]["b"], hidden["nodes"]["b"]["displayName"],
                   *[edge["id"] for edge in hidden["edges"]]]
        def get(route, pairs=(), *, expected=(200,), principal="A", case="H03"):
            value = self.request(route + "?" + urllib.parse.urlencode([("asOf", as_of), *pairs]),
                expected=expected, principal=principal, case=case)
            if principal == "A":
                assert_no_hidden(value, markers)
            if 200 in expected:
                assert_wire(value)
            return value
        def path_edges(group, labels):
            by_pair = {(e["from"], e["to"]): e["id"] for e in group["edges"]}
            return [by_pair[pair] for pair in zip(labels, labels[1:])]
        routes = ["/v1/topology/nodes", "/v1/topology/nodes/" + graph["ids"]["r"],
                  "/v1/topology/edges", "/v1/topology/edges/" + graph["edges"][0]["id"],
                  "/v1/topology/nodes/" + hidden["ids"]["r"] + "/neighbors",
                  "/v1/topology/path", "/v1/topology/ancestors"]
        for route in routes:
            get(route, expected=(401,), principal=None, case="H01-seven-anonymous")
            get(route, expected=(403,), principal="ingest", case="H01-seven-ingest-denied")
        visible_nodes = self.read_pages(routes[0], "nodes", as_of=as_of, hidden_markers=markers)
        expected_ids = set(graph["ids"].values()) | set(fanout["ids"].values()) | {
            hidden["ids"]["r"], hidden["ids"]["c"]}
        if not expected_ids.issubset({n["id"] for n in visible_nodes}):
            raise AssertionError("node pagination omitted a known visible fixture identity")
        visible_edges = self.read_pages(routes[2], "edges", as_of=as_of, hidden_markers=markers)
        expected_edges = {e["id"] for group in (graph, fanout) for e in group["edges"]}
        if not expected_edges.issubset({e["id"] for e in visible_edges}):
            raise AssertionError("edge pagination omitted a known visible fixture edge")
        node = get(routes[1])["node"]
        if node["id"] != graph["ids"]["r"] or node["display_name"] != graph["nodes"]["r"]["displayName"]:
            raise AssertionError("node detail differs from immutable registry seed")
        detail = get(routes[3])
        if detail["edge"]["id"] != graph["edges"][0]["id"] or detail["edge"]["provenance"] != "declared":
            raise AssertionError("declared edge detail lost identity/provenance")
        boundary = get(routes[4], case="Q03-hidden-boundary")
        if boundary["neighbors"] != [] or boundary["outside_neighbor_count"] != "1":
            raise AssertionError("hidden bridge must expose only distinct boundary count 1")
        get("/v1/topology/nodes/" + hidden["ids"]["b"], expected=(404,), case="Q02-hidden-detail")
        get("/v1/topology/edges/" + hidden["edges"][0]["id"], expected=(404,), case="Q02-hidden-edge")
        for oracle in plan["directed"]["paths"]:
            value = get(routes[5], [("fromNode", graph["ids"][oracle["from"]]),
                                   ("toNode", graph["ids"][oracle["to"]])], case="R01-fixed-path")
            assert_path(value, bind_path_oracle(oracle["nodes"], graph["ids"]),
                        path_edges(graph, oracle["nodes"]), oracle["status"])
        value = get(routes[5], [("fromNode", hidden["ids"]["r"]), ("toNode", hidden["ids"]["c"])],
                    case="R04-hidden-bridge")
        assert_path(value, [], [], "NotVerified")
        ancestor_cases = [(graph, x) for x in plan["directed"]["ancestors"]]
        ancestor_cases.append((fanout, {"targets": plan["fanout"]["targets"], "node": plan["fanout"]["root"],
                                       "paths": plan["fanout"]["expected_paths"]}))
        for group, oracle in ancestor_cases:
            value = get(routes[6], [("nodeId", group["ids"][k]) for k in oracle["targets"]], case="R02/B05-ancestor")
            if value["status"] != "Found" or value["node_id"] != group["ids"][oracle["node"]] or value["partial"]:
                raise AssertionError("strict common ancestor differs from fixed graph")
            expected = sorted([{"target_node_id": group["ids"][labels[-1]],
                "nodes": [group["ids"][label] for label in labels], "edge_ids": path_edges(group, labels)}
                for labels in oracle["paths"]], key=lambda item: item["target_node_id"])
            if sorted(value["paths"], key=lambda item: item["target_node_id"]) != expected:
                raise AssertionError("ancestor proofs differ from fixed targets/routes")
        a, b = graph["ids"]["a"], graph["ids"]["b"]
        invalid = [[a], [a, a], [a + "," + b], list(fanout["ids"].values())]
        for targets in invalid:
            get(routes[6], [("nodeId", x) for x in targets], expected=(400,), case="B05-ancestor-invalid")
        for target in (hidden["ids"]["b"], "service:" + str(uuid.uuid4())):
            get(routes[6], [("nodeId", a), ("nodeId", target)], expected=(404,), case="B05-ancestor-invisible")
        self.save("declared-read-checkpoint", {"status": "CHECKPOINT_ONLY", "asOf": as_of,
            "routes": 7, "observedReplayRca": "NOT_RUN", "calls_so_far": self.calls})

    def control(self, path):
        code, raw = http(self.session["control"] + path, b"",
            {"Authorization": "Bearer " + self.session["control_secret"]}, method="POST", timeout=180)
        # Control responses contain no credential material; persist the actual outcome.
        self.save("control-" + path.strip("/").replace("/", "-"), {"status": code, "body": raw.decode(errors="replace")})
        if code != 200:
            raise AssertionError("owned session control failed: " + path)
        return json.loads(raw)

    def publish(self, label, payload):
        raw_payload = json.dumps(payload, separators=(",", ":")).encode()
        code, response = http(self.session["collector"] + "/v1/traces", raw_payload,
            {"Content-Type": "application/json"}, method="POST")
        (self.output / ("collector-" + label + "-request.bin")).write_bytes(raw_payload)
        (self.output / ("collector-" + label + "-response.bin")).write_bytes(response)
        self.save("collector-" + label, {"status": code, "request_sha256": hashlib.sha256(raw_payload).hexdigest(),
            "response_sha256": hashlib.sha256(response).hexdigest()})
        if code != 200:
            raise AssertionError("Collector rejected topology producer: " + label)

    def wait_for(self, operation, label):
        deadline = time.monotonic() + 45
        while True:
            if operation():
                return
            if time.monotonic() >= deadline:
                raise AssertionError("bounded topology convergence timeout: " + label)
            time.sleep(.25)

    def observed_and_rca(self):
        namespace = "s05-" + self.nonce
        sources = {k: namespace + "-" + k for k in ("parent", "child", "B")}
        nodes = {}
        for label, source in sources.items():
            principal = "B" if label == "B" else "A"
            self.request("/v1/sources/", method="POST", body={"sourceId": source,
                "ownerGroup": self.session["evidence_owners"][principal]}, principal="admin", expected=(200, 201), case="B01-source")
            nodes[label] = self.create_service("observed-" + label, principal,
                [{"sourceId": source, "serviceNamespace": namespace, "serviceName": "checkout"}])
        start = max(time.time_ns(), *[int(n["validFromUnixNano"]) + 1000 for n in nodes.values()])
        payloads = observed_exports(self.nonce, sources, namespace, start)
        window = "?" + urllib.parse.urlencode({"from_nano": str(start - 1), "to_nano": str(start + 1000)})
        def count(label, expected):
            return self.request("/v1/traces/count" + window + "&resource_id=" + urllib.parse.quote(sources[label]),
                principal="admin", case="O01-typed-convergence")["count"] == str(expected)
        self.publish("child-first", payloads["child"])
        self.wait_for(lambda: count("child", 1), "child typed")
        self.publish("parent-later", payloads["parent"])
        self.wait_for(lambda: count("parent", 1), "parent typed")
        as_of = iso_nano(max(time.time_ns(), start + 1000))
        query = "?" + urllib.parse.urlencode({"asOf": as_of, "from": iso_nano(start - 1),
            "to": iso_nano(start + 1000), "provenance": "observed", "limit": "200"})
        def edges(principal="admin"):
            value = self.request("/v1/topology/edges" + query, principal=principal, case="O02/B02-observed-read")
            assert_wire(value)
            if value["partial"] or value["cursor"] is not None:
                raise AssertionError("small isolated observed fixture unexpectedly partial")
            return value["edges"]
        def own_pair(rows, end):
            return [e for e in rows if e["from_node_id"] == nodes["parent"]["id"] and e["to_node_id"] == nodes[end]["id"]]
        self.wait_for(lambda: len(own_pair(edges(), "child")) == 1, "one observed parent→child edge")
        edge = own_pair(edges("A"), "child")[0]
        if edge["relation"] != "depends_on" or edge["provenance"] != "observed" or edge["directed"] is not True:
            raise AssertionError("observed direction/provenance changed")
        if edge["from_owner_group"] != edge["to_owner_group"] or edge["to_owner_group"] != self.session["evidence_owners"]["A"]:
            raise AssertionError("forged payload owner affected admitted ownership")
        detail_path = "/v1/topology/edges/" + edge["id"] + query
        original = self.request(detail_path, case="O03-proof")
        if len(original["evidence"]) != 2 or original["evidence_cursor"] is not None:
            raise AssertionError("one observed relation requires two distinct span references")
        path_url = "/v1/topology/path" + query + "&" + urllib.parse.urlencode({
            "fromNode": nodes["parent"]["id"], "toNode": nodes["child"]["id"]})
        proof_nodes = [nodes["parent"]["id"], nodes["child"]["id"]]
        assert_path(self.request(path_url, case="B03-before-retry-semantic-path"), proof_nodes, [edge["id"]])
        self.request(detail_path, principal="B", expected=(404,), case="B02-hidden-proof")
        # Different HTTP envelopes intentionally remain separate raw admissions.
        self.publish("child-http-retry", payloads["child"])
        self.publish("parent-http-retry", payloads["parent"])
        self.wait_for(lambda: count("child", 2) and count("parent", 2), "separate raw HTTP retries")
        self.wait_for(lambda: len(self.request(detail_path, case="B03-raw-reference-convergence")["evidence"]) == 4,
                      "bounded references for two separate parent/child raw occurrences")
        retried = self.request(detail_path, case="B03-separate-http-semantic-proof")
        assert_retry_proof(original, retried, own_pair(edges("A"), "child"))
        # Every bounded detail reference must name an actually admitted raw
        # occurrence. Four is specific to this two-span/two-HTTP-round fixture.
        admitted_ids = set()
        for label in ("parent", "child"):
            raw = self.request("/v1/traces" + window + "&resource_id=" + urllib.parse.quote(sources[label]),
                               case="B03-admitted-raw-reference-identity")
            if raw["partial"] or raw["cursor"] is not None or len(raw["records"]) != 2:
                raise AssertionError("isolated raw retry fixture did not expose exactly two complete occurrences")
            admitted_ids.update(record["logical_id"] for record in raw["records"])
        if len(admitted_ids) != 4 or admitted_ids != {item["id"] for item in retried["evidence"]}:
            raise AssertionError("topology detail references do not match accepted raw envelope occurrences")
        assert_path(self.request(path_url, case="B03-after-retry-semantic-path"), proof_nodes, [edge["id"]])
        self.control("/replay")
        again = self.request(detail_path, case="B03-same-envelope-replay-and-http-retry")
        assert_retry_proof(original, again, own_pair(edges("A"), "child"))
        if again["evidence"] != retried["evidence"]:
            raise AssertionError("same-envelope replay multiplied raw references")
        assert_path(self.request(path_url, case="B03-after-replay-semantic-path"), proof_nodes, [edge["id"]])
        self.publish("cross-child-first", payloads["cross_child"])
        self.publish("cross-parent-later", payloads["cross_parent"])
        self.wait_for(lambda: count("B", 1) and count("parent", 3), "cross-owner raw trace")
        self.wait_for(lambda: len(own_pair(edges(), "B")) == 1, "cross-owner projection")
        cross = own_pair(edges(), "B")[0]
        if cross["from_owner_group"] != self.session["evidence_owners"]["A"] or cross["to_owner_group"] != self.session["evidence_owners"]["B"]:
            raise AssertionError("physical dual-owner snapshot changed")
        for principal in ("A", "B"):
            visible = edges(principal)
            if any(e["id"] == cross["id"] for e in visible):
                raise AssertionError("one endpoint authorization exposed a cross-owner edge")
            self.request("/v1/topology/edges/" + cross["id"] + query, principal=principal,
                expected=(404,), case="B02-both-endpoints-required")
        # A fresh real subject isolates the production RCA debounce key between probes.
        report = self.request("/v1/rca/", method="POST", body={"from": iso_nano(start - 1_000_000_000),
            "to": iso_nano(start + 1_000_000_000), "baseline_from": iso_nano(start - 3_000_000_000),
            "baseline_to": iso_nano(start - 1_000_000_000), "source_ids": [sources["parent"], sources["child"]]},
            expected=(201,), case="H07/E01-RCA-production")
        providers = {p["provider_id"]: p for p in report["providers"]}
        for name in ("topology.graph-path", "topology.common-ancestor"):
            if name not in providers or providers[name]["status"] in ("failed", "not_registered"):
                raise AssertionError("topology RCA provider missing/failed")
        reopened = self.request("/v1/rca/" + report["bundle_id"], case="E05-bundle-roundtrip")
        if reopened["content_hash"] != report["content_hash"] or reopened["providers"] != report["providers"]:
            raise AssertionError("persisted RCA hash/providers changed")
        self.request("/v1/rca/" + report["bundle_id"], principal="B", expected=(404,), case="E04-hidden-bundle")
        self.save("observed-rca-checkpoint", {"sources": sources, "nodes": nodes, "edge": edge, "cross": cross,
            "original_proof": original["evidence"], "replayed_proof": again["evidence"], "report": report,
            "raw_http_retry": "separate accepted records", "idempotency": "topology graph only"})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    args = parser.parse_args(argv)
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    probe = Probe(json.loads(args.session.read_text()), args.evidence_dir)
    cases = []
    try:
        probe.authenticate()
        probe.control("/restart")
        fixture = probe.seed_declared_graphs()
        probe.read_declared_graphs(fixture, as_of=iso_nano(time.time_ns()))
        cases.append("seven-REST-directed-ancestor-hidden-boundary")
        probe.observed_and_rca()
        cases.extend(["Collector-child-first-and-authoritative-binding", "dual-owner-read-scope",
                      "topology-retry-and-replay-proof", "RCA-production-and-bundle-roundtrip"])
        probe.verify_freeze()
        probe.save("result", {"status": "PASS", "scope": "H07 live probe; not Sprint05 acceptance",
            "nonce": probe.nonce, "cases": cases, "http_calls": probe.calls,
            "contract_sha256": owner.CONTRACT_SHA256})
    except Exception as error:
        probe.save("result", {"status": "FAIL", "nonce": probe.nonce, "completed_cases": cases,
            "http_calls": probe.calls, "error_type": type(error).__name__, "error": str(error)})
        raise
    finally:
        # Retain drift evidence even when an earlier oracle fails. A failed run
        # never becomes PASS because these diagnostic hashes happen to match.
        after = owner.snapshot()
        probe.save("source-after", after)
        probe.save("freeze-comparison", {"equal": probe.before == after})
        manifest = {str(p.relative_to(probe.output)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(probe.output.rglob("*")) if p.is_file() and p.name != "sha256.json"}
        probe.save("sha256", manifest)


if __name__ == "__main__":
    main()
