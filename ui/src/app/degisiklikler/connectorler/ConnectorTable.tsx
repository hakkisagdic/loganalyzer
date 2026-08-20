import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { CREDENTIAL_MASK, type ConnectorSummary } from "@/lib/changes/connector";

import styles from "../changes.module.css";

export interface ConnectorTableProps {
  readonly rows: readonly ConnectorSummary[];
  readonly canManage: boolean;
  readonly testingId: string | null;
  readonly onTest: (connector: ConnectorSummary) => void;
  readonly onToggle: (connector: ConnectorSummary) => void;
}

/**
 * Connector listesinin **çizilen** kısmı — durum taşımıyor.
 *
 * <p>
 * <b>Kimlik bilgisi sütunu değeri değil varlığını gösteriyor.</b> Sunucu zaten
 * yalnızca <c>credential_set</c> ve sabit bir maske döndürüyor; ekran maskeyi
 * bile basmıyor, çünkü basılan bir maske bir sonraki düzenlemede geri
 * gönderilebilecek bir metin hâline gelir. Sütun bir rozet gösteriyor: kayıtlı
 * ya da yok.
 * </p>
 */
export function ConnectorTable({
  rows,
  canManage,
  testingId,
  onTest,
  onToggle,
}: ConnectorTableProps) {
  const columns: readonly Column<ConnectorSummary>[] = [
    {
      key: "name",
      header: "Ad",
      width: "22ch",
      freeText: true,
      render: (row) => (
        <span>
          <strong>{row.name}</strong>
          <br />
          <code>{row.slug}</code>
        </span>
      ),
    },
    {
      key: "type",
      header: "Tip",
      width: "14ch",
      render: (row) => <Badge tone="accent">{row.connector_type}</Badge>,
    },
    {
      key: "group",
      header: "Grup",
      width: "16ch",
      freeText: true,
      render: (row) => <span>{row.owner_group}</span>,
    },
    {
      key: "credential",
      header: "Kimlik bilgisi",
      width: "14ch",
      render: (row) =>
        row.credential_set ? (
          <Badge tone="success">kayıtlı</Badge>
        ) : (
          <Badge tone="warning">yok</Badge>
        ),
    },
    {
      key: "state",
      header: "Durum",
      width: "18ch",
      render: (row) => (
        <span>
          {row.enabled ? <Badge tone="success">etkin</Badge> : <Badge>pasif</Badge>}{" "}
          {row.last_run_state === "Failed" ? <Badge tone="danger">son koşum düştü</Badge> : null}
        </span>
      ),
    },
    {
      key: "actions",
      header: "İşlem",
      width: "20ch",
      render: (row) => (
        <span className={styles.toolbar}>
          <Button onClick={() => onTest(row)} disabled={testingId === row.id}>
            {testingId === row.id ? "Deneniyor…" : "Bağlantıyı dene"}
          </Button>
          {canManage ? (
            <Button variant={row.enabled ? "ghost" : "primary"} onClick={() => onToggle(row)}>
              {row.enabled ? "Pasife al" : "Etkinleştir"}
            </Button>
          ) : null}
        </span>
      ),
    },
  ];

  return (
    <DataTable
      caption="Değişiklik connector'ları"
      columns={columns}
      rows={rows}
      rowKey={(row) => row.id}
    />
  );
}

/**
 * Maskenin ekranda **basılmadığını** dışarıdan da okunabilir kılan sabit.
 * Testler bunu arayıp çizilen HTML'de bulunmadığını doğruluyor.
 */
export const CredentialMaskNeverRendered = CREDENTIAL_MASK;
