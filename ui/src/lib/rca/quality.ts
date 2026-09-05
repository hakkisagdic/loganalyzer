import type { components } from "@/lib/api/schema";

export type GoldenSetQuality = components["schemas"]["GoldenSetQualityResponse"];

/**
 * Altın küme göstergesi — <b>iki yönlü bir ayrım</b> (T38 ↔ T37).
 *
 * <p>
 * <c>accuracy</c> <c>null</c> olabiliyor ve bu <b>sıfırdan farklı</b>: karar
 * verilmiş inceleme yoksa oran <i>yoktur</i>. İkisini aynı göstermek "%0 doğru"
 * ile "henüz karar verilmedi"yi tek cümleye indirmek olurdu — ve yanlış olan
 * taraf inandırıcı olanı: ürünün hiç ölçülmemiş doğruluğu, ölçülmüş ve
 * berbat çıkmış gibi görünür.
 * </p>
 *
 * <p>
 * <b>Ters yön de aynı derecede önemli:</b> <c>accuracy</c> <b>0</b> ise bu
 * gerçek bir ölçüm ve <b>gösterilmek zorunda</b>. Gizlenen bir sıfır,
 * "ölçüldü, sıfır" ile "henüz ölçülmedi" farkını öbür yönden siler. Aynı
 * ayrımın kardeşi <c>WindowTrust</c>'ta: <c>measured</c> ayrı bir alan ve oran
 * ölçülmediyse <c>null</c>, sıfır değil.
 * </p>
 */

/** Bir oranın ekrandaki hâli: ölçülmüş bir sayı ya da "henüz yok". */
export type RatioDisplay =
  | { readonly kind: "ratio"; readonly percent: string }
  | { readonly kind: "undecided"; readonly label: string };

export interface QualityDisplay {
  /** Toplam inceleme — <b>0 olsa bile gösteriliyor</b>. */
  readonly total: number;
  /** Karar verilmiş inceleme (`unknown` hariç). */
  readonly decided: number;
  readonly correct: number;
  readonly unknown: number;
  readonly accuracy: RatioDisplay;
  readonly unknownRatio: RatioDisplay;
  /**
   * Çelişen kanıt boyutunda değerlendirilmiş inceleme sayısı — <c>Sound</c> +
   * <c>Trivial</c>. Oranın paydası bu, toplam inceleme <b>değil</b>.
   */
  readonly contradictingEvaluated: number;
  /**
   * "Çelişen kanıt tiyatrosu" oranı (RCA risk #5): değerlendirilmiş
   * bölümlerin kaçta kaçı önemsizdi.
   *
   * <p>
   * Burada <c>null</c> ile <c>0</c> ayrımı <c>accuracy</c>'dekinden daha
   * keskin, çünkü <b>iyi olan uç sıfır</b>: "%0 tiyatro" en iyi sonuç,
   * "değerlendirilmedi" ise hiçbir sonuç. İkisi aynı görünürse ölçülmemiş bir
   * boyut mükemmel diye okunur — ve göstergenin var olma sebebi tam olarak
   * bunu engellemek.
   * </p>
   */
  readonly contradictingTrivialRatio: RatioDisplay;
}

/**
 * Şema <c>int64</c>/<c>double</c> alanlarını <c>number | string</c> olarak
 * tipliyor (büyük tam sayılar JSON'da dizgi inebiliyor). Tek yerde
 * çeviriliyor.
 */
function count(value: number | string | null | undefined): number {
  const parsed = Number(value ?? 0);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Oran gösterimi.
 *
 * <p>
 * <c>null</c> <b>ve yalnızca</b> <c>null</c> "henüz yok" demek. <c>0</c> geçerli
 * bir ölçüm ve <c>%0</c> olarak görünüyor.
 * </p>
 */
function ratio(value: number | string | null | undefined, label: string): RatioDisplay {
  if (value === null || value === undefined) {
    return { kind: "undecided", label };
  }

  const parsed = Number(value);

  if (!Number.isFinite(parsed)) {
    return { kind: "undecided", label };
  }

  return { kind: "ratio", percent: `%${(parsed * 100).toFixed(1)}` };
}

export function presentQuality(quality: GoldenSetQuality): QualityDisplay {
  return {
    total: count(quality.total),
    decided: count(quality.decided),
    correct: count(quality.correct),
    unknown: count(quality.unknown),
    accuracy: ratio(quality.accuracy, "henüz karar verilmedi"),
    unknownRatio: ratio(quality.unknown_ratio, "inceleme yok"),
    contradictingEvaluated: count(quality.contradicting_evaluated),
    contradictingTrivialRatio: ratio(
      quality.contradicting_trivial_ratio,
      "çelişen kanıt değerlendirilmedi",
    ),
  };
}
