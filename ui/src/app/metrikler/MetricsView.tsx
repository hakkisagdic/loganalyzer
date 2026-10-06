"use client";

import { useEffect, useState } from "react";
import { Badge } from "@/components/ui/Field";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import type { components } from "@/lib/api/schema";
import { formatInstant } from "@/lib/ui/time";
import { BucketVisualizer } from "./BucketVisualizer";
import styles from "./metrikler.module.css";

type TelemetryRecordDto = components["schemas"]["TelemetryRecordDto"];
type TelemetryPageDto = components["schemas"]["TelemetryPageDto"];

export function MetricsView() {
  const [data, setData] = useState<TelemetryPageDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [nameFilter, setNameFilter] = useState("");
  const [cursor, setCursor] = useState<string | null>(null);
  const [cursorStack, setCursorStack] = useState<string[]>([]);
  const [selectedRecord, setSelectedRecord] = useState<TelemetryRecordDto | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const params = new URLSearchParams();
    if (nameFilter) {
      params.set("name", nameFilter);
    }
    if (cursor) {
      params.set("cursor", cursor);
    }

    const qs = params.toString();
    const url = `/api/bff/v1/metrics${qs ? `?${qs}` : ""}`;

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
          setSelectedRecord(json.records[0] ?? null);
        }
      })
      .catch((err) => {
        if (!active) return;
        setError(err instanceof Error ? err.message : "Metrikler alınamadı.");
        setLoading(false);
      });

    return () => {
      active = false;
    };
  }, [nameFilter, cursor]);

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

  function renderStatusBadge(statusStr: string) {
    switch (statusStr) {
      case "Data":
      case "data":
      case "Ok":
      case "ok":
        return <Badge tone="success">Data</Badge>;
      case "Empty":
      case "empty":
        return <Badge tone="warning">Empty</Badge>;
      case "NeverFed":
      case "neverfed":
        return <Badge tone="neutral">NeverFed</Badge>;
      case "Failed":
      case "failed":
        return <Badge tone="danger">Failed</Badge>;
      default:
        return <Badge tone="neutral">{statusStr || "Bilinmiyor"}</Badge>;
    }
  }

  const columns: readonly Column<TelemetryRecordDto>[] = [
    {
      key: "name",
      header: "Metrik Adı",
      render: (r) => (
        <button
          type="button"
          onClick={() => setSelectedRecord(r)}
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
          {r.metric?.name || r.logical_id}
        </button>
      ),
    },
    {
      key: "kind",
      header: "Tür",
      render: (r) => r.metric?.kind || "—",
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
      render: () => renderStatusBadge(data?.status || "Data"),
    },
    {
      key: "time",
      header: "Zaman",
      render: (r) => formatInstant(
        r.time_unix_nano && r.time_unix_nano !== "0"
          ? new Date(Number(BigInt(r.time_unix_nano) / 1000000n)).toISOString()
          : null
      ),
    },
  ];

  return (
    <div className={styles.container}>
      <div className={styles.headerRow}>
        <h1 className={styles.title}>Metrikler</h1>
        <div className={styles.controls}>
          <input
            type="search"
            aria-label="Metrik adına göre ara"
            placeholder="Metrik adına göre filtrele..."
            className={styles.filterInput}
            value={nameFilter}
            onChange={(e) => {
              setNameFilter(e.target.value);
              setCursor(null);
              setCursorStack([]);
            }}
          />
        </div>
      </div>

      {error ? (
        <ErrorState
          title="Metrikler yüklenirken bir hata oluştu"
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
        <LoadingState label="Metrikler listesi yükleniyor..." rows={6} />
      ) : !data || data.records.length === 0 ? (
        <EmptyState
          title="Hiç metrik bulunamadı"
          description={
            nameFilter
              ? "Arama kriterine uygun metrik kaydı yok."
              : "Sisteme henüz metrik beslemesi yapılmamış."
          }
        />
      ) : (
        <div className={`${styles.grid} ${selectedRecord ? styles.gridTwoCol : ""}`}>
          <div className={styles.panel}>
            <DataTable
              caption="Sistem Metrikleri Listesi"
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

          {selectedRecord ? (
            <div className={styles.panel}>
              <h2 className={styles.panelTitle}>
                {selectedRecord.metric?.name || selectedRecord.logical_id}
              </h2>
              {selectedRecord.metric?.description ? (
                <p style={{ color: "var(--text-2)", margin: 0 }}>
                  {selectedRecord.metric.description}
                </p>
              ) : null}

              <div className={styles.metaGrid}>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Birim</span>
                  <span className={styles.metaValue}>{selectedRecord.metric?.unit || "—"}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Tür</span>
                  <span className={styles.metaValue}>{selectedRecord.metric?.kind || "—"}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Monoton</span>
                  <span className={styles.metaValue}>
                    {selectedRecord.metric?.is_monotonic === null
                      ? "—"
                      : selectedRecord.metric?.is_monotonic
                      ? "Evet"
                      : "Hayır"}
                  </span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Kaynak</span>
                  <span className={styles.metaValue}>{selectedRecord.source_id}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Kapsam Grubu</span>
                  <span className={styles.metaValue}>{selectedRecord.owner_group}</span>
                </div>
              </div>

              {selectedRecord.metric?.data_points?.map((dp, idx) => (
                <div key={idx} style={{ marginBlockStart: "var(--space-3)" }}>
                  <h3 style={{ fontSize: "var(--text-sm)", color: "var(--text-2)" }}>
                    Veri Noktası #{idx + 1}
                  </h3>
                  <BucketVisualizer point={dp} />
                </div>
              ))}
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
