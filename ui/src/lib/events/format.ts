/**
 * Ekranda gösterilen biçimler. Tek yerde, çünkü aynı değerin iki ekranda
 * farklı görünmesi "başka bir olaya bakıyorum" hissi veriyor.
 */

/**
 * Zaman damgası — **UTC**, saniye hassasiyetinde.
 *
 * <p>
 * Yerel saat dilimine çevirmiyoruz ve bu bilinçli: veri UTC'de saklanıyor,
 * cihazlar farklı dilimlerde ve bir olayı ekipçe konuşurken tek ortak ölçek
 * gerekiyor. F1'de aynı anı gösteren iki damganın farklı ofsetlerle geldiği
 * ölçüldü; ekranda dilim değiştirmek o karışıklığı geri getirirdi.
 * </p>
 */
export function formatTimestamp(value: string): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return `${date.toISOString().slice(0, 19).replace("T", " ")}Z`;
}

/** `datetime-local` alanının beklediği biçim (saniyesiz, dilimsiz). */
export function toDateTimeLocal(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toISOString().slice(0, 16);
}

const SEVERITY_LABELS: Record<number, string> = {
  0: "belirtilmemiş",
  1: "bilgi",
  2: "düşük",
  3: "orta",
  4: "yüksek",
  5: "kritik",
  6: "ölümcül",
};

/**
 * OCSF önem ölçeği. <b>0 "düşük" değil "belirtilmemiş"</b> — ikisini aynı
 * göstermek, hiç önem taşımayan bir olayı önemsiz sanmaya yol açıyor.
 */
export function formatSeverity(value: number | string): string {
  const numeric = typeof value === "number" ? value : Number.parseInt(value, 10);
  return SEVERITY_LABELS[numeric] ?? String(value);
}

const PARSE_STATUS_LABELS: Record<string, string> = {
  ok: "tam",
  partial: "kısmi",
  failed: "çözülemedi",
};

export function formatParseStatus(value: string): string {
  return PARSE_STATUS_LABELS[value] ?? value;
}

/**
 * Parser'ın şikâyetleri — <c>attrs['bizigo.parse_issues']</c>.
 *
 * <p>Normalizasyon tek anahtarda birleştiriyor (<c>mapKeys</c> bloom filtresi
 * anahtar kümesi üzerinde); burada adım/mesaj çiftlerine geri açılıyor.</p>
 */
export interface ParseIssue {
  readonly step: string;
  readonly message: string;
}

export const PARSE_ISSUES_KEY = "bizigo.parse_issues";

export function readParseIssues(attrs: Record<string, string>): ParseIssue[] {
  const packed = attrs[PARSE_ISSUES_KEY];

  if (!packed) {
    return [];
  }

  return packed
    .split(" | ")
    .map((entry) => {
      const separator = entry.indexOf(": ");

      return separator === -1
        ? { step: "—", message: entry }
        : { step: entry.slice(0, separator), message: entry.slice(separator + 2) };
    })
    .filter((issue) => issue.message.length > 0);
}
