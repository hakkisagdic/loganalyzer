/**
 * Zaman damgasının **tek biçimi** (T28 denetimi).
 *
 * <p>
 * Denetim üç ayrı biçimlendirici buldu ve üçü farklı çıktı veriyordu:
 * </p>
 *
 * <table>
 *   <tr><th>Nerede</th><th>Biçim</th></tr>
 *   <tr><td>olay ekranları</td><td>UTC, saniyeli, <c>Z</c> ekli</td></tr>
 *   <tr><td>değişiklik tablosu</td><td>UTC, <b>dakika</b> hassasiyeti, <c>Z</c> yok</td></tr>
 *   <tr><td>alarm ekranları</td><td><b>yerel saat</b>, <c>tr-TR</c> biçimi</td></tr>
 * </table>
 *
 * <p>
 * Üçüncüsü kozmetik bir tutarsızlık değil: bir alarm tetiklenmesini log
 * satırıyla eşleştiren kullanıcı, <b>saat farkı kadar</b> sapmış iki zaman
 * görüyor ve <b>hiçbir yerde bunun yazmıyor</b>. İstanbul'da üç saat. Bu, log
 * analizi yapan bir üründe doğrudan yanlış sonuç üretiyor.
 * </p>
 *
 * <p>
 * Seçilen biçim <b>UTC ve açıkça işaretli</b>. Yerel saate çevirmiyoruz: veri
 * UTC'de saklanıyor, cihazlar farklı dilimlerde ve bir olayı ekipçe konuşurken
 * tek ortak ölçek gerekiyor. F1'de aynı anı gösteren iki damganın farklı
 * ofsetlerle geldiği ölçüldü. <c>Z</c> soneki pazarlık dışı — onsuz kullanıcı
 * hangi dilimde olduğunu bilemiyor ve tam da bu karışıklık doğuyor.
 * </p>
 */

/** `2026-08-16 12:30:00Z` — UTC, saniye hassasiyetinde, dilim açıkça yazılı. */
export function formatInstant(value: string | null | undefined): string {
  if (!value) {
    return "—";
  }

  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    // Çözülemeyen değeri "—" ile gizlemiyoruz: bozuk bir damga, gösterilmeyen
    // bir damgadan daha çok bilgi taşıyor.
    return value;
  }

  return `${date.toISOString().slice(0, 19).replace("T", " ")}Z`;
}

/** `datetime-local` alanının beklediği biçim (saniyesiz, dilimsiz). */
export function toDateTimeLocal(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toISOString().slice(0, 16);
}

/**
 * "3 saat önce" — geçmiş süre.
 *
 * <p><c>now</c> dışarıdan geliyor: her satırın kendi <c>new Date()</c>'ini
 * çağırması, uzun bir listede satırlar arasında saniyelik tutarsızlık
 * üretirdi.</p>
 */
export function formatSince(timestamp: string, now: Date): string {
  const then = Date.parse(timestamp);

  if (Number.isNaN(then)) {
    return timestamp;
  }

  const seconds = Math.max(0, Math.round((now.getTime() - then) / 1000));

  if (seconds < 60) {
    return "az önce";
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes} dakika önce`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    return `${hours} saat önce`;
  }

  return `${Math.floor(hours / 24)} gün önce`;
}
