"use client";

import { useEffect, useState, useTransition } from "react";
import Link from "next/link";
import { Badge } from "@/components/ui/Field";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import type { components } from "@/lib/api/schema";
import { formatInstant } from "@/lib/ui/time";
import styles from "./anomaliler.module.css";

type AnomalyPolicyResponse = components["schemas"]["AnomalyPolicyResponse"];
type AnomalyRunResponse = components["schemas"]["AnomalyRunResponse"];

function formatNum(val: string | number | null | undefined, digits = 1): string {
  if (val === null || val === undefined) return "—";
  const num = typeof val === "number" ? val : parseFloat(val);
  return isNaN(num) ? "—" : num.toFixed(digits);
}

export function AnomalyView() {
  const [policies, setPolicies] = useState<AnomalyPolicyResponse[]>([]);
  const [selectedPolicyId, setSelectedPolicyId] = useState<string | null>(null);
  const [runs, setRuns] = useState<AnomalyRunResponse[]>([]);

  const [loadingPolicies, setLoadingPolicies] = useState(true);
  const [loadingRuns, setLoadingRuns] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [patchError, setPatchError] = useState<string | null>(null);

  const [statusFilter, setStatusFilter] = useState<string>("");
  const [signalFilter, setSignalFilter] = useState<string>("");
  const [isPending, startTransition] = useTransition();

  // Load policies
  const loadPolicies = () => {
    setLoadingPolicies(true);
    setError(null);

    const params = new URLSearchParams();
    if (signalFilter) params.set("signal", signalFilter);

    const qs = params.toString();
    const url = `/api/bff/v1/anomaly-policies${qs ? `?${qs}` : ""}`;

    fetch(url)
      .then(async (res) => {
        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body?.detail || body?.error || `HTTP ${res.status}`);
        }
        return res.json();
      })
      .then((data: AnomalyPolicyResponse[]) => {
        setPolicies(data);
        if (data.length > 0) {
          setSelectedPolicyId((prev) => (prev && data.some((p) => p.id === prev) ? prev : data[0]!.id));
        } else {
          setSelectedPolicyId(null);
        }
        setLoadingPolicies(false);
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : String(err));
        setLoadingPolicies(false);
      });
  };

  useEffect(() => {
    loadPolicies();
  }, [signalFilter]);

  // Load runs for selected policy
  useEffect(() => {
    if (!selectedPolicyId) {
      setRuns([]);
      return;
    }

    setLoadingRuns(true);
    const params = new URLSearchParams();
    params.set("policy_id", selectedPolicyId);
    if (statusFilter) params.set("status", statusFilter);

    const qs = params.toString();
    const url = `/api/bff/v1/anomaly-runs${qs ? `?${qs}` : ""}`;

    fetch(url)
      .then(async (res) => {
        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body?.detail || body?.error || `HTTP ${res.status}`);
        }
        return res.json();
      })
      .then((data: AnomalyRunResponse[]) => {
        setRuns(data);
        setLoadingRuns(false);
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : String(err));
        setLoadingRuns(false);
      });
  }, [selectedPolicyId, statusFilter]);

  const selectedPolicy = policies.find((p) => p.id === selectedPolicyId);

  const handleToggleState = async () => {
    if (!selectedPolicy) return;
    setPatchError(null);

    const newState = selectedPolicy.state === "Enabled" ? "Disabled" : "Enabled";

    startTransition(async () => {
      try {
        const res = await fetch(`/api/bff/v1/anomaly-policies/${selectedPolicy.id}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            expected_version: selectedPolicy.version,
            state: newState,
          }),
        });

        if (res.status === 409) {
          setPatchError("Politika versiyonu uyuşmuyor (başka bir işlem tarafından güncellendi). Lütfen listeyi yenileyin.");
          return;
        }

        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body?.detail || body?.error || `HTTP ${res.status}`);
        }

        const updated: AnomalyPolicyResponse = await res.json();
        setPolicies((prev) => prev.map((p) => (p.id === updated.id ? updated : p)));
      } catch (err) {
        setPatchError(err instanceof Error ? err.message : String(err));
      }
    });
  };

  const renderStatusBadge = (status: string) => {
    switch (status) {
      case "Running":
        return <span className={`${styles.stateBadge} ${styles.stateRunning}`}>Running</span>;
      case "NoSignal":
        return <span className={`${styles.stateBadge} ${styles.stateNoSignal}`}>NoSignal</span>;
      case "Triggered":
        return <span className={`${styles.stateBadge} ${styles.stateTriggered}`}>Triggered</span>;
      case "Suppressed":
        return <span className={`${styles.stateBadge} ${styles.stateSuppressed}`}>Suppressed</span>;
      case "Failed":
        return <span className={`${styles.stateBadge} ${styles.stateFailed}`}>Failed</span>;
      default:
        return <span className={styles.stateBadge}>{status}</span>;
    }
  };

  const policyColumns: Column<AnomalyPolicyResponse>[] = [
    {
      key: "name",
      header: "Politika Adı",
      render: (p) => (
        <div>
          <div style={{ fontWeight: 600 }}>{p.name}</div>
          <div style={{ fontSize: "var(--text-xs)", color: "var(--text-2)" }}>{p.id}</div>
        </div>
      ),
    },
    {
      key: "scope",
      header: "Kapsam / Hedef",
      render: (p) => (
        <div>
          <Badge tone="neutral">{p.ownerGroup}</Badge>
          <div style={{ fontSize: "var(--text-xs)", color: "var(--text-2)", marginTop: 2 }}>{p.target}</div>
        </div>
      ),
    },
    {
      key: "signal",
      header: "Sinyal",
      render: (p) => <Badge tone="accent">{p.signal}</Badge>,
    },
    {
      key: "state",
      header: "Durum",
      render: (p) => (
        <Badge tone={p.state === "Enabled" ? "success" : "neutral"}>
          {p.state === "Enabled" ? "Aktif (v" + p.version + ")" : "Devre Dışı (v" + p.version + ")"}
        </Badge>
      ),
    },
    {
      key: "windows",
      header: "Pencereler",
      render: (p) => (
        <span style={{ fontSize: "var(--text-xs)" }}>
          Olay: {String(p.eventWindowSeconds)}s / Taban: {String(p.baselineWindowSeconds)}s
        </span>
      ),
    },
    {
      key: "action",
      header: "İşlem",
      render: (p) => (
        <Button
          variant={selectedPolicyId === p.id ? "primary" : "secondary"}
          onClick={() => setSelectedPolicyId(p.id)}
        >
          {selectedPolicyId === p.id ? "Seçili" : "Detay"}
        </Button>
      ),
    },
  ];

  const runColumns: Column<AnomalyRunResponse>[] = [
    {
      key: "window",
      header: "Zaman Penceresi (UTC)",
      render: (r) => (
        <div style={{ fontSize: "var(--text-xs)" }}>
          <div>{formatInstant(r.windowStart)}</div>
          <div style={{ color: "var(--text-2)" }}>→ {formatInstant(r.windowEnd)}</div>
        </div>
      ),
    },
    {
      key: "status",
      header: "Durum",
      render: (r) => renderStatusBadge(r.status),
    },
    {
      key: "reason",
      header: "Gerekçe",
      render: (r) => (
        <span style={{ fontSize: "var(--text-xs)", color: "var(--text-2)" }}>
          {r.reason || "—"}
        </span>
      ),
    },
    {
      key: "values",
      header: "Gözlem / Taban",
      numeric: true,
      render: (r) => (
        <span style={{ fontSize: "var(--text-xs)" }}>
          {formatNum(r.observedValue)} / {formatNum(r.baselineValue)}
        </span>
      ),
    },
    {
      key: "deviation",
      header: "Sapma",
      numeric: true,
      render: (r) => {
        const dev = typeof r.deviation === "number" ? r.deviation : r.deviation ? parseFloat(r.deviation) : null;
        return (
          <span style={{ fontSize: "var(--text-xs)", fontWeight: 600 }}>
            {dev !== null && !isNaN(dev) ? `${dev > 0 ? "+" : ""}${dev.toFixed(2)}σ` : "—"}
          </span>
        );
      },
    },
    {
      key: "rca",
      header: "RCA Analizi",
      render: (r) =>
        r.rcaRunId ? (
          <Link href={`/rca/${r.rcaRunId}`} className={styles.rcaLink}>
            RCA Koşumu ↗
          </Link>
        ) : (
          <span style={{ fontSize: "var(--text-xs)", color: "var(--text-2)" }}>—</span>
        ),
    },
  ];

  if (loadingPolicies && policies.length === 0) {
    return <LoadingState label="Anomali politikaları yükleniyor" rows={5} />;
  }

  if (error && policies.length === 0) {
    return (
      <ErrorState
        title="Anomali politikaları yüklenemedi"
        hint={error}
        action={<Button onClick={loadPolicies}>Tekrar Dene</Button>}
      />
    );
  }

  return (
    <div className={styles.container}>
      <div className={styles.headerRow}>
        <div>
          <h1 className={styles.title}>Anomali Politikaları ve Değerlendirmeleri</h1>
          <p className={styles.subtitle}>
            S4 Metrik, Log ve İz anomalilerini taban çizgiye göre tespit eden kurallar ve koşan değerlendirmeler.
          </p>
        </div>

        <div className={styles.controls}>
          <select
            className={styles.selectInput}
            value={signalFilter}
            onChange={(e) => setSignalFilter(e.target.value)}
            aria-label="Sinyale göre filtrele"
          >
            <option value="">Tüm Sinyaller</option>
            <option value="log_event_count">log_event_count</option>
            <option value="metric_series_sum">metric_series_sum</option>
            <option value="trace_error_rate">trace_error_rate</option>
          </select>

          <Button variant="secondary" onClick={loadPolicies}>
            Yenile
          </Button>
        </div>
      </div>

      {policies.length === 0 ? (
        <EmptyState title="Politika bulunamadı" description="Kapsamınızda tanımlı aktif anomali politikası yok." />
      ) : (
        <div className={styles.section}>
          <h2 className={styles.sectionTitle}>Politika Listesi</h2>
          <DataTable
            caption="Anomali Politikaları Tablosu"
            columns={policyColumns}
            rows={policies}
            rowKey={(p) => p.id}
          />
        </div>
      )}

      {selectedPolicy && (
        <div className={styles.section}>
          <h2 className={styles.sectionTitle}>Politika Detayı: {selectedPolicy.name}</h2>
          <div className={styles.detailCard}>
            {patchError && (
              <div className={styles.patchErrorMessage}>
                {patchError}
              </div>
            )}

            <div className={styles.detailGrid}>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Kimlik (ID)</span>
                <span className={styles.detailValue}>{selectedPolicy.id}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Kapsam Grubu</span>
                <span className={styles.detailValue}>{selectedPolicy.ownerGroup}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Hedef Varlık</span>
                <span className={styles.detailValue}>{selectedPolicy.target}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Sinyal Tipi</span>
                <span className={styles.detailValue}>{selectedPolicy.signal}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Duyarlılık / Eşik</span>
                <span className={styles.detailValue}>
                  {String(selectedPolicy.sensitivity)}σ (min {String(selectedPolicy.minSamples)} örnek)
                </span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Olay Penceresi</span>
                <span className={styles.detailValue}>{String(selectedPolicy.eventWindowSeconds)} saniye</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Taban Penceresi</span>
                <span className={styles.detailValue}>{String(selectedPolicy.baselineWindowSeconds)} saniye</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Sıfır Taban Eşiği</span>
                <span className={styles.detailValue}>{selectedPolicy.zeroBaselineMinAbsolute != null ? String(selectedPolicy.zeroBaselineMinAbsolute) : "Yok"}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Versiyon</span>
                <span className={styles.detailValue}>v{selectedPolicy.version}</span>
              </div>
              <div className={styles.detailItem}>
                <span className={styles.detailLabel}>Son Değerlendirme</span>
                <span className={styles.detailValue}>
                  {selectedPolicy.lastEvaluatedWindowStart
                    ? formatInstant(selectedPolicy.lastEvaluatedWindowStart)
                    : "Henüz koşmadı"}
                </span>
              </div>
            </div>

            <div className={styles.detailActions}>
              <Button
                variant={selectedPolicy.state === "Enabled" ? "danger" : "primary"}
                onClick={handleToggleState}
                disabled={isPending}
              >
                {isPending
                  ? "İşleniyor..."
                  : selectedPolicy.state === "Enabled"
                  ? "Politikayı Devre Dışı Bırak"
                  : "Politikayı Aktif Et"}
              </Button>
            </div>
          </div>
        </div>
      )}

      {selectedPolicy && (
        <div className={styles.section}>
          <div className={styles.headerRow}>
            <h2 className={styles.sectionTitle}>Değerlendirme Koşumları ({selectedPolicy.name})</h2>
            <div className={styles.controls}>
              <select
                className={styles.selectInput}
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}
                aria-label="Koşum durumuna göre filtrele"
              >
                <option value="">Tüm Durumlar</option>
                <option value="Running">Running</option>
                <option value="NoSignal">NoSignal</option>
                <option value="Triggered">Triggered</option>
                <option value="Suppressed">Suppressed</option>
                <option value="Failed">Failed</option>
              </select>
            </div>
          </div>

          {loadingRuns ? (
            <LoadingState label="Değerlendirme koşumları yükleniyor" rows={4} />
          ) : runs.length === 0 ? (
            <EmptyState
              title="Koşum kaydı bulunamadı"
              description="Bu politika için henüz bir değerlendirme kaydı üretilmemiş."
            />
          ) : (
            <DataTable
              caption="Anomali Değerlendirme Koşumları"
              columns={runColumns}
              rows={runs}
              rowKey={(r) => r.id}
            />
          )}
        </div>
      )}
    </div>
  );
}
