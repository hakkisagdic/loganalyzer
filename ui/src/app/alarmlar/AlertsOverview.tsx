"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import {
  describeSeconds,
  formatInstant,
  RULE_TYPE_LABELS,
  toNumber,
  type AlertRule,
  type AlertRuleList,
  type AlertTrigger,
  type AlertTriggerList,
  type RuleType,
} from "@/lib/alerts/types";

import { TriggerHistory } from "./TriggerHistory";
import styles from "./alerts.module.css";

/**
 * Son koşumun durumu — rozet.
 *
 * <p>
 * <b>`timed_out` ayrı bir durum ve öyle görünmek zorunda.</b> Motor tarafında
 * zaman aşımı "sessiz" ile aynı kefeye konmuyor (F1'in en pahalı dersi); ekran
 * ikisini aynı rozetle gösterseydi o ayrımı tam da kullanıcının bakacağı yerde
 * kaybederdik.
 * </p>
 */
function RunStateBadge({ rule }: { rule: AlertRule }) {
  switch (rule.last_run_state) {
    case "fired":
      return <Badge tone="danger">tetiklendi</Badge>;
    case "quiet":
      return <Badge tone="success">sessiz</Badge>;
    case "suppressed":
      return <Badge tone="warning">bastırıldı</Badge>;
    case "timedout":
      return <Badge tone="warning">zaman aşımı — sonuç bilinmiyor</Badge>;
    case "failed":
      return <Badge tone="danger">hata</Badge>;
    default:
      return <Badge>hiç koşmadı</Badge>;
  }
}

/** Kural listesi ve tetiklenme geçmişi (T23). */
export function AlertsOverview() {
  const [rules, setRules] = useState<readonly AlertRule[] | null>(null);
  const [triggers, setTriggers] = useState<readonly AlertTrigger[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);

    try {
      const [ruleList, triggerList] = await Promise.all([
        api.get("/v1/alerts/rules", { signal }) as Promise<AlertRuleList>,
        api.get("/v1/alerts/triggers", { query: { limit: 100 }, signal }) as Promise<AlertTriggerList>,
      ]);

      setRules(ruleList.rules);
      setTriggers(triggerList.triggers);
    } catch (cause) {
      if (!signal?.aborted) {
        setError(describeError(cause));
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function remove(rule: AlertRule) {
    // Kuralı silmek tetiklenme geçmişini SİLMİYOR (sunucu tarafında bilinçli):
    // olay incelemesi çoğunlukla kural silindikten sonra yapılıyor.
    setBusy(true);

    try {
      await api.delete("/v1/alerts/rules/{id}", { path: { id: rule.id } });
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  if (error && rules === null) {
    return <ErrorState title="Alarmlar yüklenemedi" hint={error} />;
  }

  if (rules === null) {
    return <LoadingState label="Kurallar yükleniyor…" />;
  }

  return (
    <div className={styles.stack}>
      <div className={styles.toolbar}>
        <h1>Alarm kuralları</h1>
        <Link href="/alarmlar/yeni">
          <Button variant="primary">Yeni kural</Button>
        </Link>
      </div>

      {error ? <ErrorState title="Son işlem başarısız" hint={error} /> : null}

      {rules.length === 0 ? (
        <Card>
          <EmptyState
            title="Henüz kural yok"
            description="İlk kuralı yazarken önizleme, eşiğin son 24 saatte kaç kez tetikleneceğini gösterir."
            action={
              <Link href="/alarmlar/yeni">
                <Button variant="primary">Yeni kural</Button>
              </Link>
            }
          />
        </Card>
      ) : (
        <Card padded={false}>
          <DataTable
            caption="Tanımlı kurallar"
            rowKey={(row) => row.id}
            rows={rules}
            columns={[
              {
                key: "name",
                header: "Ad",
                width: "22%",
                render: (row) => <Link href={`/alarmlar/${row.id}`}>{row.name}</Link>,
              },
              {
                key: "type",
                header: "Tip",
                width: "14%",
                render: (row) =>
                  RULE_TYPE_LABELS[row.rule_type as RuleType]?.split(" — ")[0] ?? row.rule_type,
              },
              {
                key: "scope",
                header: "Kapsam",
                width: "16%",
                render: (row) => row.owner_groups.join(", "),
              },
              {
                key: "interval",
                header: "Aralık",
                width: "10%",
                numeric: true,
                render: (row) => describeSeconds(row.interval_seconds),
              },
              {
                key: "state",
                header: "Son koşum",
                width: "18%",
                render: (row) => (
                  <span className={styles.deliveryRow}>
                    <RunStateBadge rule={row} />
                    {/*
                      Üç durum, iki değil. `pasif` ile `gated`'i tek rozette
                      toplamak, "kullanıcı istemedi" ile "biz yapamadık"ı
                      karıştırmak olurdu: kullanıcı kapalı bir kuralı açmayı
                      dener, açılmaz, ve sebebini de göremez.

                      `gated` rozeti sebebini TAŞIYOR — sessiz bir "kapalı",
                      listeyi kullanıcının neyin kapatacağını göremediği bir
                      çöp kutusuna çevirir.
                    */}
                    {row.status === "enabled" ? null : row.status === "gated" ? (
                      <span title={row.gated_reason || undefined}>
                        <Badge>koşamaz</Badge>
                      </span>
                    ) : (
                      <Badge>pasif</Badge>
                    )}
                    {row.source === "sigma" ? <Badge>Sigma</Badge> : null}
                  </span>
                ),
              },
              {
                key: "last",
                header: "Son tetiklenme",
                width: "12%",
                render: (row) => formatInstant(row.last_fired_at),
              },
              {
                key: "actions",
                header: "İşlem",
                width: "8%",
                render: (row) => (
                  <Button variant="danger" disabled={busy} onClick={() => remove(row)}>
                    Sil
                  </Button>
                ),
              },
            ]}
          />
        </Card>
      )}

      <h2>Tetiklenme geçmişi</h2>
      <TriggerHistory triggers={triggers} />
    </div>
  );
}
