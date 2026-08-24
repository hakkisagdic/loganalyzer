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
/**
 * `gated` rozetinin metni — **`remedy`'ye göre**.
 *
 * *"31'i şema bekliyor, 11'i asla derlenmeyecek"* iki farklı cümle ve ikincisi
 * kullanıcıya bir **kapsam sınırı** olarak sunulmalı, iş kalemi olarak değil.
 * Tek bir "koşamaz" rozeti ikisini aynı şeye indirger ve liste, kullanıcının
 * neyin kapatacağını göremediği bir çöp kutusuna döner.
 *
 * Sebep metni senkronun yazdığı biçimde geliyor: `[remedy] kolon: mesaj`.
 */
export function gatedLabel(reason: string | undefined): string {
  if (!reason) return "koşamaz";

  const remedy = /^\[([a-z_]+)\]/.exec(reason)?.[1];

  // `upstream` "kimsenin yapamayacağı iş" demek ve bir iş kalemi DEĞİL;
  // ayrı bir sözcük hak ediyor. Diğerleri birinin yapabileceği bir işi
  // adlandırıyor.
  if (remedy === "upstream") return "desteklenmiyor";
  if (remedy === "schema") return "şema bekliyor";
  if (remedy === "pipeline" || remedy === "pipeline_or_schema") return "eşleme bekliyor";

  return "koşamaz";
}

export function AlertsOverview() {
  const [rules, setRules] = useState<readonly AlertRule[] | null>(null);
  const [triggers, setTriggers] = useState<readonly AlertTrigger[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  /**
   * Kaynak filtresi — **sekme değil**.
   *
   * Ticket "F2'nin alarm ekranına eklenen bir sekme" diyordu; sonraki karar
   * **tek liste, üçüncü durum** oldu ve bu onun ekrandaki karşılığı.
   *
   * Sekme, kullanıcı hangi sekmede olduğunu unuttuğunda *"kuralım nerede"*
   * sorusunu üretiyor: aynı amaca hizmet eden iki şey iki ayrı yerde
   * aranıyor. Filtre aynı kalabalığı çözüyor ve listeyi bölmüyor —
   * varsayılan "hepsi", yani hiçbir şey saklanmıyor.
   */
  const [source, setSource] = useState<"all" | "bizigo" | "sigma">("all");

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

      {/*
        Filtre yalnızca iki kaynak varken gösteriliyor: tek kaynaklı bir
        listede seçenek sunmak, olmayan bir ayrımı varmış gibi göstermek olur.
      */}
      {rules.some((r) => r.source === "sigma") && rules.some((r) => r.source !== "sigma") ? (
        <div className={styles.toolbar} role="group" aria-label="Kaynak filtresi">
          {(["all", "bizigo", "sigma"] as const).map((option) => (
            <Button
              key={option}
              variant={source === option ? "primary" : "secondary"}
              onClick={() => setSource(option)}
              aria-pressed={source === option}
            >
              {option === "all" ? "Hepsi" : option === "sigma" ? "Sigma" : "Kendi kurallarım"}
            </Button>
          ))}
        </div>
      ) : null}

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
            rows={
              source === "all"
                ? rules
                : rules.filter((r) =>
                    source === "sigma" ? r.source === "sigma" : r.source !== "sigma")
            }
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
                        <Badge>{gatedLabel(row.gated_reason)}</Badge>
                      </span>
                    ) : (
                      <Badge>pasif</Badge>
                    )}
                    {row.source === "sigma" ? <Badge>Sigma</Badge> : null}
                    {/*
                      "Kuralın ürettiği SQL değiştiğinde kullanıcı bunu görüyor."
                      Bilgi bir turdur kayda yazılıyordu ama ekranda yoktu —
                      yani kriter kapanmış GÖRÜNÜYORDU. Bu rozet onu kapatıyor.
                    */}
                    {row.sigma_changed_at ? (
                      <span title={`Ürettiği SQL değişti: ${formatInstant(row.sigma_changed_at)}`}>
                        <Badge>SQL değişti</Badge>
                      </span>
                    ) : null}
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
      <TriggerHistory triggers={triggers} onClosed={() => void load()} />
    </div>
  );
}
