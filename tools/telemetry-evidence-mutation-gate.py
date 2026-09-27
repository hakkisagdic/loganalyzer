#!/usr/bin/env python3
"""24 isolated compiled variants, named red/positive/restore TRX evidence.

--mode unit is Docker-free. Planner alone runs --mode db or all. Reuses the
existing copy/compile/TRX/resource/cleanup runner; never changes shared sources.
"""
import argparse
import importlib.util
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("evidence_mutation_runner", ROOT / "tools/telemetry-storage-mutation-gate.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
p = runner.patch


def m(name, changes, red, green, db=False):
    return runner.mutation(name, changes, red, green, "IntegrationTests" if db else "UnitTests")


API = "src/Bizigo.Api/TelemetryReadEndpoints.cs"
WIRE = "src/Bizigo.Api/TelemetryDtos.cs"
READER = "src/Bizigo.Evidence/TelemetryEvidence.cs"
REVIEW = "src/Bizigo.Api/EvidenceEndpoints.cs"
CLOSE = "src/Bizigo.Api/AlertClosureEndpoints.cs"
POS = "Allowed_A_scalar_and_root_span_remain_visible"
MUTANTS = [
    *[m("N01" + letter + "-exempt-" + kind.lower(), [p("src/Bizigo.Evidence/EvidenceContract.cs",
        "new HashSet<EvidenceKind>();", "new HashSet<EvidenceKind> { EvidenceKind." + kind + " };")],
        kind + "_is_not_exempt", "Log_remains_not_exempt") for letter, kind in (("a", "Metric"), ("b", "Trace"))],
    *[m("N02" + letter + "-di-" + label, [p("src/Bizigo.Evidence/EvidenceServiceCollectionExtensions.cs",
        "services.AddScoped<IEvidenceProvider, " + cls + ">();", "// removed provider registration")],
        "Production_di_" + label + "_required", "Production_di_" + other + "_required")
      for letter, label, cls, other in (("a", "metric_baseline", "MetricBaselineProvider", "metric_threshold"),
        ("b", "metric_threshold", "MetricThresholdProvider", "metric_baseline"),
        ("c", "trace_error", "TraceErrorPropagationProvider", "trace_dependency"),
        ("d", "trace_dependency", "TraceServiceDependencyProvider", "trace_error"))],
    *[m("N03" + letter + "-rest-scope", [p(API,
        "var request = TelemetryReadRequest.Parse(http.Request.Query, signal, route, scope, traceId, spanId);",
        "if (signal == TelemetrySignal." + signal + ") scope = AccessScope.System(scope.Subject);\n            var request = TelemetryReadRequest.Parse(http.Request.Query, signal, route, scope, traceId, spanId);")],
        kind + "_rest_historical_scope", POS, True) for letter, signal, kind in (("a", "Metrics", "Metric"), ("b", "Traces", "Trace"))],
    *[m("N04" + letter + "-provider-scope", [p(READER,
        "var rows = new List<TelemetryRecord>(); var seen = new HashSet<string>(StringComparer.Ordinal);",
        "if (signal == TelemetrySignal." + signal + ") scope = AccessScope.System(scope.Subject);\n        var rows = new List<TelemetryRecord>(); var seen = new HashSet<string>(StringComparer.Ordinal);")],
        kind + "_provider_historical_scope", "Allowed_A_provider_" + positive, True)
      for letter, signal, kind, positive in (("a", "Metrics", "Metric", "series"), ("b", "Traces", "Trace", "edge"))],
    m("N05a-failed-empty", [p("src/Bizigo.Evidence/EvidenceCoverage.cs",
        "var partial = slices.Length == 0", "slices = slices.Select(s => s.Status == EvidenceStatus.Failed ? s with { Status = EvidenceStatus.Empty } : s).ToArray();\n        var partial = slices.Length == 0")],
        "Kind_failed_is_not_empty", "Two_empty_providers_are_empty"),
    m("N05b-unknown-zero", [p("src/Bizigo.Evidence/ExcludedInputRecords.cs", "public long? Total => Measured ? KnownSubtotal : null;", "public long? Total => Measured ? KnownSubtotal : 0;")],
        "Outside_failed_count_is_null", "Outside_measured_zero_is_zero"),
    m("N05c-double-count", [p("src/Bizigo.Evidence/EvidenceBundle.cs", "public long? OutOfScopeCount => ExcludedInputRecords.Total;",
        "public long? OutOfScopeCount => Slices.Sum(s => s.OutOfScopeCount);")],
        "Four_production_providers_preserve_exact_decisions_and_distinct_outside_counts", "Outside_one_provider_count_three", True),
    m("N06a-bucket-loss", [p(WIRE, 'Array(point, "bucketCounts").Select(Integer).ToArray(), Array(point, "explicitBounds")',
        'System.Array.Empty<string>(), Array(point, "explicitBounds")')], "Metric_rest_bucket_parity", "Typed_wire_integer_exemplar_attribute_and_nanos_are_decimal_strings"),
    m("N06b-parent-loss", [p(WIRE, 'Text(span, "spanId"), Text(span, "parentSpanId"),', 'Text(span, "spanId"), "",')],
        "Trace_parent_edge_parity", "Trace_link_observation_parity"),
    m("N06c-link-loss", [p(WIRE,
        'Array(span, "links").Select(l => new TelemetrySpanLinkDto(Text(l, "traceId"), Text(l, "spanId"), Text(l, "traceState"), Integer(l, "flags"), Attributes(l), Integer(l, "droppedAttributesCount"))).ToArray(),',
        'System.Array.Empty<TelemetrySpanLinkDto>(),')], "Trace_link_observation_parity", "Trace_parent_edge_parity"),
    m("N07a-exclusive-factor", [p("src/Bizigo.Evidence/MetricArithmetic.cs", "ratio.CompareTo(factor) >= 0", "ratio.CompareTo(factor) > 0"),
        p("src/Bizigo.Evidence/MetricArithmetic.cs", "ratio.CompareTo(ExactMetricNumber.Integer(1) / factor) <= 0", "ratio.CompareTo(ExactMetricNumber.Integer(1) / factor) < 0")],
        "Baseline_factor_inclusive_boundaries", "Five_samples_and_ratio_three_positive"),
    m("N07b-minimum-one", [p("src/Bizigo.Evidence/MetricArithmetic.cs", "if (count < minimumSamples)", "if (count < 1)")],
        "Each_window_requires_configured_sample_minimum", "Five_samples_and_ratio_three_positive"),
    m("N07c-no-rule-finding", [p("src/Bizigo.Evidence/Providers/MetricProviders.cs",
        'if (rules.Length == 0) return MetricProviderOutput.Unavailable(Id, current.Status, "NoRule", "NoRule", policy.Value);',
        'if (rules.Length == 0) return MetricProviderOutput.Unavailable(Id, current.Status, "NoRule", "NoRule", policy.Value) with { Items = [new("invented", Id, Kind, window.From, 1, "invented", new Dictionary<string, string>())] };')],
        "No_rule_preserves_feed_and_reports_no_evaluation", "Matching_rule_finds_violation"),
    m("N08a-old-null-measured", [p("src/Bizigo.Evidence/GoldenReviewStore.cs",
        "var measured = visible.Where(r => r.SchemaVersion >= GoldenReviewEntity.MissingEvidenceSchemaVersion\n            && r.MissingEvidenceKinds != null);", "var measured = visible;")],
        "Golden_missing_kind_fixed_denominator_through_both_routes", "Review_capture_nonempty_persists", True),
    m("N08b-review-null-empty", [p(REVIEW, "MissingEvidenceKinds: request.MissingEvidenceKinds)", "MissingEvidenceKinds: request.MissingEvidenceKinds ?? [])")],
        "Review_capture_unanswered_is_not_measured", "Review_capture_nonempty_persists", True),
    m("N08c-close-null-empty", [p(CLOSE, "request.MissingEvidenceKinds);", "request.MissingEvidenceKinds ?? []);")],
        "Close_capture_unanswered_is_not_measured", "Close_capture_nonempty_persists", True),
    m("N08d-review-answer-loss", [p(REVIEW, "MissingEvidenceKinds: request.MissingEvidenceKinds)", "MissingEvidenceKinds: null)")],
        "Review_capture_nonempty_persists", "Close_capture_nonempty_persists", True),
    m("N08e-close-answer-loss", [p(CLOSE, "request.MissingEvidenceKinds);", "null);")],
        "Close_capture_nonempty_persists", "Review_capture_nonempty_persists", True),
]
assert len(MUTANTS) == 24

if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--mode", choices=("unit", "db", "all"), default="all")
    options, remaining = parser.parse_known_args()
    runner.MUTANTS = [item for item in MUTANTS if options.mode == "all" or
                      (item["project"] == "UnitTests") == (options.mode == "unit")]
    runner.RESTORE_FILTERS = {
        "UnitTests": ["TelemetryMutationOracleTests", "MetricTraceEvidenceContractTests", "MetricEvidenceProviderTests", "TraceEvidenceProviderTests", "GoldenReviewEvidenceKindsTests", "TelemetryReadContractTests", "ConcurrentAuditTests"],
        "IntegrationTests": ["MetricTraceEvidenceIntegrationTests", "TelemetryEvidenceScopeIntegrationTests", "TelemetryApiIntegrationTests", "GoldenReviewEvidenceKindsIntegrationTests"],
    }
    sys.argv = [sys.argv[0], *remaining]
    runner.main()
