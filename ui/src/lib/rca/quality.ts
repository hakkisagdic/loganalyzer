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
  /**
   * Rank sorusunun <b>sorulduğu</b> inceleme sayısı — <c>accuracy@k</c>'nın
   * paydası. Soruyu <b>soramayan</b> yakalama yolları (bulguları göstermeyen
   * ekranlar) burada yok, ve ayrım sunucuda açık bir alandan geliyor.
   */
  readonly rankAsked: number;
  /** Doğru bulgu ilk sıradaydı. */
  readonly accuracyAtOne: RatioDisplay;
  /** Doğru bulgu ilk üçteydi. Aynı alandan çıkıyor, ikinci bir eksen yok. */
  readonly accuracyAtThree: RatioDisplay;

  // --- Karar 1: atılan cümle oranı (T47) -----------------------------------

  /**
   * İncelenmiş <b>ayrık</b> paket sayısı — altın kümenin büyüklüğü.
   */
  readonly reviewedBundles: number;
  /**
   * <b>A</b> · incelenmiş ama raporu olmayan paket: model hiç koşmadı.
   * Oranın paydasına <b>girmiyor</b>.
   */
  readonly reasoningAbsent: number;
  /** Ölçüme giren rapor — paket başına bir tane (sonuncusu). */
  readonly reportsMeasured: number;
  /** <b>B</b> · koştu, hiç cümle üretmedi. */
  readonly producedNothing: number;
  /**
   * <b>C</b> · koştu, ürettiklerinin <b>hepsi atıldı</b>. B ile saf sayımda
   * aynı görünüyor (ikisi de boş bulgu listesi) ve <b>en pahalısı</b> bu.
   */
  readonly allDropped: number;
  /** Toplam üretilen cümle — oranın paydası. */
  readonly producedSentences: number;
  /** Atılan cümle oranı; payda cümle, rapor değil. */
  readonly droppedSentenceRatio: RatioDisplay;
  /** Atılan cümlelerin kaçta kaçı atıf uydurmuştu; paydası atılanlar. */
  readonly fabricatedCitationRatio: RatioDisplay;
  /**
   * İncelenmiş paketlerin kaçta kaçı ölçülebildi — <b>kapsamın kendisi</b>.
   * Düşükse üstteki oran altın kümenin küçük bir diliminden geliyor.
   */
  readonly measuredCoverage: RatioDisplay;
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
    rankAsked: count(quality.rank_asked),
    accuracyAtOne: ratio(quality.accuracy_at_one, "bulgu sırası sorulmadı"),
    accuracyAtThree: ratio(quality.accuracy_at_three, "bulgu sırası sorulmadı"),
    reviewedBundles: count(quality.reviewed_bundles),
    reasoningAbsent: count(quality.reasoning_absent),
    reportsMeasured: count(quality.reports_measured),
    producedNothing: count(quality.produced_nothing),
    allDropped: count(quality.all_dropped),
    producedSentences: count(quality.produced_sentences),
    droppedSentenceRatio: ratio(quality.dropped_sentence_ratio, "model hiç koşmadı"),
    fabricatedCitationRatio: ratio(quality.fabricated_citation_ratio, "atılan cümle yok"),
    measuredCoverage: ratio(quality.measured_coverage, "incelenmiş paket yok"),
  };
}
