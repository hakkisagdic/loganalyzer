import type { components, operations } from "./schema";

// A numeric or integer|string OpenAPI regression must fail tsc, even when the
// runtime JSON happens to contain a string in the ordinary version=1 case.
type Version = components["schemas"]["TopologyNodeWriteDto"]["version"];
type RequireString<T extends string> = T;
export type TopologyWriteVersion = RequireString<NonNullable<Version>>;
export type TopologyInputVersion = RequireString<NonNullable<components["schemas"]["TopologyNodeMutationDto"]["version"]>>;
export type TopologyNodeDeleteVersion = RequireString<operations["DeleteTopologyNode"]["parameters"]["query"]["version"]>;
export type TopologyEdgeDeleteVersion = RequireString<operations["DeleteTopologyDeclaredEdge"]["parameters"]["query"]["version"]>;
export type TopologyErrorNodeVersion = RequireString<NonNullable<components["schemas"]["TopologyNodeWriteResultDto"]["node"]>["version"]>;

const exact: TopologyWriteVersion = "9223372036854775807";
const exactInput: TopologyInputVersion = exact;
const exactNodeDelete: TopologyNodeDeleteVersion = exact;
const exactEdgeDelete: TopologyEdgeDeleteVersion = exact;
const exactError: TopologyErrorNodeVersion = exact;
// @ts-expect-error versions must never pass through a JavaScript Number
const lossy: TopologyWriteVersion = 9007199254740993;
// @ts-expect-error node mutation input must be decimal text only
const lossyInput: TopologyInputVersion = 9007199254740993;
// @ts-expect-error node DELETE query must be decimal text only
const lossyNodeDelete: TopologyNodeDeleteVersion = 9007199254740993;
// @ts-expect-error edge DELETE query must be decimal text only
const lossyEdgeDelete: TopologyEdgeDeleteVersion = 9007199254740993;
void exact;
void exactInput;
void exactNodeDelete;
void exactEdgeDelete;
void exactError;
void lossy;
void lossyInput;
void lossyNodeDelete;
void lossyEdgeDelete;
