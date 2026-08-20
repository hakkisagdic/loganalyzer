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
