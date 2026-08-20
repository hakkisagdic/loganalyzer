"use client";

import { useCallback, useEffect, useMemo, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import { formatInstant, toNumber } from "@/lib/alerts/types";
import {
  COVERAGE_WARN_PERCENT,
  DRAFT_STATE_LABELS,
  missPercent,
  type CatalogCoverage,
  type DraftState,
  type ParserDraft,
  type ParserDraftList,
  type ParserList,
  type ParserPublishResult,
  type ParserSummary,
} from "@/lib/parsers/types";

import styles from "./catalog.module.css";

/** Sürüm geçmişi: aynı `parser_id` için tüm kayıtlar, en yenisi başta. */
function historyOf(drafts: readonly ParserDraft[], parserId: string): readonly ParserDraft[] {
  // `toSorted` yerine kopya + `sort`: hedef kütüphane es2023 değil ve
  // `sort` yerinde sıralıyor — girdi dizisi bileşenin state'i.
  return [...drafts]
    .filter((draft) => draft.parser_id === parserId)
    .sort((left: ParserDraft, right: ParserDraft) => right.updated_at.localeCompare(left.updated_at));
}

/**
 * Parser kataloğu (T20).
 *
 * <p>
 * <b>İki sayısal gösterge ve ikisi de uyarı üretebiliyor:</b> altın örnek
 * kapsamı (F1'de 86/1/0) ve katalog geneli <c>GROK003</c>. Ticket ikincisi için
 * açık: "sessizce sayı olarak durmuyor". F1'de o sayıyı 21'den 0'a indirmek
 * dört ayrı daraltma gerektirdi ve son ikisi bağlama özeldi; kazanımın sessizce
 * kaybedilmemesi bu ekranın var olma sebeplerinden biri.
 * </p>
 */
export function CatalogOverview() {
  const [parsers, setParsers] = useState<readonly ParserSummary[] | null>(null);
  const [backtracking, setBacktracking] = useState(0);
  const [drafts, setDrafts] = useState<readonly ParserDraft[]>([]);
  const [coverage, setCoverage] = useState<CatalogCoverage | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);

    try {
      const [list, draftList] = await Promise.all([
        api.get("/v1/parsers", { signal }) as Promise<ParserList>,
        api.get("/v1/parsers/drafts", { signal }) as Promise<ParserDraftList>,
      ]);

      setParsers(list.parsers);
      setBacktracking(toNumber(list.backtracking_groks));
      setDrafts(draftList.drafts);
    } catch (cause) {
      if (!signal?.aborted) {
        setError(describeError(cause));
        setParsers([]);
      }
    }

    try {
      // Kapsam ayrı: ölçüm pahalı ve katalog listesi onsuz da anlamlı.
      // Aynı `Promise.all` içinde olsaydı ölçüm yavaşladığında liste de
      // beklerdi.
      setCoverage((await api.get("/v1/parsers/coverage", { signal })) as CatalogCoverage);
    } catch {
      // Kapsam alınamaması listeyi bozmuyor; gösterge "ölçülmedi" kalıyor.
      setCoverage(null);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function remeasure() {
    setBusy(true);

    try {
      setCoverage((await api.get("/v1/parsers/coverage", { query: { force: true } })) as CatalogCoverage);
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  async function rollback(parserId: string) {
    setBusy(true);

    try {
      const result = (await api.post("/v1/parsers/{parserId}/rollback", {
        path: { parserId },
      })) as ParserPublishResult;

      // Katalog yayın sonrası HEMEN tazeleniyor; listeyi de tazeliyoruz ki
      // ekran "geri alındı" deyip eski sürümü göstermeye devam etmesin.
      setError(
        result.catalog.errors.length > 0
          ? `Geri alındı ama katalog yüklenirken hata: ${result.catalog.errors.join(", ")}`
          : null,
      );

      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  const missing = useMemo(() => (coverage ? missPercent(coverage) : 0), [coverage]);

  if (error && parsers === null) {
    return <ErrorState title="Katalog yüklenemedi" hint={error} />;
  }

  if (parsers === null) {
    return <LoadingState label="Katalog yükleniyor…" />;
  }

  return (
    <div className={styles.stack}>
      <div className={styles.toolbar}>
        <h1>Parser kataloğu</h1>
        <Button onClick={remeasure} disabled={busy}>
          Kapsamı yeniden ölç
        </Button>
      </div>

      {error ? <ErrorState title="Son işlem başarısız" hint={error} /> : null}

      {/*
        GROK003 uyarısı. Sıfırdan farklıysa bir parser geri izlemeye düşmüş
        demek ve o ifade `matchTimeout` ödüyor — yani yüklü bir makinede
        SAĞLIKLI bir satır `failed` olabiliyor. Sayıyı sessizce göstermek,
        F1'de dört daraltmayla kazanılanı sessizce kaybetmek olurdu.
      */}
      {backtracking > 0 ? (
        <ErrorState
          title={`Katalogda ${backtracking} grok doğrusal motora sığmıyor (GROK003)`}
          hint="Geri izlemeye düşen ifade zaman aşımı ödüyor; yüklü bir makinede sağlıklı bir satır 'failed' olabilir. Parser detayında hangi ifadenin neden düştüğü yazılı."
        />
      ) : null}

      <Card>
        <div className={styles.metrics}>
          <div className={styles.metric}>
            <span className={styles.metricValue}>{parsers.length}</span>
            <span className={styles.metricLabel}>yayındaki parser</span>
          </div>

          <div className={styles.metric}>
            <span className={styles.metricValue}>
              {backtracking === 0 ? "0" : backtracking}
            </span>
            <span className={styles.metricLabel}>
              GROK003 {backtracking === 0 ? "— temiz" : "— geri izleyen ifade"}
            </span>
          </div>

          {coverage ? (
            <>
              <div className={styles.metric}>
                <span className={styles.metricValue}>
                  {`${coverage.ok}/${coverage.partial}/${coverage.failed}`}
                </span>
                <span className={styles.metricLabel}>altın örnek ok/partial/failed</span>
              </div>

              <div className={styles.metric}>
                <span className={styles.metricLabel}>
                  Ölçüm: {formatInstant(coverage.measured_at)}
                  {/*
                    Bayat bir oranı taze gibi göstermek, ekranın tek sayısal
                    göstergesini işe yaramaz kılardı.
                  */}
                  {coverage.stale ? " — katalog o günden beri değişti" : ""}
                </span>
                {coverage.stale ? <Badge tone="warning">bayat</Badge> : null}
                {missing > COVERAGE_WARN_PERCENT ? (
                  <Badge tone="danger">kapsam düştü</Badge>
                ) : null}
              </div>
            </>
          ) : (
            <div className={styles.metric}>
              <span className={styles.metricLabel}>Kapsam henüz ölçülmedi</span>
            </div>
          )}
        </div>
      </Card>

      {parsers.length === 0 ? (
        <Card>
          <EmptyState
            title="Katalogda parser yok"
            description="Repodaki dosyalar yüklenemediyse boru hattı hiçbir satırı ayrıştıramaz."
          />
        </Card>
      ) : (
        <Card padded={false}>
          <DataTable
            caption="Yayındaki parser'lar"
            rowKey={(row) => row.id}
            rows={parsers}
            columns={[
              { key: "id", header: "Parser", width: "20%", render: (row) => row.id },
              { key: "vendor", header: "Vendor", width: "12%", render: (row) => row.vendor },
              { key: "product", header: "Ürün", width: "12%", render: (row) => row.product },
              { key: "version", header: "Sürüm", width: "10%", render: (row) => row.version },
              {
                key: "groks",
                header: "GROK003",
                width: "12%",
                numeric: true,
                render: (row) =>
                  row.backtracking_groks === 0 ? (
                    <Badge tone="success">temiz</Badge>
                  ) : (
                    <Badge tone="danger">{row.backtracking_groks} ifade</Badge>
                  ),
              },
              {
                key: "state",
                header: "Sürüm geçmişi",
                width: "20%",
                render: (row) => {
                  const history = historyOf(drafts, row.id);
                  const published = history.find((draft) => draft.state === "published");

                  return (
                    <span className={styles.inlineActions}>
                      <span className={styles.muted}>{history.length} kayıt</span>
                      {published ? (
                        <span className={styles.muted}>· {published.owner || "—"}</span>
                      ) : null}
                      <Button onClick={() => setExpanded(expanded === row.id ? null : row.id)}>
                        {expanded === row.id ? "Gizle" : "Göster"}
                      </Button>
                    </span>
                  );
                },
              },
              {
                key: "rollback",
                header: "İşlem",
                width: "14%",
                render: (row) => (
                  <Button
                    variant="danger"
                    disabled={busy || historyOf(drafts, row.id).length < 2}
                    onClick={() => rollback(row.id)}
                    title="Yayındaki sürümü emekliye ayırıp bir öncekini geri getirir."
                  >
                    Geri al
                  </Button>
                ),
              },
            ]}
          />
        </Card>
      )}

      {expanded ? (
        <Card padded={false}>
          <DataTable
            caption={`${expanded} sürüm geçmişi`}
            rowKey={(row) => row.id}
            rows={historyOf(drafts, expanded)}
            columns={[
              { key: "version", header: "Sürüm", width: "14%", render: (row) => row.version },
              {
                key: "state",
                header: "Durum",
                width: "14%",
                render: (row) => (
                  <Badge tone={row.state === "published" ? "success" : "neutral"}>
                    {DRAFT_STATE_LABELS[row.state as DraftState] ?? row.state}
                  </Badge>
                ),
              },
              { key: "owner", header: "Sahip", width: "18%", render: (row) => row.owner || "—" },
              {
                key: "tests",
                header: "Geçen test",
                width: "12%",
                numeric: true,
                render: (row) => toNumber(row.passing_tests),
              },
              {
                key: "updated",
                header: "Son değişiklik",
                width: "20%",
                render: (row) => formatInstant(row.updated_at),
              },
              {
                key: "published",
                header: "Yayın",
                width: "20%",
                render: (row) => formatInstant(row.published_at),
              },
            ]}
          />
        </Card>
      ) : null}
    </div>
  );
}
