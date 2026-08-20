import { presentQuality, type GoldenSetQuality } from "@/lib/rca/quality";

import styles from "./rca.module.css";

/**
 * Altın küme göstergesi — ekranın köşesi (T38'in ucu, T37'nin ekranı).
 *
 * <p>
 * <b>Boş kümede gizlenmiyor.</b> Gösterge yalnızca veri varken çizilseydi,
 * "henüz kimse inceleme yapmadı" ile "gösterge bozuk" aynı boşluğa düşerdi —
 * ve inceleme yorgunluğu riskinin (RCA #2) tam olarak görünmesi gereken yer
 * burası: sayı sıfırsa <b>sıfır olduğu görünmeli</b>.
 * </p>
 *
 * <p>
 * <b>Doğruluk oranı <c>null</c> iken <c>%0</c> yazmıyor.</b> Karar verilmiş
 * inceleme yoksa oran <i>yoktur</i>; "%0 doğru" yazmak, hiç ölçülmemiş bir
 * doğruluğu ölçülmüş ve berbat çıkmış gibi göstermek olurdu. Ters yön de
 * korunuyor: gerçek bir <c>0</c> ölçümü <b>gizlenmiyor</b>.
 * </p>
 *
 * <p>
 * <c>quality</c> <c>null</c> ise gösterge <b>yine duruyor</b> ve okunamadığını
 * söylüyor — sessizce kaybolan bir gösterge, sıfır gösteren bir göstergeden
 * kötü.
 * </p>
 */
export interface QualityBadgeProps {
  readonly quality: GoldenSetQuality | null;
  readonly error: string | null;
}

export function QualityBadge({ quality, error }: QualityBadgeProps) {
  if (!quality) {
    return (
      <aside className={styles.quality} aria-label="Altın küme göstergesi" data-quality="unavailable">
        <span className={styles.qualityTitle}>Altın küme</span>
        <span className={styles.quiet}>{error ?? "Gösterge okunamadı."}</span>
      </aside>
    );
  }

  const display = presentQuality(quality);

  return (
    <aside className={styles.quality} aria-label="Altın küme göstergesi" data-quality="ready">
      <span className={styles.qualityTitle}>Altın küme</span>

      <dl className={styles.qualityGrid}>
        <div>
          <dt>İnceleme</dt>
          {/* 0 da bir sayı ve görünüyor. */}
          <dd data-field="total">{display.total}</dd>
        </div>

        <div>
          <dt>Karar verilmiş</dt>
          <dd data-field="decided">{display.decided}</dd>
        </div>

        <div>
          <dt>Doğruluk</dt>
          <dd data-field="accuracy" data-kind={display.accuracy.kind}>
            {display.accuracy.kind === "ratio" ? (
              display.accuracy.percent
            ) : (
              <span className={styles.quiet}>{display.accuracy.label}</span>
            )}
          </dd>
        </div>

        <div>
          <dt>&quot;Bilmiyorum&quot;</dt>
          <dd data-field="unknown_ratio" data-kind={display.unknownRatio.kind}>
            {display.unknownRatio.kind === "ratio" ? (
              display.unknownRatio.percent
            ) : (
              <span className={styles.quiet}>{display.unknownRatio.label}</span>
            )}
          </dd>
        </div>
      </dl>

      <p className={styles.quiet}>
        &quot;Bilmiyorum&quot; oranı kendisi bir gösterge: yüksekse ya kanıt paketi
        yetersiz ya soru yanlış soruluyor.
      </p>
    </aside>
  );
}
