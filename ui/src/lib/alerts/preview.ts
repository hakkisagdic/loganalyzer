import { toNumber, type Comparison, type PreviewPoint, type PreviewSource } from "./types";

/**
 * Önizlemenin eşik hesabı — **saf fonksiyonlar**.
 *
 * <p>
 * T23'ün kabul kriteri "eşik değiştikçe sayı güncelleniyor" diyor ve bunun
 * <b>nasıl</b> yapıldığı ticket'ın asıl kararı. Sunucu eşikten <b>bağımsız</b>
 * bir histogram döndürüyor; eşik karşılaştırması burada, tarayıcıda yapılıyor.
 * Yani kaydırıcı hareket ettiğinde ağa hiç çıkılmıyor.
 * </p>
 *
 * <p>
 * Alternatifi — her eşik değişiminde sunucuya sormak — K16'nın uyardığı şeyin
 * ta kendisiydi: kaydırıcıyı sürükleyen tek bir kullanıcı saniyede onlarca ağır
 * ClickHouse sorgusu üretirdi ve gürültüyü önlemek için yazılan ekran, gürültü
 * kaynağının kendisi olurdu.
 * </p>
 *
 * <p>
 * Karşılaştırma kümesi motorunkiyle <b>aynı</b> (`AlertEvaluator.Matches`).
 * Ayrışmaları, önizlemenin motorun yapmayacağı bir şeyi vaat etmesi demek —
 * ve önizlemenin tek işi o vaadi doğru vermek.
 * </p>
 */
export function matches(value: number, threshold: number, comparison: Comparison): boolean {
  switch (comparison) {
    case "gt":
      return value > threshold;
    case "gte":
      return value >= threshold;
    case "lt":
      return value < threshold;
    case "lte":
      return value <= threshold;
    default:
      return false;
  }
}

/** Eşik/oran: kaç kova eşiği aşıyor. */
export function countFirings(
  points: readonly PreviewPoint[],
  threshold: number,
  comparison: Comparison,
): number {
  return points.reduce(
    (total, point) => (matches(toNumber(point.value), threshold, comparison) ? total + 1 : total),
    0,
  );
}

/**
 * Sessizlik: eşiği aşan **boşluk** sayısı.
 *
 * <p>Kaynak sayısı değil boşluk sayısı: aynı cihaz gün içinde üç kez susmuşsa
 * kural üç kez tetiklenirdi ve kullanıcının görmesi gereken sayı o.</p>
 */
export function countSilenceFirings(
  sources: readonly PreviewSource[],
  silenceSeconds: number,
): number {
  return sources.reduce(
    (total, source) => total + source.gaps_seconds.filter((gap) => toNumber(gap) >= silenceSeconds).length,
    0,
  );
}

/** Eşiği aşan en az bir boşluğu olan kaynaklar, en uzun sessizliğe göre sıralı. */
export function silentSources(
  sources: readonly PreviewSource[],
  silenceSeconds: number,
): readonly (PreviewSource & { readonly longestGap: number; readonly gapCount: number })[] {
  return sources
    .map((source) => {
      const exceeded = source.gaps_seconds.map(toNumber).filter((gap) => gap >= silenceSeconds);

      return {
        ...source,
        gapCount: exceeded.length,
        longestGap: exceeded.length > 0 ? Math.max(...exceeded) : 0,
      };
    })
    .filter((source) => source.gapCount > 0)
    .sort((left, right) => right.longestGap - left.longestGap);
}

/**
 * Çubuk yüksekliklerinin ölçeği.
 *
 * <p>
 * Eşik tepe değerin üstündeyse ölçek eşiğe göre alınıyor — yoksa eşik çizgisi
 * grafiğin dışında kalır ve kullanıcı "eşiğim verinin neresinde" sorusunu tam
 * da en çok ihtiyaç duyduğu anda (hiç tetiklenmeyen kuralda) cevaplayamaz.
 * </p>
 */
export function chartScale(points: readonly PreviewPoint[], threshold: number): number {
  const peak = points.reduce((max, point) => Math.max(max, toNumber(point.value)), 0);
  const scale = Math.max(peak, Number.isFinite(threshold) ? threshold : 0);

  // Sıfır ölçek bölme hatası verir; tamamen boş seride bir kullanıyoruz.
  return scale > 0 ? scale : 1;
}
