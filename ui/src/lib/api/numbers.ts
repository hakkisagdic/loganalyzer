/**
 * Şemadaki sayısal alanı `number`'a çeviriyor.
 *
 * <p>
 * .NET 10'un OpenAPI üreticisi `int`, `long` ve `double` alanları
 * `type: [integer, string]` olarak yazıyor — JSON'da dizge kodlanmış sayıları
 * da kabul etmek için. Üretilen TypeScript tipi bu yüzden `number | string` ve
 * bu <b>her</b> ekranın karşılaştığı bir şey, tek bir ekranın derdi değil.
 * </p>
 *
 * <p>
 * Dönüşüm <b>tek yerde</b>: her kullanım yerinde `Number(...)` yazmak, birinde
 * unutulduğu gün `"12" > 9` gibi dizge karşılaştırmasına düşmek demekti — ve o
 * hata sessizce yanlış bir sayı üretirdi. T23'te alarm ekranının içinde doğdu,
 * T19'da ikinci tüketici gelince buraya taşındı.
 * </p>
 */
export function toNumber(value: number | string | null | undefined): number {
  if (typeof value === "number") return value;
  if (value === null || value === undefined) return 0;

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}
