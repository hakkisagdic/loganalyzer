import type { ReactNode } from "react";
import { DataTable, type Column } from "@/components/ui/DataTable";
import type { components } from "@/lib/api/schema";
import styles from "./metrikler.module.css";

type TelemetryPointDto = components["schemas"]["TelemetryPointDto"];
type TelemetryNumberDto = components["schemas"]["TelemetryNumberDto"];

function formatBound(n: TelemetryNumberDto | undefined): string {
  if (!n) return "";
  if ("special" in n && n.special) return n.special;
  return n.value !== null && n.value !== undefined ? String(n.value) : "";
}

interface BucketRow {
  readonly id: string;
  readonly label: string;
  readonly count: number;
}

export function BucketVisualizer({ point }: { readonly point: TelemetryPointDto }) {
  const rows: BucketRow[] = [];

  if (point.explicit_bounds && point.bucket_counts) {
    const bounds = point.explicit_bounds;
    const counts = point.bucket_counts;

    for (let i = 0; i < counts.length; i++) {
      const count = Number(counts[i] ?? 0);
      let label = "";

      if (i === 0 && bounds.length > 0) {
        const upper = formatBound(bounds[0]);
        label = `≤ ${upper}`;
      } else if (i < bounds.length) {
        const lower = formatBound(bounds[i - 1]);
        const upper = formatBound(bounds[i]);
        label = `(${lower}, ${upper}]`;
      } else {
        const lower = formatBound(bounds[bounds.length - 1]);
        label = `> ${lower}`;
      }

      rows.push({
        id: `bucket-${i}`,
        label,
        count,
      });
    }
  } else if (point.positive && point.positive.bucket_counts) {
    const counts = point.positive.bucket_counts;
    const offset = Number(point.positive.offset ?? 0);

    for (let i = 0; i < counts.length; i++) {
      const count = Number(counts[i] ?? 0);
      const bucketIndex = offset + i;
      rows.push({
        id: `exp-bucket-${i}`,
        label: `Kova [2^(${bucketIndex})]`,
        count,
      });
    }
  }

  if (rows.length === 0) {
    return <p className={styles.metaValue}>Kova verisi bulunamadı.</p>;
  }

  const maxCount = Math.max(...rows.map((r) => r.count), 1);

  const columns: readonly Column<BucketRow>[] = [
    {
      key: "label",
      header: "Aralık / Kova",
      width: "60%",
      render: (r) => r.label,
    },
    {
      key: "count",
      header: "Adet",
      numeric: true,
      width: "40%",
      render: (r) => String(r.count),
    },
  ];

  return (
    <div>
      <div className={styles.bucketChart} aria-hidden="true">
        {rows.map((r) => {
          const pct = Math.round((r.count / maxCount) * 100);
          return (
            <div key={r.id} className={styles.bucketRow}>
              <span className={styles.bucketBound} title={r.label}>
                {r.label}
              </span>
              <div className={styles.barTrack}>
                <div className={styles.barFill} style={{ inlineSize: `${pct}%` }} />
              </div>
              <span className={styles.bucketCount}>{r.count}</span>
            </div>
          );
        })}
      </div>

      <div style={{ marginBlockStart: "var(--space-4)" }}>
        <DataTable
          caption="Histogram Kova Dağılım Tablosu"
          columns={columns}
          rows={rows}
          rowKey={(r) => r.id}
        />
      </div>
    </div>
  );
}
