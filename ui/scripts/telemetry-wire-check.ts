import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { components } from "../src/lib/api/schema.d.ts";

// These files are produced by the C# TelemetryWire mapper tests. The same
// representation is returned by the live HTTP probe. No JS number conversion
// participates in integer decisions.
type RecordDto = components["schemas"]["TelemetryRecordDto"];
type NumberDto = components["schemas"]["TelemetryNumberDto"];
const folder = process.argv[2];
assert.ok(folder, "usage: node --experimental-strip-types scripts/telemetry-wire-check.ts <wire-evidence-directory>");
function load(name: string): RecordDto {
  return JSON.parse(readFileSync(join(folder!, `${name}.json`), "utf8")) as RecordDto;
}
function integer(number: NumberDto | null | undefined): string {
  assert.equal(number?.kind, "int");
  assert.equal(typeof number?.value, "string");
  return number!.value as string;
}
function first<T>(items: readonly T[]): T {
  const value = items[0]; assert.notEqual(value, undefined); return value!;
}
const data = load("integer");
const point = first(data.metric!.data_points);
assert.equal(BigInt(integer(point.value.value)), 9007199254740993n);
assert.equal(BigInt(integer(first(point.attributes).value.number)), 9223372036854775807n);
assert.equal(BigInt(integer(first(point.exemplars).value.value)), 9223372036854775807n);
assert.equal(typeof point.time_unix_nano, "string");
assert.equal(BigInt(point.time_unix_nano), 18446744073709551615n);
assert.equal(BigInt(first(point.exemplars).time_unix_nano), 18446744073709551614n);
assert.equal(BigInt("18446744073709551616") - BigInt(point.time_unix_nano), 1n);
for (const [file, special] of [["NaN", "NaN"], ["Infinity", "+Infinity"], ["-Infinity", "-Infinity"]] as const) {
  const p = first(load(file).metric!.data_points);
  assert.equal(typeof p.count, "string");
  assert.equal(BigInt(p.count!), 18446744073709551615n);
  assert.equal(BigInt(first(p.bucket_counts)), 18446744073709551615n);
  assert.equal(p.sum.present, true);
  assert.equal(p.sum.value?.kind, "double");
  const sum = p.sum.value as components["schemas"]["TelemetryNumberDtoTelemetryDoubleDto"];
  assert.equal(sum.value, null); assert.equal(sum.special, special);
  assert.equal(p.min.present, true); assert.equal(p.min.value?.value, 0);
  assert.equal(typeof p.min.value?.value, "number");
  assert.equal(p.max.present, false); assert.equal(p.max.value, null);
}
const span = load("span").span!;
assert.equal(BigInt(span.end_time_unix_nano) - BigInt(span.start_time_unix_nano), 1n);
assert.equal(span.parent_span_id, "3333333333333333");
assert.equal(first(span.events).time_unix_nano, "9007199254740993");
assert.equal(first(span.links).dropped_attributes_count, "9");
console.log("PASS Telemetry_wire_ts_exactness: C# JSON -> generated TS -> BigInt; optional and special doubles");
