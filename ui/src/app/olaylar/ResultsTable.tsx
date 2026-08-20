import Link from "next/link";

import { TimeSourceBadge } from "@/components/events/TimeSourceBadge";
import { Badge } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";
import type { EventSummary } from "@/lib/api/client";
import { formatParseStatus, formatSeverity } from "@/lib/events/format";
import { formatInstant } from "@/lib/ui/time";

import styles from "./events.module.css";

/**
 * Sonuç listesi.
 *
 * <p>
 * <c>DataTable</c>'ın <b>ilk gerçek tüketicisi</b>. Çok dilli gövde davranışı
 * (CJK kırılması, sağdan sola hizalama, dört satırda kesme) bileşenin içinde
 * çözülmüş durumda; buradaki tek iş <c>freeText</c> işaretini koymak.
 * </p>
 *
 * <p>
 * <c>time_source</c> her satırda görünüyor ve bu bir süs değil: zamanın cihazdan
 * mı yoksa bizim gözlemimizden mi geldiğini bilmeden iki olayı zaman ekseninde
 * yan yana koymak yanlış sonuç üretiyor.
 * </p>
 */

const statusTone = {
  ok: "success",
  partial: "warning",
  failed: "danger",
} as const;

export function ResultsTable({ events }: { events: readonly EventSummary[] }) {
  const columns: Column<EventSummary>[] = [
    {
      key: "ts",
      header: "Zaman (UTC)",
      width: "15rem",
      render: (row) => (
        <span className={styles.timeCell}>
          <Link href={`/olaylar/${row.event_id}`} className={styles.timeLink}>
            {formatInstant(row.ts)}
          </Link>
          <TimeSourceBadge value={row.time_source} />
        </span>
      ),
    },
    {
      key: "source",
      header: "Kaynak",
      width: "12rem",
      render: (row) => (
        <span className={styles.stack}>
          <span>{row.source_id || "—"}</span>
          {row.host ? <span className={styles.muted}>{row.host}</span> : null}
        </span>
      ),
    },
    {
      key: "vendor",
      header: "Ürün",
      width: "10rem",
      render: (row) => [row.vendor, row.product].filter(Boolean).join(" / ") || "—",
    },
    {
      key: "severity",
      header: "Önem",
      // 7rem'di ve en uzun etiket ("belirtilmemiş") kelime ortasından
      // kırılıyordu — ekran görüntüsünde görüldü. `overflow-wrap: anywhere`
      // çok dilli gövdeler için doğru, ama dar bir sütunda Türkçe bir kelimeyi
      // hecesiz bölüyor.
      width: "9.5rem",
      render: (row) => formatSeverity(row.severity_num),
    },
    {
      key: "parse_status",
      header: "Çözümleme",
      width: "8rem",
      render: (row) => (
        <Badge tone={statusTone[row.parse_status as keyof typeof statusTone] ?? "neutral"}>
          {formatParseStatus(row.parse_status)}
        </Badge>
      ),
    },
    {
      // Gövde serbest metin: tek aralıklı, `dir="auto"`, dört satırda kesiliyor.
      key: "body",
      header: "Gövde",
      freeText: true,
      render: (row) => row.body,
    },
  ];

  return (
    <DataTable
      caption={`${events.length} olay — zamana göre azalan`}
      columns={columns}
      rows={events}
      rowKey={(row) => row.event_id}
    />
  );
}
