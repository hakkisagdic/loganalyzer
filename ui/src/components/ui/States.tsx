import type { ReactNode } from "react";

import styles from "./ui.module.css";

/**
 * Her ekranın dört durumu var: yükleniyor, boş, hata, dolu. F1'in dersi
 * "doğrulanmamış her katman kırıktı" idi; arayüz karşılığı, üçünü unutup
 * yalnızca doluyu çizmek. Üçü burada hazır duruyor ki unutulmaları bilinçli
 * bir tercih olsun. T28 bu dört durumu ekran ekran denetleyecek.
 */

export interface EmptyStateProps {
  readonly title: string;
  readonly description?: string;
  /** Kullanıcıyı çıkışa götüren eylem — boş bir ekran çıkmaz sokak olmamalı. */
  readonly action?: ReactNode;
}

export function EmptyState({ title, description, action }: EmptyStateProps) {
  return (
    <div className={styles.state}>
      <p className={styles.stateTitle}>{title}</p>
      {description ? <p className={styles.stateBody}>{description}</p> : null}
      {action}
    </div>
  );
}

export interface ErrorStateProps {
  readonly title: string;
  /**
   * F1'de yerleşen `{ error, hint }` gövdesindeki `hint`.
   *
   * <p>Ayrı gösteriliyor çünkü işlevi farklı: `error` ne olduğunu, `hint` ne
   * yapılacağını söylüyor. İkisini tek paragrafta birleştirmek, eyleme
   * çağrıyı hata metninin içinde kaybediyor.</p>
   */
  readonly hint?: string;
  readonly action?: ReactNode;
}

export function ErrorState({ title, hint, action }: ErrorStateProps) {
  return (
    // `role="alert"` — ekran okuyucu hatayı odak değişmeden duyuruyor.
    <div className={`${styles.state} ${styles.stateError}`} role="alert">
      <p className={`${styles.stateTitle} ${styles.stateErrorTitle}`}>{title}</p>
      {hint ? <p className={styles.stateHint}>{hint}</p> : null}
      {action}
    </div>
  );
}

export interface LoadingStateProps {
  /** Ekran okuyucuya söylenen metin. Görsel iskelet ona bir şey anlatmıyor. */
  readonly label: string;
  readonly rows?: number;
}

export function LoadingState({ label, rows = 5 }: LoadingStateProps) {
  return (
    <div className={styles.skeletonRows} aria-busy="true" aria-live="polite">
      <span className="sr-only">{label}</span>
      {Array.from({ length: rows }, (_, index) => (
        <div
          key={index}
          className={styles.skeleton}
          // Eşit uzunlukta çubuklar yükleniyor gibi değil bozuk gibi duruyor;
          // değişken genişlik metin bekleniyor izlenimi veriyor.
          style={{ inlineSize: `${100 - (index % 3) * 12}%` }}
        />
      ))}
    </div>
  );
}
