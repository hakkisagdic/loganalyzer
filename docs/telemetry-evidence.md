# Metric and trace reads and evidence

The telemetry read API and all four RCA providers use the same audited
`IScopedQuery` boundary. Event-time inventory ownership remains authoritative:
after A transfers a source to B, historical A points/spans remain visible only
to A. A resource attribute, requested owner filter, cursor or trace link cannot
grant access. Healthy unknown inventory remains `_unassigned`.

## REST reads

All routes require a reader role. Anonymous requests return 401, ingest-only
credentials return 403, and inaccessible details use the same 404 as missing
details. Backend or audit persistence failures return 503 rather than empty data.

| GET route | Result |
| --- | --- |
| `/v1/metrics`, `/v1/traces` | Typed records, bounded page, partial/cursor |
| `/v1/metrics/count`, `/v1/traces/count` | Scoped count |
| `/v1/metrics/summary`, `/v1/traces/summary` | Typed grouped summaries |
| `/v1/metrics/feed`, `/v1/traces/feed` | Scoped feed state and count |
| `/v1/metrics/points/{logicalId}` | Single typed point |
| `/v1/traces/{traceId}` | Bounded trace detail |
| `/v1/traces/{traceId}/spans/{spanId}` | Single typed span |

`from_nano` and `to_nano` are required integral decimal strings and describe a
half-open interval. List/trace/summary routes accept bounded `limit` and opaque
`cursor`; continuations are bound to the route, query and principal scope.
Unsupported filters, malformed continuations and invalid ranges fail with 4xx.
Responses retain incomplete/partial status instead of claiming complete evidence.

All integer/nanosecond wire values are decimal strings, including values above
JavaScript's exact Number range. Use `BigInt`, not `Number`, for exact arithmetic.
Optional numbers carry `present`; floating point NaN/Infinity are explicitly
tagged instead of converted to zero or JSON null. Generated OpenAPI TypeScript
types and the executable `ui/tools/telemetry-wire-contract.mjs` oracle cover this
boundary. Histogram buckets, exemplars and span parent/events/links remain typed.

## Metric policy

`MetricEvidence:MinimumSamples` defaults to 5 and `ChangeFactor` to 2. Baseline
and incident windows must have equal duration for the baseline provider. Valid
unequal windows produce `WindowMismatch` only for that provider; other providers
and explicit thresholds still run. Overlapping or future baseline windows retain
the global RCA 400 validation and never create a completed bundle.

Metric arithmetic uses exact integer/rational comparisons. Factor boundaries
are inclusive; both windows must meet the sample minimum. Missing, unsupported,
nonfinite and zero-baseline cases remain explicit decisions rather than invented
ratios. Counter reset/start-time and distribution weighting are part of the
typed arithmetic contract.

Threshold violations require an explicit enabled rule. For example:

```json
{
  "MetricEvidence": {
    "MinimumSamples": 5,
    "ChangeFactor": 2,
    "ThresholdRules": [{
      "StableId": "core-latency",
      "Version": 1,
      "MetricName": "service.latency",
      "OwnerGroups": ["network/core"],
      "Unit": "ms",
      "Statistic": "windowMean",
      "Operator": "gte",
      "Threshold": "20"
    }]
  }
}
```

Operators are `gt`, `gte`, `lt`, `lte`; the supported statistic is `windowMean`.
Thresholds are strings to preserve integer precision. Rules narrow the
principal's scope. No matching rule means `NoRule` and no violation finding.
Policy identity/version and evaluation values are retained with the bundle.

## Evidence and review semantics

The registered providers are `metrics.baseline`, `metrics.threshold`,
`traces.error-propagation` and `traces.service-dependency`. Metric and Trace are
expected evidence kinds. Feed status, evaluation status and provider registration
are separate: unavailable data is not reported as a successful empty query.
The two providers consuming the same signal do not double-count excluded input
records. An unavailable count remains null. Trace parents and observed links
remain distinguishable; bounded or incomplete input is reported explicitly.

RCA bundles persist the decisions. Reopening/export/model summaries do not rerun
the metric policy. Bundle content identity canonicalizes telemetry dictionary
keys so PostgreSQL JSONB key order does not change the hash. Decision order,
values and nulls remain meaningful. Schema1 hash behavior is preserved.

Both report review and alert closure accept `missing_evidence_kinds` with these
distinct meanings:

| Input | Persisted meaning |
| --- | --- |
| omitted or null | Unanswered, excluded from measured denominator |
| `[]` | Answered; no evidence kind missing |
| `["Metric", "Trace"]` | Answered; named kinds missing |

New reviews use schema3. Invalid/duplicate kinds fail before persistence. Legacy
reviews are not retroactively treated as answered. Missing-kind quality uses a
fixed measured-review denominator; it is separate from verdict accuracy. An
automatic destructive downgrade is not supported; export/back up the new review
fields before choosing a migration strategy.

## Verification ownership

Local unit/TypeScript/Python checks are Docker-free. The coordinator runs real
PG/ClickHouse/Keycloak/Collector tests and the DB mutation variants serially.
`telemetry-evidence-smoke.py --serve` provides the production API session;
independent `telemetry-evidence-probe.py` runs use separate nonce/evidence paths.
The session records source and binary hashes. Cleanup checks only owned
processes/containers, fails on timeout, and records elapsed time and liveness.
An external evaluator being unavailable is not a PASS.
