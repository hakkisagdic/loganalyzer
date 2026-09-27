#!/usr/bin/env python3
"""Planner-only: compiled, isolated DB mutations with named TRX red/green controls.

No compiler failure, test infrastructure crash, skipped/zero test or source
mutation alone counts as a kill. Source tree is never edited. Container lifecycle
belongs to the existing DevStackFixture; Planner owns the serial resource claim.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("mutation_helpers", ROOT / "tools/capacity-mutation-gate.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
GREEN = "Known_inventory_without_payload_claim_remains_visible"
CURRENT = "Single_and_CSV_current_source_writes_remain_functional"
SINK = "src/Bizigo.Storage.ClickHouse/TelemetryWriter.cs"
READER = "src/Bizigo.Storage.ClickHouse/TelemetryReader.cs"
INGEST = "src/Bizigo.Ingest/Otlp/SignalIngest.cs"
MATERIALIZE = "src/Bizigo.Ingest/Otlp/TelemetryMaterializer.cs"


def patch(path, before, after, occurrence=None):
    return {"path": path, "before": before, "after": after, "occurrence": occurrence}


def mutation(name, changes, red, green=GREEN, project="IntegrationTests"):
    return {"name": name, "changes": changes, "red": red, "green": green, "project": project}


MUTANTS = [
    mutation("M01-payload-owner-trust", [patch(SINK,
        'return [record.Owner.OwnerGroup, record.Name,',
        '''var claimed = record.Resource.GetProperty("attributes").EnumerateArray().FirstOrDefault(a => a.GetProperty("key").GetString() == "owner_group");
        var owner = claimed.ValueKind == JsonValueKind.Undefined ? record.Owner.OwnerGroup : claimed.GetProperty("value").GetProperty("stringValue").GetString()!;
        return [owner, record.Name,''')], "Payload_owner_claim_and_unassigned_never_widen_authoritative_scope"),
    mutation("M02-reader-without-scope", [patch(READER, "var scopeSql = scope.ToSqlFragment();", 'var scopeSql = "1";')],
        "Payload_owner_claim_and_unassigned_never_widen_authoritative_scope"),
    mutation("M03-metric-buckets", [patch(MATERIALIZE, "var metric = leaf.Metric;",
        "var metric = leaf.Metric;\n        if (metric?.Histogram is not null) metric.Histogram.DataPoints[0].BucketCounts.Clear();")],
        "Complete_sender_typed_fields_roundtrip_and_replay_without_merge"),
    mutation("M04-trace-context", [patch(MATERIALIZE, "var metric = leaf.Metric;",
        "var metric = leaf.Metric;\n        if (leaf.Span is not null) { leaf.Span.ParentSpanId = ByteString.Empty; leaf.Span.Events.Clear(); leaf.Span.Links.Clear(); }")],
        "Complete_sender_typed_fields_roundtrip_and_replay_without_merge"),
    mutation("M05-query-no-logical-dedup", [patch(READER, 'FROM {table} FINAL WHERE', 'FROM {table} WHERE')],
        "Replay_duplicates_are_reduced_before_merge"),
    mutation("M06-replay-current-owner", [
        patch(INGEST, "await WriteResultAsync(bound, decoded, token);", "await WriteResultAsync(bound, decoded, token, true);", 1),
        patch(INGEST, "private async Task WriteResultAsync(RawSignalEnvelope envelope, TelemetryDecode decoded, CancellationToken token)",
              "private async Task WriteResultAsync(RawSignalEnvelope envelope, TelemetryDecode decoded, CancellationToken token, bool replay = false)"),
        patch(INGEST, "var source = envelope.OwnerBindings!.Single(b => b.LeafKey == leaf.Key);",
              "var source = replay ? (await owners.ResolveAsync([TelemetryMaterializer.Ownership(leaf) with { EventTimeUnixNano = ulong.MaxValue - 1 }], token))[0] with { EventTimeUnixNano = TelemetryMaterializer.Time(leaf) } : envelope.OwnerBindings!.Single(b => b.LeafKey == leaf.Key);")],
        "Archive_restore_after_wal_retention_preserves_A_and_unassigned"),
    mutation("M07-database-error-zero", [patch(READER,
        'catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, null, Failure(ex)); }',
        'catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Empty, 0); }', 0)],
        "Failed_empty_never_fed_and_all_query_audit_outcomes_are_distinct"),
    mutation("M08-audit-hook", [patch("src/Bizigo.Query/ScopedQuery.Telemetry.cs",
        '''await _audit.RecordAsync(new AuditRecord(scope.Subject, "telemetry." + signal + "." + action,
                signal.ToString(), Describe(scope, predicate), summary + ";outcome=" + outcome + ";partial=" + partial,
                count, (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue), succeeded), auditBudget.Token);''',
        '''_ = new AuditRecord(scope.Subject, "telemetry." + signal + "." + action,
                signal.ToString(), Describe(scope, predicate), summary + ";outcome=" + outcome + ";partial=" + partial,
                count, (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue), succeeded);
            await Task.CompletedTask;''')], "Failed_empty_never_fed_and_all_query_audit_outcomes_are_distinct"),
    *[mutation("M09-history-" + label, [
        patch("src/Bizigo.ControlPlane/ControlPlaneDbContext.cs", "public override async Task<int> SaveChangesAsync(",
            "public Task<int> SaveWithoutOwnershipAsync(CancellationToken token) => base.SaveChangesAsync(true, token);\n\n    public override async Task<int> SaveChangesAsync("),
        patch("src/Bizigo.Api/SourcesEndpoints.cs", "try { await db.SaveChangesAsync(cancellationToken); }",
            "try { await db.SaveWithoutOwnershipAsync(cancellationToken); }", ordinal)],
        "Single_upsert_and_csv_commit_source_and_history_atomically", CURRENT)
      for ordinal, label in enumerate(("single", "csv"))],
    mutation("M10-logical-ttl", [patch(READER, '"expires_nano > {as_of:UInt64}"', '"1"')],
        "Expired_physical_rows_are_hidden_before_ttl_materialization"),
    mutation("M11-lookup-error-as-unknown", [patch(INGEST,
        '{ SetFailure(ex); return Full("Authoritative ownership history is unavailable."); }',
        '{ SetFailure(ex); resolved = decoded.Accepted.Select(leaf => new TelemetryOwnerBinding(leaf.Key, TelemetryMaterializer.Ownership(leaf).Candidates.FirstOrDefault() ?? "_unknown", "_unassigned", 0, TelemetryMaterializer.Time(leaf), "unknown")).ToArray(); }')],
        "History_failure_rejects_before_wal_and_recovers", "Healthy_missing_history_is_durable_unassigned", "UnitTests"),
]


def change(text, item):
    before, after, occurrence = item["before"], item["after"], item["occurrence"]
    count = text.count(before)
    assert count == 1 if occurrence is None else count > occurrence, (item["path"], count, occurrence)
    if occurrence is None:
        return text.replace(before, after, 1)
    position, start = -1, 0
    for _ in range(occurrence + 1):
        position = text.index(before, start); start = position + len(before)
    return text[:position] + after + text[position + len(before):]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence-dir", required=True, type=Path)
    parser.add_argument("--only", choices=[m["name"] for m in MUTANTS])
    args = parser.parse_args(); evidence = args.evidence_dir.resolve(); evidence.mkdir(parents=True, exist_ok=True)
    assert not list(evidence.iterdir()), "new evidence directory required"
    selected = [m for m in MUTANTS if args.only is None or m["name"] == args.only]
    helper.resources()
    with tempfile.TemporaryDirectory(prefix="telemetry-storage-mutants-") as temporary:
        copy = Path(temporary) / "repo"; copy.mkdir()
        names = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
        source_hashes = {}
        for name in dict.fromkeys(names):
            source = ROOT / name
            if not name or name.startswith("graphify-out/") or not source.is_file():
                continue
            target = copy / name; target.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(source, target)
            source_hashes[name] = hashlib.sha256(source.read_bytes()).hexdigest()
        (evidence / "source-sha256.json").write_text(json.dumps(source_hashes, indent=2))
        env = os.environ | {"GIT_DIR": str(ROOT / ".git"), "GIT_WORK_TREE": str(copy), "BIZIGO_TELEMETRY_EVIDENCE": str(evidence / "db-artifacts")}
        def build(label, project):
            helper.resources()
            result = helper.execute([helper.DOTNET, "build", "tests/Bizigo." + project, "--configuration", "Release", "-m:1"], copy, env)
            (evidence / (label + "-build.log")).write_text(result.stdout + result.stderr)
            assert result.returncode == 0, "compiler failure is not kill: " + label
        def tests(label, project, test_names):
            helper.resources(); folder = evidence / label; folder.mkdir(parents=True, exist_ok=True)
            trx = folder / "result.trx"
            result = helper.execute([helper.DOTNET, "test", "tests/Bizigo." + project, "--configuration", "Release", "--no-build", "-m:1",
                "--filter", "|".join("FullyQualifiedName~" + n for n in test_names), "--logger", "trx;LogFileName=result.trx", "--results-directory", str(folder)], copy, env)
            (folder / "test.log").write_text(result.stdout + result.stderr)
            assert trx.exists(), "runner failure: missing TRX"
            tree = ET.parse(trx)
            rows = [(r.attrib["testName"], r.attrib["outcome"]) for r in tree.findall(".//t:UnitTestResult", helper.NS)]
            assert rows and all(value in ("Passed", "Failed") for _, value in rows), "zero/skip/runner is not evidence"
            return result.returncode, rows
        for project in sorted({m["project"] for m in selected}):
            build("baseline-" + project, project)
            required = sorted({m[k] for m in selected if m["project"] == project for k in ("red", "green")})
            code, rows = tests("baseline-" + project, project, required)
            assert code == 0 and all(v == "Passed" for _, v in rows)
            assert all(any(name in n for n, _ in rows) for name in required)
        for mutant in selected:
            originals = {p["path"]: (copy / p["path"]).read_text() for p in mutant["changes"]}
            try:
                for item in mutant["changes"]:
                    path = copy / item["path"]; path.write_text(change(path.read_text(), item))
                    assert item["after"] in path.read_text(), "mutation did not reach isolated source"
                (evidence / (mutant["name"] + "-patch.json")).write_text(json.dumps(mutant, indent=2))
                build(mutant["name"], mutant["project"])
                code, rows = tests(mutant["name"], mutant["project"], [mutant["red"], mutant["green"]])
                assert code != 0 and any(mutant["red"] in n and v == "Failed" for n, v in rows), "surviving mutant"
                greens = [v for n, v in rows if mutant["green"] in n]
                assert greens and all(v == "Passed" for v in greens), "positive control failed"
                failures = [n for n, v in rows if v == "Failed"]
                assert all(mutant["red"] in n for n in failures), "unexpected infrastructure failure"
                print("KILLED " + mutant["name"] + "; compiled + named TRX failure + positive control", flush=True)
            finally:
                for name, original in originals.items(): (copy / name).write_text(original)
        for project in sorted({m["project"] for m in selected}):
            build("restored-" + project, project)
            required = globals().get("RESTORE_FILTERS", {}).get(project,
                ["Telemetry", "SourceOwnershipHistory"] if project == "IntegrationTests" else ["TelemetryOwnership", "SignalDurability"])
            code, rows = tests("restored-" + project, project, required)
            assert code == 0 and all(v == "Passed" for _, v in rows)
        assert all(hashlib.sha256((ROOT / n).read_bytes()).hexdigest() == digest for n, digest in source_hashes.items()), "shared source changed during gate"
    (evidence / "result.json").write_text(json.dumps({"status": "PASS", "mutants": [m["name"] for m in selected], "restored": "PASS", "isolated_copy_removed": True}, indent=2))
    print(f"PASS {len(selected)} compiled telemetry mutations; shared tree untouched; restore green", flush=True)


if __name__ == "__main__":
    main()
