#!/usr/bin/env python3
"""Independent real Keycloak→production REST/RCA probe. No Docker commands.

Every invocation owns a fresh nonce and evidence directory. Credentials remain
in the private session; request evidence contains no Authorization headers.
"""
import argparse
import base64
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import time
import urllib.parse
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("collector_probe", ROOT / "tools/otlp-collector-probe.py")
old = importlib.util.module_from_spec(spec)
spec.loader.exec_module(old)
http, wait_for = old.http, old.wait_for
spec = importlib.util.spec_from_file_location("evidence_owner", ROOT / "tools/telemetry-evidence-smoke.py")
owner_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner_module)


def journal(output, number, path, owner, body, status, raw, case):
    request_bytes = b"" if body is None else json.dumps(body).encode()
    row = {"number": number, "method": "GET" if body is None else "POST", "path": path,
           "principal": owner, "status": status, "caseId": case,
           "request_sha256": hashlib.sha256(request_bytes).hexdigest(),
           "response_sha256": hashlib.sha256(raw).hexdigest()}
    with (output / "http.jsonl").open("a") as stream: stream.write(json.dumps(row) + "\n")
    (output / f"http-{number:04}-response.bin").write_bytes(raw)


def verify_raw(session, output, nonce):
    root = Path(session["signals"])
    processed = []
    for path in (root / "processed").glob("*.json"):
        item = json.loads(path.read_text())
        if any(any(a.get("key") == "test.run" and a.get("value", {}).get("stringValue") == nonce
                   for a in leaf["resource"].get("attributes", [])) for leaf in item["leaves"]):
            processed.append(item)
    assert {item["signal"] for item in processed} == {"Metrics", "Traces"}
    captures = {entry["sha256"]: entry for path in Path(session["captures"]).glob("*.json")
                if (entry := json.loads(path.read_text()))["status"] == 200}
    for item in processed:
        manifest = json.loads((root / "manifests" / (item["envelope_id"] + ".json")).read_text())
        compressed = old.s3_get(session["s3"], manifest["object_key"])
        assert hashlib.sha256(compressed).hexdigest() == manifest["object_sha256"]
        archive = output / (item["envelope_id"] + ".zst"); archive.write_bytes(compressed)
        decoded = subprocess.check_output([old.helpers.DOTNET, str(ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"),
            "--read-archive", str(archive), str(manifest["envelope_length"])], cwd=ROOT)
        envelope = json.loads(decoded); payload = base64.b64decode(envelope["payload"])
        sha = hashlib.sha256(payload).hexdigest()
        assert sha == envelope["payload_sha256"] == item["payload_sha256"]
        capture = captures[sha]
        assert payload == (Path(session["captures"]) / capture["payload"]).read_bytes()
        assert capture["path"] == "/v1/" + item["signal"].lower()
        assert envelope["content_type"] == capture["content_type"].split(";")[0]
        for suffix, value in (("manifest", manifest), ("processed", item), ("envelope", envelope), ("capture", capture)):
            (output / (item["envelope_id"] + "-" + suffix + ".json")).write_text(json.dumps(value, indent=2))
        (output / (item["envelope_id"] + "-raw.bin")).write_bytes(payload)
    return processed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    args = parser.parse_args()
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    session = json.loads(args.session.read_text())
    output = args.evidence_dir.resolve(); output.mkdir(parents=True, exist_ok=True)
    assert not list(output.iterdir()), "fresh independent evidence directory required"
    nonce = uuid.uuid4().hex; calls = 0; cases = []
    case_results = {}
    active_case = "setup"

    def save(name, value):
        (output / (name + ".json")).write_text(json.dumps(value, indent=2))

    before = owner_module.snapshot()
    assert before == json.loads(Path(session["manifestPath"]).read_text()), "session source/binary changed"
    save("source-before", before)

    status, raw, _ = http(session["control"] + "/probe-identity/" + nonce, b"",
                         {"Authorization": "Bearer " + session["control_secret"]})
    assert status == 200, "fresh scoped probe identity required"
    identity = json.loads(raw)
    assert identity["nonce"] == nonce
    users = dict(session["evidence_users"])
    users["A"] = [identity["username"], identity["password"]]
    save("admission-identity", {"nonce": nonce, "subject": identity["subject"],
                               "username": identity["username"], "scope": "network/core"})
    tokens = {}
    client = session["evidence_client"]
    for owner, (username, password) in users.items():
        data = urllib.parse.urlencode({"grant_type": "password", "client_id": client["id"], "client_secret": client["secret"],
                                       "username": username, "password": password}).encode()
        status, body, _ = http(session["token_url"], data, {"Content-Type": "application/x-www-form-urlencoded"})
        assert status == 200, "real reader token issuance failed"
        tokens[owner] = json.loads(body)["access_token"]
        if owner == "A":
            claims = tokens[owner].split(".")[1]
            claims = json.loads(base64.urlsafe_b64decode(claims + "=" * (-len(claims) % 4)))
            assert claims["sub"] == identity["subject"], "token did not use the fresh probe subject"
    data = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": session["client_id"],
                                   "client_secret": session["client_secret"]}).encode()
    status, body, _ = http(session["token_url"], data, {"Content-Type": "application/x-www-form-urlencoded"})
    assert status == 200; tokens["ingest"] = json.loads(body)["access_token"]

    def control(path):
        status, raw, _ = http(session["control"] + path, b"", {"Authorization": "Bearer " + session["control_secret"]}, timeout=180)
        assert status == 200, "owned session control failed: " + path
        return json.loads(raw)

    def request(path, body=None, owner="A", expected=(200,)):
        nonlocal calls
        headers = {"Content-Type": "application/json"}
        if owner is not None: headers["Authorization"] = "Bearer " + tokens[owner]
        code, raw, _ = http(session["api"] + path, None if body is None else json.dumps(body).encode(), headers, timeout=90)
        calls += 1
        journal(output, calls, path, owner, body, code, raw, active_case)
        try: value = json.loads(raw)
        except ValueError: value = raw.decode(errors="replace")
        save(f"http-{calls:04}", {"path": path, "owner": owner, "request": body, "status": code, "response": value})
        assert code in expected, (path, code, value)
        return value

    # The product scope resolver loads the fleet mappings at startup. Both
    # independent probes can safely restart this owned API before sending data.
    control("/restart")
    names = {owner: "s04-" + nonce + "-" + owner for owner in ("A", "B", "unknown")}
    for owner in ("A", "B"):
        request("/v1/sources/", {"sourceId": names[owner], "ownerGroup": session["evidence_owners"][owner]}, "admin", (200, 201))
    start = ((time.time_ns() // 1_000_000_000) + 1) * 1_000_000_000
    trace_ids = {owner: uuid.uuid4().hex for owner in names}
    save("fixture-window-ids", {"from_nano": str(start), "to_nano": str(start + 20_000_000_000),
                              "sources": names, "traces": trace_ids})
    save("typed-query-expected", {"metric_count_A": "12", "span_count_A": "2", "metric_count_B": "3",
                                  "span_count_B": "2", "exact_integer": "9007199254740993",
                                  "histogram_buckets": ["1", "2", "3"], "outside_distinct": 5,
                                  "baseline_ratio": "2", "threshold": "20", "minimum_samples": 5})

    def resource(owner):
        return {"attributes": [{"key": "bizigo.source_key", "value": {"stringValue": names[owner]}},
            {"key": "service.name", "value": {"stringValue": "s04-service"}},
            {"key": "test.run", "value": {"stringValue": nonce}},
            {"key": "owner_group", "value": {"stringValue": "forged-admin"}}]}

    metric_groups, trace_groups = [], []
    for owner in names:
        points = [{"timeUnixNano": str(start + (offset + i) * 1_000_000_000), "asInt": str(value)}
                  for offset, value, size in ([(1, 10, 5), (11, 20, 5)] if owner == "A" else [(11, 999, 3 if owner == "B" else 1)])
                  for i in range(size)]
        metrics = [{"name": "s04.metric", "unit": "ms", "gauge": {"dataPoints": points}}]
        if owner == "A":
            metrics += [{"name": "s04.exact", "unit": "bytes", "gauge": {"dataPoints": [
                {"timeUnixNano": str(start + 12_000_000_001), "asInt": "9007199254740993"}]}},
                {"name": "s04.histogram", "unit": "ms", "histogram": {"aggregationTemporality": 2, "dataPoints": [
                    {"timeUnixNano": str(start + 12_000_000_002), "count": "6", "sum": 11,
                     "explicitBounds": [1, 3], "bucketCounts": ["1", "2", "3"]}]}}]
        metric_groups.append({"resource": resource(owner), "scopeMetrics": [{"metrics": metrics}]})
        spans = [{"traceId": trace_ids[owner], "spanId": f"{i+1:016x}", "name": "operation", "kind": 2,
                  "parentSpanId": "" if i == 0 else "0000000000000001", "startTimeUnixNano": str(start + 12_000_000_000 + i),
                  "endTimeUnixNano": str(start + 12_000_000_100 + i), "status": {"code": 2},
                  "events": [{"name": "observed", "timeUnixNano": str(start + 12_000_000_000 + i)}],
                  "links": [] if i == 0 else [{"traceId": trace_ids[owner], "spanId": "0000000000000001"}]}
                 for i in range(1 if owner == "unknown" else 2)]
        trace_groups.append({"resource": resource(owner), "scopeSpans": [{"spans": spans}]})
    for signal, fixture in {"metrics": {"resourceMetrics": metric_groups}, "traces": {"resourceSpans": trace_groups}}.items():
        save("producer-" + signal, fixture)
        status, _, _ = http(session["collector"] + "/v1/" + signal, json.dumps(fixture).encode(), {"Content-Type": "application/json"})
        assert status == 200, "Collector rejected producer"
    window = f"?from_nano={start}&to_nano={start + 20_000_000_000}"
    def scoped_query(owner): return window + "&resource_id=" + urllib.parse.quote(names[owner])
    for signal, count in (("metrics", 12), ("traces", 2)):
        wait_for(lambda: request("/v1/" + signal + "/count" + scoped_query("A")).get("count") == str(count))
    verify_raw(session, output, nonce)
    cases.append("Collector-to-production-ingest-and-read")
    metrics = request("/v1/metrics" + scoped_query("A"))["records"]
    exact = next(r for r in metrics if r["metric"]["name"] == "s04.exact")
    assert exact["metric"]["data_points"][0]["value"]["value"]["value"] == "9007199254740993"
    histogram = next(r for r in metrics if r["metric"]["name"] == "s04.histogram")
    assert histogram["metric"]["data_points"][0]["bucket_counts"] == ["1", "2", "3"]
    trace = trace_ids["A"]
    paths = ["/v1/metrics", "/v1/metrics/count", "/v1/metrics/summary", "/v1/metrics/feed",
             "/v1/metrics/points/" + urllib.parse.quote(exact["logical_id"], safe=""),
             "/v1/traces", "/v1/traces/count", "/v1/traces/summary", "/v1/traces/feed",
             "/v1/traces/" + trace, "/v1/traces/" + trace + "/spans/0000000000000002"]
    for index, path in enumerate(paths, 1):
        active_case = f"V2-H11-{index:02}"
        query = window if "/points/" in path else scoped_query("A")
        request(path + query, owner=None, expected=(401,))
        request(path + query, owner="ingest", expected=(403,))
        request(path + query)
        detail = "/points/" in path or path.startswith("/v1/traces/" + trace)
        denied = request(path + query, owner="B", expected=(404,) if detail else (200,))
        if not detail:
            if path.endswith(("/count", "/feed")): assert denied["count"] == "0"
            elif path.endswith("/summary"): assert denied["groups"] == []
            else: assert denied["records"] == []
        case_results[active_case] = {"status": "passed", "happy": 200, "scope": 404 if detail else "empty",
                                     "anonymous": 401, "ingest_only": 403}
    span = request(paths[-1] + window)["span"]
    assert span["parent_span_id"] == "0000000000000001" and len(span["events"]) == len(span["links"]) == 1
    cases.append("eleven-real-REST-auth-and-typed-parity")
    assert request("/v1/metrics" + scoped_query("B"))["records"] == []
    assert request("/v1/metrics/count" + scoped_query("B"), owner="B")["count"] == "3"
    assert request("/v1/metrics/count" + scoped_query("unknown"), owner="admin")["count"] == "1"
    assert request("/v1/metrics" + scoped_query("unknown"))["records"] == []
    request("/v1/traces/" + trace_ids["B"] + window, expected=(404,))
    cases.append("scoped-A-B-unknown-and-hidden-detail")

    def iso(nano): return datetime.datetime.fromtimestamp(nano // 1_000_000_000, datetime.timezone.utc).isoformat()
    active_case = "V2-E-live"
    rca = {"from": iso(start + 10_000_000_000), "to": iso(start + 20_000_000_000),
           "baseline_from": iso(start), "baseline_to": iso(start + 10_000_000_000), "source_ids": [names["A"], names["B"]]}
    report = request("/v1/rca/", rca, expected=(201,))
    save("bundle-response", report)
    assert report["out_of_scope_count"] == 5 and report["excluded_input_records"]["measured"]
    providers = {p["provider_id"]: p for p in report["providers"]}
    for identity in ("metrics.baseline", "metrics.threshold", "traces.error-propagation", "traces.service-dependency"):
        assert providers[identity]["status"] not in ("failed", "not_registered")
        assert providers[identity]["telemetry"] is not None
    assert providers["metrics.baseline"]["item_count"] == providers["metrics.threshold"]["item_count"] == 1
    assert any(d["state"] == "NoRule" for d in providers["metrics.threshold"]["telemetry"]["decisions"])
    assert providers["traces.error-propagation"]["item_count"] == 1
    cases.append("four-provider-decisions-and-distinct-five")
    case_results["V2-M-live"] = {"status": "passed", "oracle": "five10→five20; inclusive2x; scopedexplicit20; NoRule"}
    case_results["V2-T-live"] = {"status": "passed", "oracle": "twoERRORspans; parent and link preserved; one error path"}
    case_results["V2-E-live"] = {"status": "passed", "oracle": "four registered providers; canonicaloutside5"}
    bundle = report["bundle_id"]
    active_case = "V2-G-review-live"
    reopened = request("/v1/rca/" + bundle)
    assert reopened["content_hash"] == report["content_hash"] and reopened["providers"] == report["providers"]
    request("/v1/rca/" + bundle, owner="B", expected=(404,))
    request("/v1/rca/" + bundle + "/review", {"verdict": "correct", "missing_evidence_kinds": ["Trace", "Metric"]}, expected=(201,))
    reviewed = request("/v1/rca/" + bundle)["review"]
    assert reviewed["schema_version"] == 3 and reviewed["missing_evidence_asked"]
    assert reviewed["missing_evidence_kinds"] == ["Metric", "Trace"]
    markdown = request("/v1/rca/" + bundle + "/export")
    (output / "bundle-export.md").write_text(markdown)
    assert "missing_evidence" in markdown
    quality = request("/v1/rca/quality")
    save("quality", quality)
    persisted = control("/evidence/" + bundle)["persisted"]
    save("persisted-pg", persisted)
    assert len(persisted["reviews"]) == 1 and persisted["reviews"][0]["missing_evidence_kinds"] == ["Metric", "Trace"]
    assert persisted["reviews"][0]["schema_version"] == 3
    audit = persisted["audit"]
    assert any(a["action"] == "telemetry.Metrics.excluded-inputs" for a in audit)
    assert any(a["action"] == "telemetry.Traces.excluded-inputs" for a in audit)
    save("audit-rows", audit)
    save("bundle-persisted", persisted["bundle"])
    model = subprocess.check_output([old.helpers.DOTNET, str(ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"),
        "--evidence-summary", str(output / "bundle-persisted.json")], cwd=ROOT)
    (output / "model-input.json").write_bytes(model)
    assert all(identity.encode() in model for identity in ("metrics.baseline", "metrics.threshold", "traces.error-propagation", "traces.service-dependency"))
    case_results["V2-G-review-live"] = {"status": "passed", "oracle": "nonempty schema3, PG/reopen/export"}
    case_results["V2-G-close-matrix"] = {"status": "not_run", "integration": "GoldenReviewEvidenceKindsIntegrationTests; /tmp/bizigo-s04/db-oracles/oracles.trx"}
    case_results["V2-G-review-null-empty-quality"] = {"status": "not_run", "integration": "Golden_missing_kind_fixed_denominator_through_both_routes; Review_capture_unanswered_is_not_measured"}
    case_results["V2-E-fault-matrix"] = {"status": "not_run", "integration": "TelemetryApiIntegrationTests + MetricTraceEvidenceContractTests"}
    case_results["V2-M-full-numeric-matrix"] = {"status": "not_run", "integration": "MetricTraceEvidenceContractTests + MetricEvidenceProviderTests"}
    case_results["V2-T-full-graph-budget-matrix"] = {"status": "not_run", "integration": "TraceEvidenceProviderTests"}
    cases.append("persistent-bundle-and-golden-review")
    active_case = "archive-replay"
    control("/replay")
    assert request("/v1/metrics/count" + scoped_query("A"))["count"] == "12"
    assert request("/v1/traces/count" + scoped_query("A"))["count"] == "2"
    assert request("/v1/rca/" + bundle)["providers"] == report["providers"]
    cases.append("archive-replay-does-not-duplicate-or-recompute-bundle")
    after = owner_module.snapshot(); save("source-after", after)
    assert before == after, "source/binary changed during independent probe"
    save("result", {"status": "PASS", "cases": cases, "case_results": case_results,
                    "http_requests": calls, "nonce": nonce, "sources": names,
                    "scope": "live subset only; explicit not_run cases require separate integration/unit evidence"})
    save("sha256", {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.iterdir()) if p.is_file()})
    print(f"PASS {len(cases)}/{len(cases)} production telemetry evidence cases; {calls} HTTP requests")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        import sys
        if "--evidence-dir" in sys.argv:
            path = Path(sys.argv[sys.argv.index("--evidence-dir") + 1])
            if path.is_dir() and not (path / "result.json").exists():
                (path / "result.json").write_text(json.dumps({"status": "FAIL", "error": str(error), "remaining": "not_run"}))
        raise
