#!/usr/bin/env python3
"""Independent evaluator: HTTP + read-only artifacts/S3; never Docker APIs."""
import argparse
import base64
import datetime
import hashlib
import hmac
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import urllib.parse
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("collector_http", ROOT / "tools/otlp-collector-smoke.py")
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)
http, wait_for = helpers.http, helpers.wait_for


def s3_get(config, key):
    path = "/" + config["bucket"] + "/" + key
    url = config["url"] + path
    host = urllib.parse.urlparse(url).netloc
    now = datetime.datetime.now(datetime.timezone.utc)
    date, stamp = now.strftime("%Y%m%d"), now.strftime("%Y%m%dT%H%M%SZ")
    payload_hash = hashlib.sha256(b"").hexdigest()
    names = "host;x-amz-content-sha256;x-amz-date"
    canonical_headers = f"host:{host}\nx-amz-content-sha256:{payload_hash}\nx-amz-date:{stamp}\n"
    canonical = "\n".join(["GET", path, "", canonical_headers, names, payload_hash])
    scope = date + "/us-east-1/s3/aws4_request"
    signing = "\n".join(["AWS4-HMAC-SHA256", stamp, scope, hashlib.sha256(canonical.encode()).hexdigest()])
    signing_key = ("AWS4" + config["secret"]).encode()
    for part in (date, "us-east-1", "s3", "aws4_request"):
        signing_key = hmac.new(signing_key, part.encode(), hashlib.sha256).digest()
    signature = hmac.new(signing_key, signing.encode(), hashlib.sha256).hexdigest()
    headers = {"x-amz-date": stamp, "x-amz-content-sha256": payload_hash,
               "Authorization": f"AWS4-HMAC-SHA256 Credential={config['access']}/{scope}, SignedHeaders={names}, Signature={signature}"}
    status, body, _ = http(url, headers=headers)
    assert status == 200, f"S3 read failed {status}"
    return body


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    subprocess.run(["bash", "tools/machine-resources.sh", "check"], cwd=ROOT, check=True)
    session = json.loads(args.session.read_text())
    output = args.evidence_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    nonce = uuid.uuid4().hex
    resource = {"attributes": [{"key": "test.run", "value": {"stringValue": nonce}},
                               {"key": "owner_group", "value": {"stringValue": "admin"}}]}
    metric = {"name": "independent.histogram", "unit": "ms", "description": nonce,
        "histogram": {"aggregationTemporality": 2, "dataPoints": [{"count": "6", "sum": 25,
            "min": 1, "max": 9, "timeUnixNano": "100", "startTimeUnixNano": "1",
            "bucketCounts": ["1", "2", "3"], "explicitBounds": [2, 4], "flags": 1,
            "exemplars": [{"asDouble": 3.5, "timeUnixNano": "99", "traceId": "11111111111111111111111111111111", "spanId": "2222222222222222"}]}]}}
    span = {"name": "independent.span", "traceId": "11111111111111111111111111111111", "spanId": "2222222222222222",
        "parentSpanId": "3333333333333333", "traceState": "vendor=value", "kind": 2, "flags": 257,
        "startTimeUnixNano": "100", "endTimeUnixNano": "200", "droppedAttributesCount": 3,
        "events": [{"name": "event", "timeUnixNano": "150", "droppedAttributesCount": 2}],
        "links": [{"traceId": "44444444444444444444444444444444", "spanId": "5555555555555555", "flags": 1}],
        "status": {"code": 2, "message": "expected failure"}}
    fixtures = {
        "metrics": {"resourceMetrics": [{"resource": resource, "scopeMetrics": [{"metrics": [metric]}]}]},
        "traces": {"resourceSpans": [{"resource": resource, "scopeSpans": [{"spans": [span]}]}]},
        "logs": {"resourceLogs": [{"resource": resource, "scopeLogs": [{"logRecords": [
            {"body": {"stringValue": "collector-log-" + nonce}}, {"body": {"stringValue": "collector-log-second-" + nonce}}]}]}]},
    }
    for name, fixture in fixtures.items():
        (output / (nonce + "-" + name + "-producer.json")).write_text(json.dumps(fixture, indent=2))

    def control(path):
        status, body, _ = http(session["control"] + path, b"",
            {"Authorization": "Bearer " + session["control_secret"]}, timeout=180)
        assert status == 200, (path, status, body)
    def send():
        for name, fixture in fixtures.items():
            status, body, _ = http(session["collector"] + "/v1/" + name, json.dumps(fixture).encode(), {"Content-Type": "application/json"})
            assert status == 200, (name, status, body)
    signal_root = Path(session["signals"])
    def matching_outputs():
        results = []
        for path in (signal_root / "processed").glob("*.json"):
            item = json.loads(path.read_text())
            leaves = [leaf for leaf in item["leaves"] if any(a.get("key") == "test.run" and a.get("value", {}).get("stringValue") == nonce
                      for a in leaf["resource"].get("attributes", []))]
            if leaves:
                results.append((item, leaves))
        return results
    def complete():
        results = matching_outputs()
        signals = {item["signal"] for item, leaves in results if leaves}
        return results if signals == {"Metrics", "Traces"} else None

    # Force real API admission limits, then let the shipped collector retry.
    prior = {p.name for p in Path(session["captures"]).glob("*.json")}
    control("/backpressure")
    try:
        send()
        wait_for(lambda: any(json.loads(p.read_text())["status"] == 503
                            for p in Path(session["captures"]).glob("*.json") if p.name not in prior))
        assert not matching_outputs(), "backpressure must not admit the request"
    finally:
        control("/restart")
    results = wait_for(complete, timeout=180)
    expected_ids = {leaf["logical_id"] for _, leaves in results for leaf in leaves}
    assert len(expected_ids) == 2, "same collector export must not duplicate logical replay"
    actual_metrics = [leaf["metric"] for _, leaves in results for leaf in leaves if leaf["metric"]]
    actual_spans = [leaf["span"] for _, leaves in results for leaf in leaves if leaf["span"]]
    assert actual_metrics == [metric], (actual_metrics, metric)
    assert actual_spans == [span], (actual_spans, span)
    assert all(leaf["owner_group"] == "_unassigned" for _, leaves in results for leaf in leaves)

    captures = [json.loads(path.read_text()) for path in Path(session["captures"]).glob("*.json")]
    observed = {entry["sha256"]: entry for entry in captures if entry["status"] == 200}
    for item, _ in results:
        manifest = json.loads((signal_root / "manifests" / (item["envelope_id"] + ".json")).read_text())
        compressed = s3_get(session["s3"], manifest["object_key"])
        assert hashlib.sha256(compressed).hexdigest() == manifest["object_sha256"]
        saved = output / (item["envelope_id"] + ".zst")
        saved.write_bytes(compressed)
        decoded = subprocess.check_output([helpers.DOTNET, str(ROOT / "sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"),
            "--read-archive", str(saved), str(manifest["envelope_length"])], cwd=ROOT)
        envelope = json.loads(decoded)
        payload = base64.b64decode(envelope["payload"])
        raw_hash = hashlib.sha256(payload).hexdigest()
        assert raw_hash == envelope["payload_sha256"] == item["payload_sha256"]
        capture = observed[raw_hash]
        assert capture["authorization_present"] and capture["path"] == "/v1/" + item["signal"].lower()
        assert payload == (Path(session["captures"]) / capture["payload"]).read_bytes()
        assert envelope["content_type"] == capture["content_type"].split(";")[0]
        assert envelope["received_at"] == item["received_at"]
        (output / (item["envelope_id"] + "-envelope.json")).write_bytes(decoded)
    basic = base64.b64encode((session["ch_user"] + ":" + session["ch_password"]).encode()).decode()
    def logs_delivered():
        sql = "SELECT count() FROM bizigo.events WHERE body LIKE '%" + nonce + "%'"
        status, body, _ = http(session["clickhouse"], sql.encode(), {"Authorization": "Basic " + basic})
        return status == 200 and int(body) >= 2
    wait_for(logs_delivered)
    control("/restart")
    control("/replay")
    control("/replay")
    after = wait_for(complete)
    assert {leaf["logical_id"] for _, leaves in after for leaf in leaves} == expected_ids
    assert len([leaf for _, leaves in after for leaf in leaves]) == 2
    assert logs_delivered()
    report = {"status": "PASS", "nonce": nonce, "cases": 8, "logical_ids": sorted(expected_ids),
              "collector": session["collector_image"], "raw_capture_oracle": True, "s3_readback": True,
              "typed_fixture_parity": True, "oauth2": True, "real_api_503_retry": True,
              "restart_and_repeated_replay": True, "existing_logs_delivered": True}
    (output / (nonce + "-result.json")).write_text(json.dumps(report, indent=2))
    print(f"PASS independent Collector HTTP/raw/S3/replay: 8 cases; evidence {output}")


if __name__ == "__main__":
    main()
