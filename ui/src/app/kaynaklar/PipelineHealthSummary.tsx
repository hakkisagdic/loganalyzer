import { Badge, Card } from "@/components/ui/Field";
import type { PipelineHealth } from "@/lib/api/client";

import styles from "./inventory.module.css";

/**
 * `/v1/health/pipeline` göstergelerinin <b>özeti</b> (T17).
 *
 * <p>
 * Bu göstergelerin ortak yanı, hiçbirinin arıza anında alarm üretmemesi: sistem
 * çalışmaya devam ediyor. <c>bound_ratio</c> düşse de satırlar ayrıştırılıyor,
 * WAL birikse de veri kaybolmuyor, sidecar ölse de ingest akıyor. Hepsi sessiz
 * çürüme sınıfından — bu blok olmadan kimse fark etmiyor.
 * </p>
 *
 * <p>
 * Ekran <b>tam sağlık ekranı değil</b> (o T20'nin işi); envanterin yanında
 * durmasının sebebi, buradaki iki göstergenin doğrudan envanter bakımıyla
 * ilgili olması: <c>bound_ratio</c> ve <c>unassigned_sources</c>.
 * </p>
 */
export function PipelineHealthSummary({ health }: { health: PipelineHealth }) {
  const indicators = [
    {
      key: "bound",
      label: "Parser bağlama oranı",
      value: `${(Number(health.dispatch.bound_ratio) * 100).toFixed(1)}%`,
      healthy: health.dispatch.bound_ratio_healthy,
      // Envanter bakımsız kalınca düşen gösterge tam olarak bu.
      hint: `hedef ${(Number(health.dispatch.bound_ratio_target) * 100).toFixed(0)}% — düşerse envanterde eksik eşleme var`,
    },
    {
      key: "unassigned",
      label: "Eşleşmeyen kaynak",
      value: String(health.inventory.unassigned_sources),
      healthy: health.inventory.unassigned_sources === 0,
      hint: "envanterde karşılığı olmayan cihaz sayısı",
    },
    {
      key: "wal",
      label: "WAL",
      value: health.wal.is_full ? "dolu" : `${formatBytes(Number(health.wal.total_bytes))}`,
      healthy: !health.wal.is_full,
      hint: "dolarsa ingest 503 döner ve cihazlar geri basılır",
    },
    {
      key: "encoding",
      label: "Kodlama uyuşmazlığı",
      value: String(health.ingest.declared_encoding_mismatches),
      healthy: Number(health.ingest.declared_encoding_mismatches) === 0,
      // Doğrudan bu ekranın düzelteceği bir şey: envanterdeki `encoding` yanlış.
      hint: "sıfırdan büyükse envanterdeki encoding değeri yanlış",
    },
    {
      key: "archive",
      label: "Ham arşiv",
      value: health.archive.healthy ? "sağlıklı" : "dikkat",
      healthy: health.archive.healthy,
      hint: "kayıp ya da bozuk nesne replay gününde değil bugün görünmeli",
    },
    {
      key: "sidecar",
      label: "Keşif sidecar'ı",
      value: health.sidecar.enabled ? health.sidecar.circuit : "kapalı",
      healthy: !health.sidecar.enabled || health.sidecar.circuit !== "Open",
      hint: "sıcak yolda değil; arızası yalnızca template_id'yi boş bırakır",
    },
  ];

  return (
    <Card>
      <p className={styles.blockTitle}>Boru hattı özeti</p>

      <div className={styles.healthGrid}>
        {indicators.map((indicator) => (
          <div key={indicator.key} className={styles.indicator}>
            <span className={styles.definitionTerm}>{indicator.label}</span>
            <span className={styles.indicatorValue}>
              {indicator.value}{" "}
              {/* Durum renkle DEĞİL metinle de anlatılıyor (WCAG 1.4.1). */}
              <Badge tone={indicator.healthy ? "success" : "warning"}>
                {indicator.healthy ? "iyi" : "bak"}
              </Badge>
            </span>
            <span className={styles.muted}>{indicator.hint}</span>
          </div>
        ))}
      </div>
    </Card>
  );
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value)) {
    return "—";
  }

  const units = ["B", "KiB", "MiB", "GiB"];
  let size = value;
  let unit = 0;

  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }

  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}
