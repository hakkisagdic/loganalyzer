"use client";

import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState } from "@/components/ui/States";
import { formatInstant, type AlertDelivery, type AlertTrigger } from "@/lib/alerts/types";

import styles from "./alerts.module.css";

/**
 * Bir teslimin rozeti.
 *
 * <p>
 * <b>"Gönderildi" ile "ulaştı" ayrı</b> (T23 kabul kriteri). Bekleyen bir
 * teslim başarı değil; başarısız olan da sessizce kaybolmamalı. Rozet rengi
 * tek başına anlam taşımıyor — metin de durumu söylüyor (WCAG 1.4.1).
 * </p>
 */
function DeliveryBadge({ delivery }: { delivery: AlertDelivery }) {
  switch (delivery.state) {
    case "delivered":
      return <Badge tone="success">ulaştı</Badge>;
    case "failed":
      return <Badge tone="danger">ulaşmadı ({delivery.attempts} deneme)</Badge>;
    default:
      return <Badge tone="warning">bekliyor ({delivery.attempts} deneme)</Badge>;
  }
}

function Deliveries({ deliveries }: { deliveries: readonly AlertDelivery[] }) {
  if (deliveries.length === 0) {
    // Kanalı olmayan kural sessizce "gönderildi" görünmemeli: tetiklendi ama
    // kimseye gitmedi ve bu, alarmın en sinsi arızası.
    return <span className={styles.muted}>kanal bağlı değil</span>;
  }

  return (
    <div className={styles.deliveryList}>
      {deliveries.map((delivery) => (
        <div className={styles.deliveryRow} key={delivery.channel_id}>
          <span>{delivery.channel_name}</span>
          <DeliveryBadge delivery={delivery} />
          {delivery.state === "delivered" ? (
            <span className={styles.muted}>{formatInstant(delivery.delivered_at)}</span>
          ) : null}
          {delivery.state === "pending" && delivery.next_attempt_at ? (
            <span className={styles.muted}>
              sonraki deneme {formatInstant(delivery.next_attempt_at)}
            </span>
          ) : null}
          {delivery.last_error ? (
            // Redaksiyondan geçmiş metin: gönderici gizli bilgiyi buraya
            // yazamıyor (T22 bekçisi).
            <span className={styles.deliveryError}>{delivery.last_error}</span>
          ) : null}
        </div>
      ))}
    </div>
  );
}

/** Tetiklenme geçmişi: ne zaman, hangi değerle, hangi kanala gitti, ulaştı mı. */
export function TriggerHistory({ triggers }: { triggers: readonly AlertTrigger[] }) {
  if (triggers.length === 0) {
    return (
      <Card>
        <EmptyState
          title="Henüz tetiklenme yok"
          description="Kurallar koştukça tetiklenmeler ve kanal teslimleri burada görünecek."
        />
      </Card>
    );
  }

  return (
    <Card padded={false}>
      <DataTable
        caption="Tetiklenme geçmişi"
        rowKey={(row) => row.id}
        rows={triggers}
        columns={[
          {
            key: "fired",
            header: "Zaman",
            width: "14%",
            render: (row) => formatInstant(row.fired_at),
          },
          { key: "rule", header: "Kural", width: "16%", render: (row) => row.rule_name },
          {
            key: "source",
            header: "Kaynak",
            width: "12%",
            render: (row) => row.source_id || "—",
          },
          {
            key: "value",
            header: "Değer",
            width: "10%",
            numeric: true,
            render: (row) => `${row.value.toLocaleString("tr-TR")} / ${row.threshold.toLocaleString("tr-TR")}`,
          },
          {
            key: "summary",
            header: "Özet",
            width: "24%",
            freeText: true,
            render: (row) => row.summary,
          },
          {
            key: "deliveries",
            header: "Bildirim",
            width: "24%",
            render: (row) => <Deliveries deliveries={row.deliveries} />,
          },
        ]}
      />
    </Card>
  );
}
