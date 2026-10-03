import type { components } from "./schema";

// A numeric or integer|string OpenAPI regression must fail tsc, even when the
// runtime JSON happens to contain a string in the ordinary version=1 case.
type Version = components["schemas"]["TopologyNodeWriteDto"]["version"];
type RequireString<T extends string> = T;
export type TopologyWriteVersion = RequireString<NonNullable<Version>>;

const exact: TopologyWriteVersion = "9223372036854775807";
// @ts-expect-error versions must never pass through a JavaScript Number
const lossy: TopologyWriteVersion = 9007199254740993;
void exact;
void lossy;
