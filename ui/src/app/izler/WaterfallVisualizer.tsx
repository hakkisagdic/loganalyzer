import { useState } from "react";
import { Badge } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";
import type { components } from "@/lib/api/schema";
import styles from "./izler.module.css";

type TelemetrySpanDto = components["schemas"]["TelemetrySpanDto"];

interface SpanItem {
  readonly span: TelemetrySpanDto;
  readonly startNano: bigint;
  readonly endNano: bigint;
  readonly durationMs: number;
  readonly offsetPct: number;
  readonly widthPct: number;
  readonly isError: boolean;
}

export function WaterfallVisualizer({
  spans,
  onSelectSpan,
}: {
  readonly spans: readonly TelemetrySpanDto[];
  readonly onSelectSpan?: (span: TelemetrySpanDto) => void;
}) {
  const [selectedSpanId, setSelectedSpanId] = useState<string | null>(
    spans[0]?.span_id ?? null
  );

  if (spans.length === 0) {
    return <p className={styles.metaValue}>İzde hiç span kaydı yok.</p>;
  }

  let minStart = BigInt(spans[0]?.start_time_unix_nano || "0");
  let maxEnd = BigInt(spans[0]?.end_time_unix_nano || "0");

  for (const s of spans) {
    const st = BigInt(s.start_time_unix_nano || "0");
    const et = BigInt(s.end_time_unix_nano || "0");
    if (st < minStart) minStart = st;
    if (et > maxEnd) maxEnd = et;
  }

  const totalNano = maxEnd > minStart ? maxEnd - minStart : 1n;

  const items: SpanItem[] = spans.map((s) => {
    const st = BigInt(s.start_time_unix_nano || "0");
    const et = BigInt(s.end_time_unix_nano || "0");
    const durNano = et >= st ? et - st : 0n;
    const durationMs = Number(durNano / 1000000n);
    const offsetPct = Number(((st - minStart) * 100n) / totalNano);
    const widthPct = Math.max(1, Number((durNano * 100n) / totalNano));
    const isError = Number(s.status_code) === 2; // Otel StatusCode 2 = Error

    return {
      span: s,
      startNano: st,
      endNano: et,
      durationMs,
      offsetPct: Math.min(100, Math.max(0, offsetPct)),
      widthPct: Math.min(100, widthPct),
      isError,
    };
  });

  const columns: readonly Column<SpanItem>[] = [
    {
      key: "name",
      header: "Span Adı",
      render: (item) => (
        <button
          type="button"
          onClick={() => {
            setSelectedSpanId(item.span.span_id);
            onSelectSpan?.(item.span);
          }}
          style={{
            background: "none",
            border: "none",
            padding: 0,
            color: "var(--accent)",
            fontWeight: "var(--weight-medium)",
            cursor: "pointer",
            textAlign: "start",
          }}
        >
          {item.span.name}
        </button>
      ),
    },
    {
      key: "id",
      header: "Span ID",
      render: (item) => item.span.span_id,
    },
    {
      key: "parent",
      header: "Üst Span ID",
      render: (item) => item.span.parent_span_id || "— (Kök)",
    },
    {
      key: "duration",
      header: "Süre (ms)",
      numeric: true,
      render: (item) => String(item.durationMs),
    },
    {
      key: "status",
      header: "Durum",
      render: (item) => (
        <Badge tone={item.isError ? "danger" : "success"}>
          {item.isError ? "Hata" : "Başarılı"}
        </Badge>
      ),
    },
  ];

  return (
    <div>
      <div className={styles.waterfallContainer} aria-label="Şelale Grafiği">
        {items.map((item) => {
          const isSelected = item.span.span_id === selectedSpanId;
          return (
            <div
              key={item.span.span_id}
              className={styles.waterfallRow}
              style={{
                outline: isSelected ? "var(--focus-width) solid var(--accent)" : "none",
                borderRadius: "var(--radius-sm)",
                padding: "var(--space-1)",
              }}
            >
              <span className={styles.waterfallLabel} title={item.span.name}>
                {item.span.parent_span_id ? "↳ " : ""}{item.span.name}
              </span>
              <div className={styles.waterfallTimeline}>
                <div
                  className={`${styles.waterfallSpanBar} ${
                    item.isError ? styles.waterfallSpanBarError : ""
                  }`}
                  style={{
                    left: `${item.offsetPct}%`,
                    inlineSize: `${item.widthPct}%`,
                  }}
                  title={`${item.span.name} (${item.durationMs} ms)`}
                />
              </div>
              <span className={styles.waterfallDuration}>{item.durationMs} ms</span>
            </div>
          );
        })}
      </div>

      <div style={{ marginBlockStart: "var(--space-4)" }}>
        <DataTable
          caption="Span Hiyerarşisi ve Detay Tablosu"
          columns={columns}
          rows={items}
          rowKey={(item) => item.span.span_id}
        />
      </div>
    </div>
  );
}
