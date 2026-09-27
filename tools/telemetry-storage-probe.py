#!/usr/bin/env python3
"""Independent HTTP/data oracle against Planner's owned live session; no Docker.

Each invocation creates its own nonce, source writes, producer payloads, scoped
queries and evidence. The query adapter delegates to production IScopedQuery.
"""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.parse
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("otlp_probe", ROOT / "tools/otlp-collector-probe.py")
old = importlib.util.module_from_spec(spec)
spec.loader.exec_module(old)
http, wait_for = old.http, old.wait_for


def contains(actual, expected):
    """Wire oracle: check every sender leaf field, allowing Collector defaults.

    Protobuf JSON can represent integral scalar values as strings or numbers;
    compare those exactly as decimal integers, never via floating point.
    """
    if isinstance(expected, dict):
        assert isinstance(actual, dict), (actual, expected)
        for key, value in expected.items():
            assert key in actual, (key, actual)
            contains(actual[key], value)
    elif isinstance(expected, list):
        assert len(actual) == len(expected), (actual, expected)
        for left, right in zip(actual, expected):
            contains(left, right)
    elif isinstance(expected, str) and expected.lstrip("-").isdigit() and isinstance(actual, (str, int)):
        assert int(actual) == int(expected), (actual, expected)
    else:
        assert actual == expected, (actual, expected)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    args = parser.parse_args()
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    session = json.loads(args.session.read_text())
    output = args.evidence_dir.resolve(); output.mkdir(parents=True, exist_ok=True)
    assert not list(output.iterdir()), "independent evidence directory must be empty"
    nonce = uuid.uuid4().hex; trace_id = uuid.uuid4().hex
    cases, serial = [], 0
    endpoint = session["telemetry_query"]
    def evidence(name, value):
        (output / (name + ".json")).write_text(json.dumps(value, indent=2))
    def request(path, body=None, owner="admin", expected=200):
        nonlocal serial
        data = json.dumps(body).encode() if body is not None else None
        status, returned, _ = http(endpoint + path, data, {"Authorization": "Bearer " + session["query_tokens"][owner], "Content-Type": "application/json"}, timeout=120)
        serial += 1
        decoded = json.loads(returned) if returned else None
        evidence(f"http-{serial:03}", {"path": path, "owner": owner, "request": body, "status": status, "response": decoded})
        assert status in (expected if isinstance(expected, tuple) else (expected,)), (path, status, returned)
        return decoded
    def control(path):
        status, body, _ = http(session["control"] + path, b"", {"Authorization": "Bearer " + session["control_secret"]}, timeout=180)
        assert status == 200, (path, status, body)
        return json.loads(body)
    def source(name, owner):
        request("/v1/sources/", {"source_id": name, "owner_group": owner}, expected=(200, 201))
    names = {owner: nonce + "-" + owner for owner in ("A", "B", "unknown")}
    prior_audit = request("/fixture/audit?" + urllib.parse.urlencode({"subject": session["query_subjects"]["A"]}))
    audit_after = max((row["id"] for row in prior_audit), default=0)
    source(names["A"], "A"); source(names["B"], "B")
    timestamp = time.time_ns()
    def resource(name):
        return {"attributes": [
            {"key": "bizigo.source_key", "value": {"stringValue": name}},
            {"key": "service.name", "value": {"stringValue": "service-" + nonce}},
            {"key": "test.run", "value": {"stringValue": nonce}},
            {"key": "owner_group", "value": {"stringValue": "forged-admin"}}]}
    exemplar = {"asInt": "9007199254740993", "timeUnixNano": str(timestamp), "traceId": trace_id, "spanId": "0123456789abcdef"}
    point = {"asInt": "-9223372036854775808", "timeUnixNano": str(timestamp), "startTimeUnixNano": str(timestamp - 1),
             "attributes": [{"key": "bytes", "value": {"bytesValue": "AP8="}}], "exemplars": [exemplar]}
    metrics = [
        {"name": "same", "unit": "bytes", "gauge": {"dataPoints": [point]}},
        {"name": "same", "unit": "count", "sum": {"aggregationTemporality": 1, "isMonotonic": True, "dataPoints": [point]}},
        {"name": "same", "unit": "ms", "histogram": {"aggregationTemporality": 2, "dataPoints": [{"timeUnixNano": str(timestamp),
            "count": "6", "sum": 11, "min": 0, "max": 8, "explicitBounds": [1, 3], "bucketCounts": ["1", "2", "3"], "exemplars": [exemplar]}]}},
        {"name": "same", "unit": "ms", "exponentialHistogram": {"aggregationTemporality": 1, "dataPoints": [{"timeUnixNano": str(timestamp),
            "count": "10", "sum": 1, "scale": -2, "zeroCount": "1", "zeroThreshold": .001,
            "positive": {"offset": -3, "bucketCounts": ["2", "3"]}, "negative": {"offset": 2, "bucketCounts": ["4"]}}]}},
        {"name": "same", "unit": "s", "summary": {"dataPoints": [{"timeUnixNano": str(timestamp), "count": "4", "sum": 21,
            "quantileValues": [{"quantile": .5, "value": 8}]}]}},
    ]
    span = {"name": "operation", "traceId": trace_id, "spanId": "0123456789abcdef", "parentSpanId": "9999999999999999",
        "traceState": "vendor=state", "kind": 2, "flags": 769, "startTimeUnixNano": str(timestamp), "endTimeUnixNano": str(timestamp + 100),
        "attributes": [{"key": "nested", "value": {"arrayValue": {"values": [{"intValue": "9223372036854775807"}, {"boolValue": True}]}}}],
        "events": [{"name": "event", "timeUnixNano": str(timestamp + 1), "droppedAttributesCount": 2}],
        "links": [{"traceId": trace_id, "spanId": "9999999999999999", "traceState": "link=value", "flags": 257}],
        "status": {"code": 2, "message": "expected"}, "droppedAttributesCount": 2, "droppedEventsCount": 3, "droppedLinksCount": 4}
    fixtures = {
        "Metrics": {"resourceMetrics": [{"resource": resource(name), "scopeMetrics": [{"metrics": metrics}]} for name in names.values()]},
        "Traces": {"resourceSpans": [{"resource": resource(name), "scopeSpans": [{"spans": [span]}]} for name in names.values()]},
    }
    for signal, fixture in fixtures.items():
        evidence("producer-" + signal, fixture)
        status, body, _ = http(session["collector"] + "/v1/" + signal.lower(), json.dumps(fixture).encode(), {"Content-Type": "application/json"})
        assert status == 200, body
    def query(signal, name=None, **extra):
        return {"signal": signal, "from_nano": timestamp - 10000000000, "to_nano": timestamp + 300000000000,
                "resource_id": name, "limit": 100, **extra}
    def count_ready(signal, owner, name, count):
        result = request("/fixture/query/count", query(signal, name), owner)
        return result if result.get("status") == "Data" and result.get("count") == count else None
    for owner, name in names.items():
        scope = "_unassigned" if owner == "unknown" else owner
        for signal, count in (("Metrics", 5), ("Traces", 1)):
            wait_for(lambda: count_ready(signal, scope, name, count), timeout=120)
            page = request("/fixture/query/list", query(signal, name), scope)
            assert len(page["records"]) == count and not page["partial"]
            for row in page["records"]:
                assert row["owner"]["owner_group"] == scope
                if signal == "Traces": contains(row["span"], span)
                else:
                    kind = next(key for key in ("gauge", "sum", "histogram", "exponentialHistogram", "summary") if key in row["metric"])
                    contains(row["metric"], next(m for m in metrics if kind in m))
            summary = request("/fixture/query/summary", query(signal, name), scope)
            assert len(summary["groups"]) == count and all(g["count"] == 1 for g in summary["groups"])
    cases.append("collector-full-typed-five-metrics-and-span")
    a_trace = request("/fixture/query/trace", query("Traces", trace_id=trace_id), "A")
    assert len(a_trace["records"]) == 1 and a_trace["records"][0]["owner"]["source_id"] == names["A"]
    narrowed = request("/fixture/query/list", query("Metrics", names["B"], owner_groups=["B"]), "A")
    assert not narrowed["records"]
    outside = request("/fixture/query/outside-count", query("Metrics", names["B"]), "A")
    assert outside["count"] == 5
    cases.append("scoped-list-detail-summary-outside-intersection")

    # Independent API ingress bytes from the transparent capture, not producer
    # bytes (the Collector transforms wire encoding) and not WAL self-comparison.
    captured = {json.loads(p.read_text())["sha256"]: p for p in Path(session["captures"]).glob("*.json") if json.loads(p.read_text())["status"] == 200}
    observed = 0
    for path in Path(session["signals"]).joinpath("manifests").glob("*.json"):
        manifest = json.loads(path.read_text())
        content = old.s3_get(session["s3"], manifest["object_key"])
        assert hashlib.sha256(content).hexdigest() == manifest["object_sha256"]
        archive_path = output / (path.stem + ".archive"); archive_path.write_bytes(content)
        raw = subprocess.check_output([old.helpers.DOTNET, str(ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"), "--read-archive", str(archive_path), str(manifest["envelope_length"])], cwd=ROOT)
        envelope = json.loads(raw)
        if not any(b["source_id"] in names.values() for b in envelope.get("owner_bindings", [])):
            continue
        capture = json.loads(captured[envelope["payload_sha256"]].read_text())
        ingress = (Path(session["captures"]) / capture["payload"]).read_bytes()
        assert ingress == base64.b64decode(envelope["payload"])
        assert hashlib.sha256(ingress).hexdigest() == envelope["payload_sha256"]
        evidence(path.stem + "-envelope", envelope); evidence(path.stem + "-manifest", manifest); evidence(path.stem + "-capture", capture)
        (output / (path.stem + "-ingress.bin")).write_bytes(ingress); observed += 1
    assert observed >= 2
    cases.append("independent-ingress-archive-owner-hash")

    source(names["A"], "B"); source(names["unknown"], "B")
    control("/replay")
    assert request("/fixture/query/count", query("Metrics", names["A"]), "A")["count"] == 5
    assert request("/fixture/query/count", query("Metrics", names["A"]), "B")["count"] == 0
    assert request("/fixture/query/count", query("Metrics", names["unknown"]), "_unassigned")["count"] == 5
    cases.append("production-replay-preserves-historical-and-unassigned")

    # Actual child process dies after ClickHouse acknowledgement and before its
    # local processed checkpoint. Production API/Collector proof above is separate.
    crash_source = nonce + "-crash"; source(crash_source, "A")
    crash_time = time.time_ns(); crash_span = dict(span, startTimeUnixNano=str(crash_time), endTimeUnixNano=str(crash_time + 100))
    crash_export = {"resourceSpans": [{"resource": resource(crash_source), "scopeSpans": [{"spans": [crash_span]}]}]}
    armed = control("/db-crash-arm")
    request("/v1/traces", crash_export)
    root = Path(armed["query_signals"])
    wait_for(lambda: (root / "after-telemetry-db-before-checkpoint.entered").exists())
    assert not list((root / "processed").glob("*.json"))
    crash_query = query("Traces", crash_source)
    assert request("/fixture/query/count", crash_query, "A")["count"] == 1
    old_pid = int(Path(session["query_pid_file"]).read_text())
    control("/db-crash-restart")
    try:
        os.kill(old_pid, 0)
        raise AssertionError("crashed fixture PID still alive")
    except ProcessLookupError:
        pass
    assert request("/fixture/query/count", crash_query, "A")["count"] == 1
    assert len(list((root / "processed").glob("*.json"))) == 1
    cases.append("real-child-db-ack-checkpoint-kill-recovery")
    assert request("/fixture/sweep", {})["segments"] == 0
    source(crash_source, "B")
    control("/restore-query-archive")
    assert request("/fixture/query/count", crash_query, "A")["count"] == 1
    assert request("/fixture/query/count", crash_query, "B")["count"] == 0
    cases.append("fresh-process-archive-only-restore-after-wal-retention")
    audit = [row for row in request("/fixture/audit?" + urllib.parse.urlencode({"subject": session["query_subjects"]["A"]})) if row["id"] > audit_after]
    assert audit and all("secret" not in row["details"] and "forged-admin" not in row["details"] for row in audit)
    assert {"telemetry.Metrics.search", "telemetry.Metrics.count", "telemetry.Metrics.summary", "telemetry.Traces.detail"}.issubset({row["action"] for row in audit})
    cases.append("real-postgres-query-audit")
    evidence("result", {"status": "PASS", "nonce": nonce, "cases": cases, "case_count": len(cases), "http_requests": serial})
    evidence("evidence-sha256", {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.iterdir()) if p.is_file()})
    print(f"PASS {len(cases)}/{len(cases)} telemetry storage live cases; {serial} independent HTTP requests", flush=True)


if __name__ == "__main__":
    main()
