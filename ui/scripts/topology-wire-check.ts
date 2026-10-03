import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { components, operations } from "../src/lib/api/schema.d.ts";

type Schemas = components["schemas"];
type RequireText<T extends string> = T;
// Compile-time guards reject number|string just as strictly as number.
type NodeVersion = RequireText<Schemas["TopologyNodeWriteDto"]["version"]>;
type EdgeVersion = RequireText<Schemas["TopologyDeclaredEdgeVersion"]["version"]>;
type NodeInputVersion = RequireText<NonNullable<Schemas["TopologyNodeMutationDto"]["version"]>>;
type EdgeInputVersion = RequireText<NonNullable<Schemas["TopologyDeclaredEdgeInput"]["version"]>>;
type NodeDelete = operations["DeleteTopologyNode"]["parameters"]["query"];
type EdgeDelete = operations["DeleteTopologyDeclaredEdge"]["parameters"]["query"];
type DeleteNodeVersion = RequireText<NodeDelete["version"]>;
type DeleteEdgeVersion = RequireText<EdgeDelete["version"]>;

const folder = process.argv[2];
assert.ok(folder, "usage: node --experimental-strip-types scripts/topology-wire-check.ts <C# wire directory>");
const manifest = JSON.parse(readFileSync(join(folder, "sha256.json"), "utf8")) as Record<string, string>;
assert.equal(Object.keys(manifest).length, 27);
function load<T>(version: string, name: string): T {
  const file = `${version}-${name}.json`;
  const bytes = readFileSync(join(folder!, file));
  assert.equal(createHash("sha256").update(bytes).digest("hex"), manifest[file], file);
  return JSON.parse(bytes.toString("utf8")) as T;
}
function exact(value: unknown, expected: bigint): void {
  assert.equal(typeof value, "string");
  assert.match(value as string, /^(0|[1-9][0-9]*)$/);
  assert.equal(BigInt(value as string), expected);
  assert.equal((BigInt(value as string) + 1n) - BigInt(value as string), 1n);
}
for (const expected of [1n, 9007199254740993n, 9223372036854775807n]) {
  const version = expected.toString();
  const node = load<Schemas["TopologyNodeWriteDto"]>(version, "node-write");
  const edge = load<Schemas["TopologyDeclaredEdgeVersion"]>(version, "edge-write");
  const nodeInput = load<Schemas["TopologyNodeMutationDto"]>(version, "node-input");
  const edgeInput = load<Schemas["TopologyDeclaredEdgeInput"]>(version, "edge-input");
  const nodeDelete = load<NodeDelete>(version, "delete-query");
  const edgeDelete = load<EdgeDelete>(version, "delete-query");
  const versions: [NodeVersion, EdgeVersion, NodeInputVersion, EdgeInputVersion, DeleteNodeVersion, DeleteEdgeVersion] =
    [node.version, edge.version, nodeInput.version!, edgeInput.version!, nodeDelete.version, edgeDelete.version];
  for (const field of versions) exact(field, expected);
  exact(load<Schemas["TopologyNodeWriteResultDto"]>(version, "node-error").node!.version, expected);
  exact(load<Schemas["TopologyDeclaredEdgeResult"]>(version, "edge-error").edge!.version, expected);
  const page = load<Schemas["TopologyNodePageDto"]>(version, "node-read");
  assert.equal(page.nodes.length, 1);
  exact(page.nodes[0]!.version, expected);
  exact(page.nodes[0]!.valid_from_unix_nano, 18446744073709551615n);
  exact(page.published_sequence, 18446744073709551615n);
  assert.equal(page.nodes[0]!.valid_to_unix_nano, null);
  assert.equal(page.cursor, null); assert.equal(page.partial, false);
  const readEdge = load<Schemas["TopologyEdgeDto"]>(version, "edge-read");
  exact(readEdge.version, expected); exact(readEdge.publication_sequence, 18446744073709551615n);
  exact(readEdge.first_seen_unix_nano, 18446744073709551614n);
  exact(readEdge.last_seen_unix_nano, 18446744073709551615n);
  assert.equal(BigInt(readEdge.last_seen_unix_nano) - BigInt(readEdge.first_seen_unix_nano), 1n);
  assert.equal(readEdge.effective_expiry_unix_nano, null);
  exact(node.validFromUnixNano, 18446744073709551615n);
  exact(edge.validFromUnixNano, 18446744073709551615n);
  assert.equal(readEdge.relation, "depends_on"); assert.equal(readEdge.provenance, "declared");
  assert.equal(typeof readEdge.confidence, "number"); assert.ok(Number.isFinite(readEdge.confidence));
  assert.equal(edge.confidence, 1); assert.equal(readEdge.confidence, 1);
}
console.log("PASS topology wire: 27 C# JSON files -> generated TS -> exact BigInt, input/delete/response/error/read fields");
