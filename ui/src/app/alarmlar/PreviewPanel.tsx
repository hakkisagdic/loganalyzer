"use client";

import { useMemo } from "react";

import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { chartScale, countFirings, countSilenceFirings, matches, silentSources } from "@/lib/alerts/preview";
import {
  describeSeconds,
  formatInstant,
  toNumber,
  type AlertPreview,
  type Comparison,
} from "@/lib/alerts/types";

import styles from "./alerts.module.css";

export interface PreviewPanelProps {
  readonly preview: AlertPreview | null;
  readonly loading: boolean;
  readonly error: string | null;
  readonly threshold: number;
  readonly comparison: Comparison;
  readonly isSilence: boolean;
}

/**
 * Önizleme paneli — "bu kural son 24 saatte kaç kez tetiklenirdi" (T23).
 *
 * <p>
 * <b>Sayı burada hesaplanıyor, sunucudan gelmiyor.</b> Sunucu eşikten bağımsız
 * bir seri döndürüyor; eşik değiştiğinde bu bileşen aynı seriyi yeniden
 * yorumluyor ve <b>hiçbir istek atmıyor</b>. Kabul kriteri "eşik değiştikçe
 * sayı güncelleniyor" tam olarak bunu istiyor ve alternatifi — her değişimde
 * sunucuya sormak — K16'nın uyardığı yükü kullanıcının parmağına bağlardı.
 * </p>
 *
 * <p>
 * Dört durum da çizilmiş: yükleniyor, hata, boş ve dolu. F1'in dersinin
 * arayüz karşılığı, üçünü unutup yalnızca doluyu çizmek.
 * </p>
 */
export function PreviewPanel({
  preview,
  loading,
  error,
  threshold,
  comparison,
  isSilence,
}: PreviewPanelProps) {
  const firings = useMemo(() => {
    if (!preview) {
      return 0;
    }

    return isSilence
      ? countSilenceFirings(preview.sources, threshold)
      : countFirings(preview.points, threshold, comparison);
  }, [preview, threshold, comparison, isSilence]);

  const silent = useMemo(
    () => (preview && isSilence ? silentSources(preview.sources, threshold) : []),
    [preview, isSilence, threshold],
  );

  const scale = useMemo(
    () => (preview ? chartScale(preview.points, threshold) : 1),
    [preview, threshold],
  );

  if (loading && !preview) {
    return (
      <Card>
        <LoadingState label="Önizleme hesaplanıyor…" rows={3} />
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <ErrorState
          title="Önizleme alınamadı"
          hint={error}
        />
      </Card>
    );
  }

  if (!preview) {
    return (
      <Card>
        <EmptyState
          title="Önizleme yok"
          description="Kapsam seçtiğinizde kural geçmiş veriye karşı koşturulur."
        />
      </Card>
    );
  }

  const window = `${formatInstant(preview.from)} – ${formatInstant(preview.to)}`;

  return (
    <Card>
      <div className={styles.stack}>
        <div className={styles.previewHead}>
          <h2>Önizleme</h2>

          {/*
            `aria-live`: sayı eşik değiştikçe güncelleniyor ve bu, ekran
            okuyucu kullanan biri için sessiz bir değişim olurdu.
          */}
          <span className={styles.previewCount} aria-live="polite">
            {firings}
          </span>
          <span>kez tetiklenirdi</span>

          {loading ? <Badge>güncelleniyor…</Badge> : null}
        </div>

        <p className={styles.previewNote}>
          {window} · kova {describeSeconds(preview.bucket_seconds)}
          {preview.note ? ` · ${preview.note}` : ""}
        </p>

        {isSilence ? (
          silent.length === 0 ? (
            <EmptyState
              title="Bu eşikte hiçbir kaynak susmuş sayılmazdı"
              description="Eşiği düşürerek hangi kaynakların yakalanacağını görebilirsiniz."
            />
          ) : (
            <DataTable
              caption={`Eşiği aşan kaynaklar (${describeSeconds(threshold)} ve üzeri sessizlik)`}
              rowKey={(row) => row.source_id}
              rows={silent}
              columns={[
                { key: "source", header: "Kaynak", width: "30%", render: (row) => row.source_id },
                { key: "group", header: "Grup", width: "20%", render: (row) => row.owner_group },
                {
                  key: "longest",
                  header: "En uzun sessizlik",
                  width: "20%",
                  numeric: true,
                  render: (row) => describeSeconds(row.longestGap),
                },
                {
                  key: "count",
                  header: "Kaç kez",
                  width: "15%",
                  numeric: true,
                  render: (row) => row.gapCount,
                },
                {
                  key: "last",
                  header: "Son görülme",
                  width: "15%",
                  render: (row) => formatInstant(row.last_seen),
                },
              ]}
            />
          )
        ) : preview.points.length === 0 ? (
          <div className={styles.chartEmpty}>Seçilen pencerede hiç olay yok.</div>
        ) : (
          <>
            {/*
              Grafik `aria-hidden`: aynı bilgi hemen üstünde sayı olarak ve
              altında metin olarak var. Ekran okuyucuya yüzlerce çubuğu tek tek
              okutmak, bilgiyi artırmadan gürültü üretirdi.
            */}
            <div className={styles.chart} aria-hidden="true">
              {preview.points.map((point) => {
                const value = toNumber(point.value);
                const firing = matches(value, threshold, comparison);
                const height = Math.max(2, Math.round((value / scale) * 100));

                return (
                  <div
                    key={point.at}
                    className={`${styles.bar} ${firing ? styles.barFiring : ""}`}
                    style={{ blockSize: `${height}%` }}
                    title={`${formatInstant(point.at)} — ${point.count}`}
                  />
                );
              })}
            </div>

            <p className={styles.previewNote}>
              {preview.points.length} kovanın {firings} tanesi eşiği aşıyor. Kırmızı çubuklar
              tetiklenecek pencereler.
            </p>
          </>
        )}
      </div>
    </Card>
  );
}
