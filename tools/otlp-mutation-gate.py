#!/usr/bin/env python3
"""Compiling OTLP mutants, isolated copy, named red tests and restored green."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("capacity_gate_helpers", ROOT / "tools/capacity-mutation-gate.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
CONTROL = "Malformed_json_is_protocol_rejection"
MUTANTS = [
    ("ack-before-fsync", "src/Bizigo.Ingest/Otlp/SignalIngest.cs", "FlushToDisk = true, StrictRecovery = true,",
     "FlushToDisk = false, StrictRecovery = true,", "Real_http_ack_waits_for_fsync_and_queue_reservation"),
    ("metric-buckets", "src/Bizigo.Ingest/Otlp/OtlpTelemetryDecoder.cs", "return copy;",
     "if (copy.Histogram is not null) copy.Histogram.DataPoints[0].BucketCounts.Clear();\n        return copy;",
     "All_metric_fields_survive_typed_decode_and_replay"),
    ("trace-parent-events-links", "src/Bizigo.Ingest/Otlp/OtlpTelemetryDecoder.cs", "null, span.Clone()",
     "null, new OpenTelemetry.Proto.Trace.V1.Span { Name = span.Name, TraceId = span.TraceId, SpanId = span.SpanId }",
     "Trace_parent_events_links_and_context_survive_typed_decode"),
    ("payload-owner-trust", "src/Bizigo.Ingest/Otlp/SignalIngest.cs", '["owner_group"] = source.OwnerGroup',
     '["owner_group"] = leaf.Resource.Attributes.FirstOrDefault(a => a.Key == "owner_group")?.Value.StringValue ?? source.OwnerGroup',
     "Inventory_candidates_and_ambiguous_alias_never_trust_payload_owner"),
    ("duplicate-replay-identity", "src/Bizigo.Ingest/Otlp/SignalIngest.cs", '["logical_id"] = envelope.EnvelopeId.ToString("N")',
     '["logical_id"] = Guid.NewGuid().ToString("N")', "Restart_and_concurrent_replay_keep_identity_raw_decision_and_owner"),
    ("archive-readback-hash", "src/Bizigo.Storage.Raw/SignalArchive.cs",
     'existing = await store.GetAsync(key, token);\n            if (existing is null || RawSignalEnvelope.Hash(existing) != built.Sha256)',
     'existing = await store.GetAsync(key, token);\n            if (existing is null)',
     "Archive_verification_failure_never_commits_manifest"),
    ("integer-double-rounding", "src/Bizigo.Ingest/Otlp/OtlpJsonCodec.cs",
     "else if (incoming && field.FieldType is FieldType.Int64",
     "else if (!incoming && field.FieldType is FieldType.Int64",
     "Numeric_integer_precision_survives_HTTP_archive_and_replay"),
    ("idle-retention-disabled", "src/Bizigo.Ingest/Otlp/SignalIngestService.cs",
     "await Task.WhenAll(ConsumeAsync(stoppingToken), RetainAsync(stoppingToken));",
     "await ConsumeAsync(stoppingToken);",
     "Idle_service_reclaims_verified_expired_WAL_and_reopens_admission"),
    ("duplicate-json-unhandled", "src/Bizigo.Ingest/Otlp/OtlpJsonCodec.cs",
     "RejectDuplicateKeys(document.RootElement);",
     "// mutation: bypass duplicate-field validation",
     "Duplicate_known_and_unknown_JSON_keys_return_bounded_protocol_error"),
    ("gzip-wire-equals-expanded-limit", "src/Bizigo.Api/LogsEndpoint.cs",
     "Math.Max(65536L, limit / 8)", "0L",
     "Stored_gzip_and_identity_share_exact_expanded_HTTP_limit"),
]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence-dir", type=Path)
    parser.add_argument("--legacy-upgrade", action="store_true", help="Check legacy copy-on-write and blank candidate regressions")
    args = parser.parse_args()
    mutants = MUTANTS if not args.legacy_upgrade else [
        ("legacy-overwrite-before-manifest", "src/Bizigo.Storage.Raw/SignalArchive.cs",
         'key = $"otlp/v1/{envelope.Signal.ToString().ToLowerInvariant()}/{envelope.EnvelopeId:N}.{built.Sha256}.json.zst";',
         'key = old.ObjectKey;', "Interrupted_legacy_upgrade_preserves_original_restore_set_and_retries"),
        ("legacy-first-empty-candidate", "src/Bizigo.Ingest/Otlp/SignalIngest.cs",
         '.Candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))',
         '.Candidates.FirstOrDefault()', "Legacy_blank_candidates_use_next_nonblank_or_deterministic_unknown"),
    ]
    control = "Normal_legacy_upgrade_remains_idempotent" if args.legacy_upgrade else CONTROL
    evidence = args.evidence_dir or Path(tempfile.mkdtemp(prefix="otlp-mutation-evidence-"))
    evidence.mkdir(parents=True, exist_ok=True)
    helper.resources()
    with tempfile.TemporaryDirectory(prefix="otlp-mutants-") as temporary:
        copy = Path(temporary) / "repo"
        copy.mkdir()
        names = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
        for name in dict.fromkeys(names):
            if name and (ROOT / name).is_file():
                target = copy / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, target)
        env = os.environ | {"GIT_DIR": str(ROOT / ".git"), "GIT_WORK_TREE": str(copy)}
        build = [helper.DOTNET, "build", "tests/Bizigo.UnitTests", "--configuration", "Release", "-m:1"]
        def compile_stage(label):
            helper.resources()
            result = helper.execute(build, copy, env)
            (evidence / (label + "-build.log")).write_text(result.stdout + result.stderr)
            assert result.returncode == 0, f"compiler failure is not a kill: {label}"
        compile_stage("baseline")
        all_tests = [control] + [m[4] for m in mutants]
        code, outcomes = helper.tests(copy, all_tests, evidence / "baseline", env)
        assert code == 0 and all(outcome == "Passed" for _, outcome in outcomes)
        for label, name, before, after, red in mutants:
            path = copy / name
            original = path.read_text()
            assert original.count(before) == 1, f"stale mutation anchor: {label}"
            try:
                path.write_text(original.replace(before, after, 1))
                assert after in path.read_text()
                compile_stage(label)
                code, outcomes = helper.tests(copy, [red, control], evidence / label, env)
                assert code != 0 and any(red in test and outcome == "Failed" for test, outcome in outcomes), label
                controls = [outcome for test, outcome in outcomes if control in test]
                assert controls and all(outcome == "Passed" for outcome in controls), label
                assert all(outcome in ("Failed", "Passed") for _, outcome in outcomes), "runner error/skip is not a kill"
                print(f"KILLED {label}: named TRX failure and positive controls passed", flush=True)
            finally:
                path.write_text(original)
        compile_stage("restored")
        code, outcomes = helper.tests(copy, ["Signal", "OtlpTelemetry", "OtlpBodyRead", "LegacySignalReplayTests"], evidence / "restored", env)
        assert code == 0 and outcomes and all(outcome == "Passed" for _, outcome in outcomes)
    (evidence / "result.json").write_text(json.dumps({"mutants_killed": len(mutants), "restored": "PASS"}, indent=2))
    print(f"PASS {len(mutants)} OTLP mutants; restored suite green; common tree untouched; evidence {evidence}")


if __name__ == "__main__":
    main()
