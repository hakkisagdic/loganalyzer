import type { ReactNode } from "react";

import styles from "./ui.module.css";

export interface Column<TRow> {
  readonly key: string;
  readonly header: string;
  /** Sütun genişliği (CSS). `table-layout: fixed` olduğu için verilmesi önerilir. */
  readonly width?: string;
  /** Sayısal sütunlar sağa yaslanıp tablo rakamlarıyla hizalanıyor. */
  readonly numeric?: boolean;
  /**
   * Log gövdesi gibi serbest metin sütunları.
   *
   * <p>Bunlar tek aralıklı, dört satırda kesilen ve `dir="auto"` taşıyan
   * hücrelerde gösteriliyor — Arapça bir gövde soldan sağa hizalanırsa
   * okunamaz hâle geliyor.</p>
   */
  readonly freeText?: boolean;
  readonly render: (row: TRow) => ReactNode;
}

export interface DataTableProps<TRow> {
  /** Ekran okuyucunun tabloyu tanıması için — görsel olarak da yararlı. */
  readonly caption: string;
  readonly columns: readonly Column<TRow>[];
  readonly rows: readonly TRow[];
  readonly rowKey: (row: TRow) => string;
}

/**
 * Ortak tablo.
 *
 * <p>
 * Çok dilli gövde riski bileşenin içinde çözülüyor (`ui.module.css` — Tablo
 * bölümü): boşluksuz CJK kırılıyor, sağdan sola metin `dir="auto"` ile doğru
 * hizalanıyor, aşırı uzun gövde dört satırda kesiliyor. Bunları her ekranın
 * ayrı ayrı hatırlaması beklenmiyor.
 * </p>
 */
export function DataTable<TRow>({ caption, columns, rows, rowKey }: DataTableProps<TRow>) {
  return (
    <div className={styles.tableWrap} tabIndex={0} role="region" aria-label={caption}>
      <table className={styles.table}>
        <caption>{caption}</caption>
        <colgroup>
          {columns.map((column) => (
            <col key={column.key} style={column.width ? { width: column.width } : undefined} />
          ))}
        </colgroup>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.key} scope="col">
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((column) => (
                <td
                  key={column.key}
                  className={
                    [column.numeric ? styles.cellNumeric : null, column.freeText ? styles.cellBody : null]
                      .filter(Boolean)
                      .join(" ") || undefined
                  }
                  // Yazı yönünü içeriğe bırakıyoruz: Arapça gövde sağdan sola,
                  // Türkçe gövde soldan sağa. Sabit `ltr` Arapçayı okunamaz kılar.
                  dir={column.freeText ? "auto" : undefined}
                >
                  {/*
                    Serbest metin ayrı bir öğede: kırpma kuralları hücreye
                    uygulanınca `<td>` tablo hücresi olmaktan çıkıyor ve satır
                    yüksekliği yanlış hesaplanıyor (bkz. ui.module.css).
                  */}
                  {column.freeText ? (
                    <span className={styles.cellBodyText}>{column.render(row)}</span>
                  ) : (
                    column.render(row)
                  )}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
