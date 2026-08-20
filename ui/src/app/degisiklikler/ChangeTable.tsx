import { Badge } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";

/**
 * `change_events` satırının UI karşılığı.
 *
 * <p>Alan adları `snake_case`: API'nin tamamı öyle konuşuyor (T15, `/auth/me`).</p>
 */
export interface ChangeRow {
  readonly change_id: string;
  readonly timestamp: string;
  readonly owner_group: string;
  readonly target_kind: string;
  readonly target_id: string;
  readonly change_kind: string;
  readonly actor: string;
  readonly summary: string;
  readonly source: string;
  readonly external_ref: string;
}

/**
 * Zaman damgası biçimi. `Intl` yerine sabit bir biçim: tabloda hizalanması
 * gereken bir sütun ve yerel biçim uzunluğu satırdan satıra değiştirirdi.
 */
export function formatInstant(value: string): string {
  const date = new Date(value);

  return Number.isNaN(date.getTime())
    ? "—"
    : date.toISOString().replace("T", " ").slice(0, 16);
}

/**
 * Değişiklik listesinin **çizilen** kısmı — durum taşımıyor.
 *
 * <p>
 * Veri getiren bileşenden ayrı duruyor çünkü sınanabilir olan bu: çok dilli
 * gövdenin `DataTable`'a gerçekten `freeText` olarak verildiği, `target_kind`'ın
 * metin olarak çizildiği ve yüzlerce satırın tabloyu bozmadığı ancak burada
 * ölçülebiliyor (T15'in `ResultsTable`'ı ile aynı gerekçe).
 * </p>
 */
export function ChangeTable({ rows }: { rows: readonly ChangeRow[] }) {
  const columns: readonly Column<ChangeRow>[] = [
    {
      key: "timestamp",
      header: "Zaman",
      width: "17ch",
      render: (row) => <span>{formatInstant(row.timestamp)}</span>,
    },
    {
      key: "target",
      header: "Hedef",
      width: "22ch",
      // Hedef kimliği cihaz adı ya da depo yolu olabiliyor; serbest metin.
      freeText: true,
      render: (row) => (
        <span>
          <Badge>{row.target_kind}</Badge> {row.target_id}
        </span>
      ),
    },
    {
      key: "changeKind",
      header: "Tür",
      width: "14ch",
      render: (row) => <span>{row.change_kind}</span>,
    },
    {
      key: "summary",
      header: "Özet",
      // Özet Türkçe, Arapça ya da Çince gelebiliyor — hizalama içeriğe
      // bırakılıyor; `DataTable` bunu `dir="auto"` ile çözüyor.
      freeText: true,
      render: (row) =>
        row.external_ref ? (
          <a href={row.external_ref} rel="noreferrer noopener" target="_blank">
            {row.summary || row.external_ref}
          </a>
        ) : (
          <span>{row.summary || "—"}</span>
        ),
    },
    {
      key: "actor",
      header: "Kim",
      width: "16ch",
      freeText: true,
      render: (row) => <span>{row.actor || "—"}</span>,
    },
    {
      key: "source",
      header: "Kaynak",
      width: "12ch",
      render: (row) => (
        <Badge tone={row.source === "manual" ? "neutral" : "accent"}>{row.source}</Badge>
      ),
    },
  ];

  return (
    <DataTable
      caption="Son değişiklikler"
      columns={columns}
      rows={rows}
      rowKey={(row) => row.change_id}
    />
  );
}
