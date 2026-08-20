import Link from "next/link";

import { DataTable, type Column } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Field";
import { describeSilence, type InventoryRow } from "@/lib/sources/inventory";

import styles from "./inventory.module.css";

/**
 * Envanter listesi (T17).
 *
 * <p>
 * Kapsam filtresi burada <b>yok</b>: liste <c>IScopedQuery.SearchSourcesAsync</c>
 * tarafından zaten filtrelenmiş geliyor. F1'de bu bir tasarım düzeltmesiydi —
 * filtre önce uç katmanındaydı, sonra tek kapıya taşındı (K17). Ekranda ikinci
 * bir filtre, o kararı geri almak olurdu.
 * </p>
 */

export interface InventoryTableProps {
  readonly rows: readonly InventoryRow[];
  /** Süre hesabının tabanı. Dışarıdan geliyor ki test zamanı sabitleyebilsin. */
  readonly now: Date;
  readonly windowHours: number;
}

export function InventoryTable({ rows, now, windowHours }: InventoryTableProps) {
  const columns: Column<InventoryRow>[] = [
    {
      key: "source_id",
      header: "Kaynak",
      width: "14rem",
      render: (row) => (
        <span className={styles.stack}>
          <span className={styles.mono}>{row.source.source_id}</span>
          {row.source.hostname ? (
            <span className={styles.muted}>{row.source.hostname}</span>
          ) : null}
        </span>
      ),
    },
    {
      key: "peer",
      header: "Adres",
      width: "10rem",
      render: (row) => <span className={styles.mono}>{row.source.peer_address || "—"}</span>,
    },
    {
      key: "owner_group",
      header: "Grup",
      width: "10rem",
      render: (row) => row.source.owner_group,
    },
    {
      key: "class",
      header: "Sınıf / kodlama",
      width: "10rem",
      render: (row) => (
        <span className={styles.stack}>
          <span>{row.source.source_class}</span>
          <span className={styles.muted}>{row.source.encoding}</span>
        </span>
      ),
    },
    {
      key: "parser",
      header: "Parser",
      width: "11rem",
      render: (row) =>
        row.source.is_known_to_dispatcher ? (
          <span className={styles.mono}>{row.source.parser_id}</span>
        ) : (
          // Dispatcher'ın en hızlı kademesi `source_id → parser_id` bağı;
          // bağsız kaynak alt kademelere düşüyor ve bu sessiz bir maliyet.
          <Badge tone="warning">bağlı değil</Badge>
        ),
    },
    {
      key: "last_seen",
      header: "Son görülme",
      width: "11rem",
      render: (row) => {
        const silence = describeSilence(row, now);

        return silence.kind === "quiet" ? (
          <Badge tone="danger">{silence.label}</Badge>
        ) : (
          <span>{silence.label}</span>
        );
      },
    },
    {
      key: "events",
      header: `Olay (${windowHours} sa)`,
      width: "8rem",
      numeric: true,
      render: (row) => (row.activity ? row.activity.event_count : 0),
    },
    {
      key: "state",
      header: "Durum",
      width: "7rem",
      render: (row) =>
        row.source.enabled ? (
          <Badge tone="success">açık</Badge>
        ) : (
          <Badge tone="neutral">kapalı</Badge>
        ),
    },
    {
      key: "links",
      header: "",
      width: "7rem",
      render: (row) => (
        // Envanterden aramaya köprü: kaynak filtresi zaten seçili geliyor ve o
        // filtre keyset sayfalamayı sabit süreli kılan şey.
        <Link href={`/olaylar?source_id=${encodeURIComponent(row.source.source_id)}`}>
          Loglar →
        </Link>
      ),
    },
  ];

  return (
    <DataTable
      caption={`${rows.length} kaynak — veri gelmeyenler başta`}
      columns={columns}
      rows={rows}
      rowKey={(row) => row.source.source_id}
    />
  );
}
