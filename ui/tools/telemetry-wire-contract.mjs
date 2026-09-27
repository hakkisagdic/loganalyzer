// Runnable contract alias for the same strict generated-TypeScript/Node oracle.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("../scripts/telemetry-wire-check.ts", import.meta.url));
const evidence = process.argv[2] ?? process.env.BIZIGO_TELEMETRY_WIRE_EVIDENCE ?? "/tmp/bizigo-s04/wire-json";
const result = spawnSync(process.execPath, ["--experimental-strip-types", script, evidence], { stdio: "inherit" });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
