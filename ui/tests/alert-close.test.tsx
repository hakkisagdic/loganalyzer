import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { CloseTriggerPanel } from "@/app/alarmlar/CloseTriggerPanel";
import { TriggerHistory } from "@/app/alarmlar/TriggerHistory";
import {
  CLOSE_COST_HINT,
  canClose,
  closeOutcomeText,
  closeRequest,
  isOpen,
} from "@/lib/alerts/closure";
import { reviewRequest } from "@/lib/rca/report";
import type { AlertTrigger } from "@/lib/alerts/types";

/**
 * Alarm kapatma ekranı (T38'in kalan yarısı).
 *
 * <p>
 * Buradaki iddiaların ortak noktası: <b>zorunluluk sunucuda yapısal, ekranın
 * işi onu görünür kılmak.</b> Ekran kuralı tekrarlamıyor — tekrarlasaydı iki
 * kopya zamanla ayrışırdı — ama kullanıcıya <i>neden</i> öyle olduğunu
 * söylüyor.
 * </p>
 */

function trigger(overrides: Partial<AlertTrigger> = {}): AlertTrigger {
  return {
    id: "11111111-1111-4111-8111-111111111111",
    rule_id: "22222222-2222-4222-8222-222222222222",
    rule_name: "Eşik kuralı",
    fired_at: "2026-08-21T10:00:00Z",
    window_from: "2026-08-21T09:00:00Z",
    window_to: "2026-08-21T10:00:00Z",
    value: 120,
    threshold: 80,
    source_id: "fg-core",
    owner_group: "net-core",
    summary: "eşik aşıldı",
    deliveries: [],
    state: "open",
    closed_at: null,
    closed_by: null,
    review_id: null,
    ...overrides,
  } as AlertTrigger;
}

describe("kapatma gövdesi", () => {
  /**
   * <b>İnceleme ile kapatma aynı gövdeyi kuruyor.</b>
   *
   * <p>
   * İki uç aynı dört alanı alıyor ve ikinci bir kurucu yazmak §9'un yasakladığı
   * kopya olurdu: bir alan bir tarafta eklenip diğerinde unutulursa altın
   * kümenin yarısı eksik bir boyutla dolar ve ayrışma ancak F4'ün
   * karşılaştırmasında görünürdü.
   * </p>
   */
  it("RCA incelemesiyle birebir aynı", () => {
    expect(closeRequest("wrong", "trivial", "  BGP flap  ", "not")).toEqual(
      reviewRequest("wrong", "trivial", "  BGP flap  ", "not"),
    );
  });

  it("gerçek kök nedeni kırpıyor ama boşsa boş bırakıyor", () => {
    expect(closeRequest("wrong", "unknown", "  BGP flap  ").actual_root_cause).toBe("BGP flap");

    // Boşluğu bilgi taşıyor: "yanlış ama doğrusunu bilmiyorum" ile "bu alanı
    // hiç doldurmadım" ayrı şeyler ve yer tutucu ikisini birleştirirdi.
    expect(closeRequest("wrong", "unknown", "   ").actual_root_cause).toBe("");
  });

  /**
   * <c>reviewer</c> gövdede <b>yok</b>: sunucu onu token'dan alıyor.
   * İstemciden göndermek, herkesin başkasının adına oy yazabilmesi demek.
   */
  it("inceleyeni istemciden göndermiyor", () => {
    expect(Object.keys(closeRequest("correct", "sound", "", ""))).toEqual([
      "verdict",
      "contradicting_evidence",
      "actual_root_cause",
      "note",
    ]);
  });
});

describe("kapatma önkoşulu", () => {
  it("kararsız kapatma yok", () => {
    expect(canClose(null)).toBe(false);
    expect(canClose("")).toBe(false);
  });

  /**
   * <b>"Bilmiyorum" geçerli bir karar.</b>
   *
   * <p>
   * Zorunlu bir soruda kaçış yoksa insanlar rastgele seçip geçer ve altın küme
   * <i>ölçülüyormuş gibi görünen</i> gürültüyle dolar — ölçülememekten kötü.
   * Ekranda engellenseydi sunucunun kabul ettiği bir karar ekranda
   * yazılamazdı; iki taraf ayrışırdı.
   * </p>
   */
  it("bilmiyorum ile kapatılabiliyor", () => {
    expect(canClose("unknown")).toBe(true);
  });
});

describe("kapatma paneli", () => {
  /**
   * <b>Dört düğme, dördü de kapatıyor.</b>
   *
   * <p>
   * Ayrı bir "kapat" düğmesi olsaydı kullanıcı önce ona basar, 400 alır ve
   * zorunluluğu bir hata mesajından öğrenirdi. Burada karar düğmesi kapatma
   * düğmesi: "Doğru" demek hem incelemeyi yazmak hem alarmı kapatmak.
   * </p>
   */
  it("incelemesiz kapatma düğmesi yok", () => {
    const html = renderToStaticMarkup(<CloseTriggerPanel trigger={trigger()} />);

    for (const verdict of ["correct", "incomplete", "wrong", "unknown"]) {
      expect(html).toContain(`data-testid="close-${verdict}"`);
    }

    // "Kapat" diye kararsız bir düğme YOK — olsaydı zorunluluk ekranda
    // atlanabilir görünürdü.
    expect(html).not.toContain('data-testid="close-only"');
  });

  /**
   * "Bilmiyorum" diğerleriyle <b>aynı</b> görünüyor: küçük bir bağlantı ya da
   * ikincil bir düğme değil. Görsel olarak küçültmek onu bir kaçış gibi
   * gösterirdi, oysa ölçümün kendisi.
   */
  it("bilmiyorum eşit bir seçenek olarak duruyor", () => {
    const html = renderToStaticMarkup(<CloseTriggerPanel trigger={trigger()} />);

    expect(html).toContain("Bilmiyorum");
    expect(html).toContain('data-testid="close-unknown"');
  });

  /**
   * <b>Kapatma ucuz değil ve bu tıklamadan ÖNCE yazılı.</b>
   *
   * <p>
   * Söylenmezse yavaş bir düğme "takıldı" diye okunur, kullanıcı ikinci kez
   * tıklar, ve ikinci tık "zaten kapatılmış" hatası alır — sebebi olmayan bir
   * arıza gibi görünen bir hata.
   * </p>
   */
  it("paket üretiminin maliyetini önceden söylüyor", () => {
    const html = renderToStaticMarkup(<CloseTriggerPanel trigger={trigger()} />);

    expect(html).toContain(CLOSE_COST_HINT);
    expect(CLOSE_COST_HINT).toContain("kanıt paketi");
  });

  /** Çelişen kanıt her kapatmada soruluyor, karara bağlı değil. */
  it("çelişen kanıt kararını her zaman soruyor", () => {
    const html = renderToStaticMarkup(<CloseTriggerPanel trigger={trigger()} />);

    expect(html).toContain('data-testid="close-contradicting"');
    expect(html).toContain("Vardı, önemsizdi");
  });
});

describe("kapatma sonucu", () => {
  /**
   * Paketin bu kapatmada üretilip üretilmediği <b>söyleniyor</b> — kullanıcı
   * neden beklediğini sonradan da görebilmeli.
   */
  it("üretim olduysa bunu ayrıca söylüyor", () => {
    expect(closeOutcomeText(true)).toContain("üretildi");
    expect(closeOutcomeText(false)).toContain("zaten vardı");
  });
});

describe("tetiklenme durumu", () => {
  it("açık tetiklenme kapatılabiliyor", () => {
    expect(isOpen(trigger())).toBe(true);
  });

  it("kapalı tetiklenme kapatılamıyor", () => {
    expect(isOpen(trigger({ state: "closed" }))).toBe(false);
  });

  /**
   * <b>Kapalı satırda kapatma düğmesi hiç görünmüyor.</b>
   *
   * <p>
   * Sunucu ikinci kapatmayı reddediyor (aynı olgu altın kümeye iki kez
   * yazılırsa doğruluk oranı iki kez ağırlık verir). Reddedilecek bir düğmeyi
   * göstermek, kullanıcıya var olmayan bir seçenek sunmak olurdu.
   * </p>
   */
  it("kapalı satır kapatma paneli göstermiyor", () => {
    const closed = trigger({
      state: "closed",
      closed_at: "2026-08-21T11:00:00Z",
      closed_by: "analyst.core",
      review_id: "33333333-3333-4333-8333-333333333333",
    });

    const html = renderToStaticMarkup(<TriggerHistory triggers={[closed]} />);

    expect(html).not.toContain('data-testid="close-panel"');
    expect(html).toContain("kapatıldı");

    // Kim kapattı da yazılı: cevabı olmayan bir kapatma kayıt değil, yalnızca
    // bir durum değişikliği olurdu.
    expect(html).toContain("analyst.core");
  });

  it("açık satır kapatma paneli gösteriyor", () => {
    const html = renderToStaticMarkup(<TriggerHistory triggers={[trigger()]} />);

    expect(html).toContain('data-testid="close-panel"');
  });
});
