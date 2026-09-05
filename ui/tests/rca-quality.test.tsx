import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { QualityBadge } from "@/app/rca/QualityBadge";
import { ApiError, describeError } from "@/lib/api/errors";
import { presentQuality, type GoldenSetQuality } from "@/lib/rca/quality";

/**
 * Altın küme göstergesi (T38'in ucu, T37'nin ekranı).
 *
 * <p>
 * <b>İki yönlü bir ayrım ve ikisi de sınanıyor.</b> Bir yön: <c>accuracy</c>
 * <c>null</c> iken <c>%0</c> yazmak, hiç ölçülmemiş bir doğruluğu ölçülmüş ve
 * berbat çıkmış gibi göstermek. Öbür yön: gerçek bir <c>0</c> ölçümünü
 * gizlemek, "ölçüldü, sıfır" ile "henüz ölçülmedi" farkını öbür taraftan
 * silmek.
 * </p>
 *
 * <p>
 * Yalnızca birini sınamak yetmez: <c>null</c>'ı gizleyen bir düzeltme, sıfırı
 * da gizlemeye çok yakın duruyor ve o hatanın belirtisi yok.
 * </p>
 */

function quality(overrides: Partial<GoldenSetQuality> = {}): GoldenSetQuality {
  return {
    total: 0,
    decided: 0,
    correct: 0,
    unknown: 0,
    accuracy: null,
    unknown_ratio: null,
    contradicting_sound: 0,
    contradicting_trivial: 0,
    contradicting_unknown: 0,
    contradicting_evaluated: 0,
    contradicting_trivial_ratio: null,
    contradicting_unspecified: 0,
    rank_asked: 0,
    accuracy_at_one: null,
    accuracy_at_three: null,
    reviewed_bundles: 0,
    reasoning_absent: 0,
    reports_measured: 0,
    produced_nothing: 0,
    all_dropped: 0,
    produced_sentences: 0,
    dropped_sentences: 0,
    fabricated_sentences: 0,
    dropped_sentence_ratio: null,
    fabricated_citation_ratio: null,
    measured_coverage: null,
    ...overrides,
  } as GoldenSetQuality;
}

describe("altın küme göstergesi", () => {
  /**
   * <b>Asıl bekçi.</b> Karar verilmiş inceleme yokken oran <i>yok</i> ve ekran
   * <c>%0</c> <b>yazmıyor</b>.
   */
  it("Karar_yokken_yuzde_sifir_yazmiyor", () => {
    const html = renderToStaticMarkup(<QualityBadge quality={quality()} error={null} />);

    expect(html).toContain("henüz karar verilmedi");
    expect(html).not.toContain("%0.0");
    expect(html).toContain('data-field="accuracy" data-kind="undecided"');
  });

  /**
   * <b>Ters yön.</b> Ölçülmüş bir sıfır <b>gizlenmiyor</b> — "ölçüldü, hiçbiri
   * doğru değil" kurulabilir ve kurulması gereken bir cümle.
   */
  it("Olculmus_sifir_gizlenmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge quality={quality({ total: 4, decided: 4, correct: 0, accuracy: 0 })} error={null} />,
    );

    expect(html).toContain("%0.0");
    expect(html).toContain('data-field="accuracy" data-kind="ratio"');
    expect(html).not.toContain("henüz karar verilmedi");
  });

  /**
   * <b>Boş kümede sayı görünüyor, gösterge gizlenmiyor.</b> Sıfırı saklamak,
   * "henüz kimse inceleme yapmadı" ile "gösterge bozuk"u aynı boşluğa
   * düşürürdü — ve inceleme yorgunluğu riskinin görünmesi gereken yer tam
   * burası.
   */
  it("Bos_kumede_sifir_goruluyor", () => {
    const html = renderToStaticMarkup(<QualityBadge quality={quality()} error={null} />);

    expect(html).toContain('data-quality="ready"');
    expect(html).toContain('data-field="total">0<');
    expect(html).toContain('data-field="decided">0<');
  });

  /**
   * Gösterge okunamazsa <b>yine duruyor</b> ve okunamadığını söylüyor.
   * Sessizce kaybolan bir gösterge, sıfır gösterenden kötü: yokluğu bir
   * bilgiymiş gibi okunur.
   */
  it("Okunamayan_gosterge_kaybolmuyor", () => {
    const html = renderToStaticMarkup(<QualityBadge quality={null} error="Yetki reddedildi." />);

    expect(html).toContain('data-quality="unavailable"');
    expect(html).toContain("Yetki reddedildi.");
    expect(html).toContain("Altın küme");
  });

  it("Oranlar_yuzde_olarak_bicimleniyor", () => {
    const display = presentQuality(quality({ total: 10, decided: 8, correct: 6, accuracy: 0.75 }));

    expect(display.accuracy).toEqual({ kind: "ratio", percent: "%75.0" });
  });

  /**
   * <c>unknown_ratio</c> de aynı ayrımı taşıyor: hiç inceleme yokken oran
   * <i>yok</i>, sıfır değil.
   */
  it("Bilmiyorum_orani_da_null_ile_sifiri_ayiriyor", () => {
    expect(presentQuality(quality()).unknownRatio.kind).toBe("undecided");
    expect(presentQuality(quality({ total: 3, unknown: 0, unknown_ratio: 0 })).unknownRatio).toEqual({
      kind: "ratio",
      percent: "%0.0",
    });
  });

  /**
   * Şema <c>int64</c>'ü <c>number | string</c> tipliyor: büyük sayılar JSON'da
   * dizgi inebiliyor ve gösterge onları <c>NaN</c> göstermemeli.
   */
  it("Dizgi_gelen_sayilar_okunuyor", () => {
    const display = presentQuality(quality({ total: "1204", decided: "900", accuracy: "0.5" }));

    expect(display.total).toBe(1204);
    expect(display.decided).toBe(900);
    expect(display.accuracy).toEqual({ kind: "ratio", percent: "%50.0" });
  });
});

describe("çelişen kanıt tiyatrosu (T47)", () => {
  /**
   * <b>Asıl bekçi ve bu ölçüde <i>accuracy</i>'dekinden daha keskin.</b>
   *
   * <p>
   * Burada <b>iyi olan uç sıfır</b>: "%0 tiyatro" en iyi sonuç. Ölçülmemiş bir
   * boyut <c>%0</c> yazarsa ekran onu <b>mükemmel</b> diye gösterir — yani
   * göstergenin var olma sebebinin tam tersi.
   * </p>
   */
  it("Degerlendirilmemisse_yuzde_sifir_yazmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({ total: 6, contradicting_evaluated: 0, contradicting_trivial_ratio: null })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="contradicting_trivial_ratio" data-kind="undecided"');
    expect(html).toContain("çelişen kanıt değerlendirilmedi");
    expect(html).not.toContain("%0.0");
  });

  /** Ters yön: gerçek bir sıfır <b>ölçülmüş</b> sonuç ve gizlenmiyor. */
  it("Olculmus_sifir_tiyatro_gizlenmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({ total: 4, contradicting_evaluated: 4, contradicting_trivial_ratio: 0 })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="contradicting_trivial_ratio" data-kind="ratio"');
    expect(html).toContain("%0.0");
  });

  /**
   * Ekranda görünen payda <b>değerlendirilmiş</b> sayısı, toplam inceleme
   * değil. Okuyan kişi oranın neye bölündüğünü görmeden yorumlayamaz.
   */
  it("Paydasi_ekranda_gorunuyor", () => {
    const display = presentQuality(
      quality({
        total: 10,
        contradicting_sound: 1,
        contradicting_trivial: 3,
        contradicting_evaluated: 4,
        contradicting_trivial_ratio: 0.75,
      }),
    );

    expect(display.contradictingEvaluated).toBe(4);
    expect(display.contradictingTrivialRatio).toEqual({ kind: "ratio", percent: "%75.0" });
  });
});

describe("accuracy@k (T47)", () => {
  /**
   * <b>Soru sorulmamışsa <c>%0</c> yazmıyor.</b>
   *
   * <p>
   * Bu, göstergedeki en inandırıcı yanlış olurdu: kimse sıra sorusuna cevap
   * vermemişken ekranda <i>"%0 ilk bulguda doğru"</i> yazması, ürünün hiç
   * ölçülmemiş doğruluğunu <b>ölçülmüş ve berbat</b> gibi gösterir.
   * </p>
   */
  it("Sorulmamissa_yuzde_sifir_yazmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge quality={quality({ total: 5, rank_asked: 0 })} error={null} />,
    );

    expect(html).toContain('data-field="accuracy_at_one" data-kind="undecided"');
    expect(html).toContain('data-field="accuracy_at_three" data-kind="undecided"');
    expect(html).toContain("bulgu sırası sorulmadı");
  });

  /** Ölçülmüş sıfır gizlenmiyor — hiçbir bulgu doğru çıkmamış olabilir. */
  it("Olculmus_sifir_accuracy_gizlenmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({ total: 4, rank_asked: 4, accuracy_at_one: 0, accuracy_at_three: 0 })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="accuracy_at_one" data-kind="ratio"');
    expect(html).toContain("%0.0");
  });

  /** İki oran aynı alandan çıkıyor ve paydaları ortak. */
  it("Iki_oran_ayni_paydayi_paylasiyor", () => {
    const display = presentQuality(
      quality({ total: 8, rank_asked: 4, accuracy_at_one: 0.25, accuracy_at_three: 0.5 }),
    );

    expect(display.rankAsked).toBe(4);
    expect(display.accuracyAtOne).toEqual({ kind: "ratio", percent: "%25.0" });
    expect(display.accuracyAtThree).toEqual({ kind: "ratio", percent: "%50.0" });
  });
});

describe("atılan cümle oranı — üç hâl (T47)", () => {
  /**
   * <b>Model hiç koşmamışsa <c>%0</c> yazmıyor.</b>
   *
   * <p>
   * <c>%0 atılan cümle</c> <b>mükemmel kalite</b> demek. Ölçülemeyen bir oranın
   * en iyi sonuçla aynı baytları üretmesi, bu göstergenin engellemek için var
   * olduğu şey.
   * </p>
   */
  it("Model_kosmamissa_yuzde_sifir_yazmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({ reviewed_bundles: 3, reasoning_absent: 3, dropped_sentence_ratio: null })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="dropped_sentence_ratio" data-kind="undecided"');
    expect(html).toContain("model hiç koşmadı");
    expect(html).not.toContain("%0.0");
  });

  /** Ölçülmüş sıfır gizlenmiyor — hiçbir cümle atılmamış olabilir. */
  it("Olculmus_sifir_atilan_cumle_gizlenmiyor", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({
          reviewed_bundles: 2,
          reports_measured: 2,
          produced_sentences: 20,
          dropped_sentence_ratio: 0,
        })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="dropped_sentence_ratio" data-kind="ratio"');
    expect(html).toContain("%0.0");
  });

  /**
   * <b>B ile C ekranda ayrı sayılar.</b> İkisi de boş bulgu listesi veriyor ve
   * ikisi de oranı hareket ettirmiyor; ayrı gösterilmezlerse en pahalı hâl
   * (hepsi atıldı) görünmez olur.
   */
  it("Uretmeyen_ile_hepsi_atilan_ekranda_ayri", () => {
    const html = renderToStaticMarkup(
      <QualityBadge
        quality={quality({ reports_measured: 2, produced_nothing: 1, all_dropped: 1 })}
        error={null}
      />,
    );

    expect(html).toContain('data-field="produced_nothing"');
    expect(html).toContain('data-field="all_dropped"');
  });

  /** Kapsam oranın yanında: ölçülen rapor / incelenmiş paket. */
  it("Olcum_kapsami_oranin_yaninda", () => {
    const display = presentQuality(
      quality({
        reviewed_bundles: 4,
        reports_measured: 1,
        reasoning_absent: 3,
        produced_sentences: 10,
        dropped_sentences: 3,
        fabricated_sentences: 1,
        dropped_sentence_ratio: 0.3,
        fabricated_citation_ratio: 1 / 3,
        measured_coverage: 0.25,
      }),
    );

    expect(display.reportsMeasured).toBe(1);
    expect(display.reviewedBundles).toBe(4);
    expect(display.droppedSentenceRatio).toEqual({ kind: "ratio", percent: "%30.0" });
    expect(display.measuredCoverage).toEqual({ kind: "ratio", percent: "%25.0" });
  });
});

/**
 * <b>Uç patladığında gösterge ne yapıyor.</b>
 *
 * <p>
 * `GET /v1/rca/quality` canlı Postgres'e karşı hiç koşmadı; ekran tarafının
 * sınayabileceği şey de zaten uç değil, <b>ucun düşmesine verilen tepki</b>.
 * Sessizce kaybolan bir gösterge, sıfır gösterenden kötü: yokluğu bir bilgi
 * gibi okunur ve "henüz kimse inceleme yapmadı" ile "gösterge bozuk" aynı
 * boşluğa düşer.
 * </p>
 */
describe("gösterge hata yolu", () => {
  it("Sunucu_hatasinda_gosterge_duruyor_ve_sebebi_yaziyor", () => {
    // Sayfa `describeError` ile mesajı çıkarıp bileşene veriyor; burada aynı
    // yol koşuluyor ki ekranın gördüğü metin sınansın.
    const message = describeError(
      new ApiError(500, { error: "Kalite göstergesi hesaplanamadı." }),
    );
    const html = renderToStaticMarkup(<QualityBadge quality={null} error={message} />);

    expect(html).toContain('data-quality="unavailable"');
    expect(html).toContain("Altın küme");
    expect(html).toContain("Kalite göstergesi hesaplanamadı.");

    // En önemlisi: hata yolunda uydurulmuş bir sayı YOK — ne sayaç ne oran.
    expect(html).not.toContain('data-field="total"');
    expect(html).not.toContain('data-field="accuracy"');
    expect(html).not.toContain("henüz karar verilmedi");
  });

  /**
   * Hata metni gelmezse bile gösterge <b>bir şey söylüyor</b> — boş bir kutu,
   * "sorun yok" diye okunur.
   */
  it("Sebep_bilinmese_de_gosterge_sessiz_kalmiyor", () => {
    const html = renderToStaticMarkup(<QualityBadge quality={null} error={null} />);

    expect(html).toContain('data-quality="unavailable"');
    expect(html).toContain("Gösterge okunamadı.");
  });
});
