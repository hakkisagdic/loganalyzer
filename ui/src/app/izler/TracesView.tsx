"use client";

import { useEffect, useState } from "react";
import { Badge } from "@/components/ui/Field";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import type { components } from "@/lib/api/schema";
import { formatInstant } from "@/lib/ui/time";
import { WaterfallVisualizer } from "./WaterfallVisualizer";
import styles from "./izler.module.css";

type TelemetryRecordDto = components["schemas"]["TelemetryRecordDto"];
type TelemetryPageDto = components["schemas"]["TelemetryPageDto"];
type TelemetrySpanDto = components["schemas"]["TelemetrySpanDto"];

export function TracesView() {
  const [data, setData] = useState<TelemetryPageDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [traceIdFilter, setTraceIdFilter] = useState("");
  const [cursor, setCursor] = useState<string | null>(null);
  const [cursorStack, setCursorStack] = useState<string[]>([]);
  const [selectedRecord, setSelectedRecord] = useState<TelemetryRecordDto | null>(null);
  const [activeSpan, setActiveSpan] = useState<TelemetrySpanDto | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const params = new URLSearchParams();
    if (traceIdFilter) {
      params.set("trace_id", traceIdFilter);
    }
    if (cursor) {
      params.set("cursor", cursor);
    }

    const qs = params.toString();
    const url = `/api/bff/v1/traces${qs ? `?${qs}` : ""}`;

    fetch(url)
      .then(async (res) => {
        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body?.detail || body?.error || `HTTP ${res.status}`);
        }
        return res.json();
      })
      .then((json: TelemetryPageDto) => {
        if (!active) return;
        setData(json);
        setLoading(false);
        if (json.records?.length && !selectedRecord) {
          const first = json.records[0] ?? null;
          setSelectedRecord(first);
          setActiveSpan(first?.span ?? null);
        }
      })
      .catch((err) => {
        if (!active) return;
        setError(err instanceof Error ? err.message : "İzler alınamadı.");
        setLoading(false);
      });

    return () => {
      active = false;
    };
  }, [traceIdFilter, cursor]);

  const handleNextPage = () => {
    if (data?.cursor) {
      setCursorStack((prev) => [...prev, cursor ?? ""]);
      setCursor(data.cursor);
    }
  };

  const handlePrevPage = () => {
    if (cursorStack.length > 0) {
      const prevCursor = cursorStack[cursorStack.length - 1];
      setCursorStack((prev) => prev.slice(0, -1));
      setCursor(prevCursor || null);
    }
  };

  const columns: readonly Column<TelemetryRecordDto>[] = [
    {
      key: "traceId",
      header: "İz (Trace) ID",
      render: (r) => (
        <button
          type="button"
          onClick={() => {
            setSelectedRecord(r);
            setActiveSpan(r.span ?? null);
          }}
          style={{
            background: "none",
            border: "none",
            padding: 0,
            color: "var(--accent)",
            fontWeight: "var(--weight-medium)",
            cursor: "pointer",
            textAlign: "start",
            fontFamily: "var(--font-mono)",
            fontSize: "var(--text-xs)",
          }}
        >
          {r.span?.trace_id || r.logical_id}
        </button>
      ),
    },
    {
      key: "spanName",
      header: "Span Adı",
      render: (r) => r.span?.name || "—",
    },
    {
      key: "source",
      header: "Kaynak / Servis",
      render: (r) => r.source_id,
    },
    {
      key: "group",
      header: "Grup",
      render: (r) => r.owner_group,
    },
    {
      key: "status",
      header: "Durum",
      render: (r) => {
        const isError = Number(r.span?.status_code) === 2;
        return (
          <Badge tone={isError ? "danger" : "success"}>
            {isError ? "Hata" : "Başarılı"}
          </Badge>
        );
      },
    },
    {
      key: "time",
      header: "Zaman",
      render: (r) =>
        formatInstant(
          r.time_unix_nano && r.time_unix_nano !== "0"
            ? new Date(Number(BigInt(r.time_unix_nano) / 1000000n)).toISOString()
            : null
        ),
    },
  ];

  return (
    <div className={styles.container}>
      <div className={styles.headerRow}>
        <h1 className={styles.title}>İzler (Traces)</h1>
        <div className={styles.controls}>
          <input
            type="search"
            aria-label="İz ID'ye göre ara"
            placeholder="İz (trace) ID'ye göre filtrele..."
            className={styles.filterInput}
            value={traceIdFilter}
            onChange={(e) => {
              setTraceIdFilter(e.target.value);
              setCursor(null);
              setCursorStack([]);
            }}
          />
        </div>
      </div>

      {error ? (
        <ErrorState
          title="İzler yüklenirken bir hata oluştu"
          hint={error}
          action={
            <Button
              variant="secondary"
              onClick={() => {
                setCursor(null);
                setCursorStack([]);
              }}
            >
              Yeniden Dene
            </Button>
          }
        />
      ) : loading ? (
        <LoadingState label="İzler listesi yükleniyor..." rows={6} />
      ) : !data || data.records.length === 0 ? (
        <EmptyState
          title="Hiç iz bulunamadı"
          description={
            traceIdFilter
              ? "Arama kriterine uygun iz kaydı yok."
              : "Sisteme henüz dağıtık iz beslemesi yapılmamış."
          }
        />
      ) : (
        <div className={`${styles.grid} ${selectedRecord ? styles.gridTwoCol : ""}`}>
          <div className={styles.panel}>
            <DataTable
              caption="Dağıtık İzler Listesi"
              columns={columns}
              rows={data.records}
              rowKey={(r) => r.logical_id}
            />

            <div className={styles.pagination}>
              <Button
                variant="secondary"
                disabled={cursorStack.length === 0}
                onClick={handlePrevPage}
              >
                Önceki
              </Button>
              <Button
                variant="secondary"
                disabled={!data.cursor}
                onClick={handleNextPage}
              >
                Sonraki
              </Button>
            </div>
          </div>

          {selectedRecord?.span ? (
            <div className={styles.panel}>
              <h2 className={styles.panelTitle}>
                {selectedRecord.span.name}
              </h2>

              <div className={styles.metaGrid}>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>İz (Trace) ID</span>
                  <span className={styles.metaValue}>{selectedRecord.span.trace_id}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Span ID</span>
                  <span className={styles.metaValue}>{selectedRecord.span.span_id}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Kaynak</span>
                  <span className={styles.metaValue}>{selectedRecord.source_id}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Grup</span>
                  <span className={styles.metaValue}>{selectedRecord.owner_group}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Durum Mesajı</span>
                  <span className={styles.metaValue}>
                    {selectedRecord.span.status_message || "—"}
                  </span>
                </div>
              </div>

              <h3 style={{ fontSize: "var(--text-md)", marginBlockStart: "var(--space-4)" }}>
                Şelale (Waterfall) Görünümü
              </h3>
              <WaterfallVisualizer
                spans={[selectedRecord.span]}
                onSelectSpan={(span) => setActiveSpan(span)}
              />

              {activeSpan?.attributes?.length ? (
                <div style={{ marginBlockStart: "var(--space-3)" }}>
                  <h4 style={{ fontSize: "var(--text-sm)", color: "var(--text-2)" }}>
                    Span Öznitelikleri
                  </h4>
                  <div className={styles.metaGrid}>
                    {activeSpan.attributes.map((attr, i) => (
                      <div key={i} className={styles.metaItem}>
                        <span className={styles.metaLabel}>{attr.key}</span>
                        <span className={styles.metaValue}>
                          {JSON.stringify(attr.value)}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
