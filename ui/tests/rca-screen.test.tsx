import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { ReportView } from "@/app/rca/[id]/ReportView";
import { drilldownLosesFilters, toEventsHref } from "@/lib/rca/drilldown";
import {
  CONTRADICTING_CHOICES,
  honestyLines,
  presentStatus,
  REVIEW_STATES,
  reviewRequest,
  STATUS_PRESENTATION,
  type RcaReport,
  type RcaSlice,
} from "@/lib/rca/report";

/**
 * RCA rapor ekranı (T37).
 *
 * <p>
 * <b>Bu dosyanın taşıyıcı testi <c>Dort_durum_ekranda_ayirt_edilebiliyor</c>.</b>
 * Sunucu tarafındaki ikizi <c>RcaFourStatesTests</c>; ikisi <b>ayrı</b> sorular
 * ve birinde geçip diğerinde kaybolması beklenen kaza. Telde dört ayrı dizgi
 * gelmesi, ekranın onları dört ayrı şey olarak <b>çizdiğini</b> göstermiyor.
 * </p>
 *
 * <p>
 * Kaybedilirse olan şey sessiz: <c>never_fed</c> ("değişiklik akışı hiç
 * beslenmemiş") ekranda "değişiklik olmadı" diye görünür ve kullanıcı bir
 * sinyalin <b>yokluğunu</b> bulgu sanar.
 * </p>
 */

const WINDOW = {
  from: "2026-08-20T13:00:00Z",
  to: "2026-08-20T14:00:00Z",
  baseline_from: "2026-08-13T13:00:00Z",
  baseline_to: "2026-08-20T13:00:00Z",
  owner_groups: ["network/core"],
  source_ids: [],
};

function slice(provider_id: string, status: string, detail: string): RcaSlice {
  return { provider_id, kind: "log", status, detail, item_count: 0, truncated: false };
}

function report(overrides: Partial<RcaReport> = {}): RcaReport {
  return {
    bundle_id: "01920000-0000-7000-8000-000000000001",
    content_hash: "abcdef0123456789",
    gathered_at: "2026-08-20T14:00:05Z",
    window: WINDOW,
    findings: [],
    timeline: [],
    silent: [slice("logs.first-seen", "empty", "Tabanda görülmeyen imza yok.")],
    not_consulted: [
      slice("change.feed", "never_fed", "Değişiklik akışı hiç beslenmemiş."),
      slice("logs.silence", "unavailable", "Kaynak etkinlik yüzeyi kapalı."),
      slice("logs.volume", "failed", "Sorgu zaman aşımına uğradı."),
      slice("metric.baseline", "not_registered", "Bu tür için sağlayıcı yok — F5."),
    ],
    trust: { measured: true, total_events: 1204, unreliable_time_events: 0, unreliable_ratio: 0 },
    out_of_scope_count: 0,
    is_partial: false,
    review: null,
    ...overrides,
  } as RcaReport;
}

describe("dört durum ekranda ayrı kalıyor", () => {
  /**
   * <b>Çivili değişmez.</b> Dört durumun rozet metni dört <b>ayrı</b> dizgi ve
   * hepsi çizilen HTML'de var.
   */
  it("Dort_durum_ekranda_ayirt_edilebiliyor", () => {
    const html = renderToStaticMarkup(<ReportView report={report()} />);

    const labels = ["empty", "never_fed", "unavailable", "failed", "not_registered"].map(
      (status) => presentStatus(status).label,
    );

    expect(new Set(labels).size).toBe(labels.length);

    for (const label of labels) {
      expect(html).toContain(label);
    }

    // Durum, DOM'da da makine-okunur duruyor: rozet metni değişse bile ayrım
    // kaybolmuyor.
    for (const status of ["never_fed", "unavailable", "failed", "not_registered"]) {
      expect(html).toContain(`data-status="${status}"`);
    }
  });

  /**
   * <c>Empty</c> "bakılmayanlar" bölümüne düşmüyor — yukarıdakinin tersi ve
   * ayrı yazılması şart. Yalnızca "dört etiket var" demek, <c>Empty</c>'nin
   * aralarına karışmadığını göstermiyor.
   */
  it("Bakildi_ama_bos_bakilmayanlardan_ayri_bolumde", () => {
    const html = renderToStaticMarkup(<ReportView report={report()} />);

    const silentSection = section(html, "bakildi-bos");
    const notConsulted = section(html, "bakilmayanlar");

    expect(silentSection).toContain("logs.first-seen");
    expect(notConsulted).not.toContain("logs.first-seen");

    expect(notConsulted).toContain("change.feed");
    expect(silentSection).not.toContain("change.feed");
  });

  /**
   * <c>never_fed</c> ekranda "değişiklik olmadı" demiyor — cümle bilerek uzun
   * ve bilerek olumsuz.
   */
  it("Besleme_yok_degisiklik_olmadi_demiyor", () => {
    expect(STATUS_PRESENTATION.never_fed?.meaning).toContain("DEĞİL");
    expect(STATUS_PRESENTATION.never_fed?.label).not.toBe(STATUS_PRESENTATION.empty?.label);
  });

  /**
   * Tanınmayan bir durum <b>gizlenmiyor</b>. Sunucu yeni bir değer eklerse ve
   * ekran onu "veri yok"a düşürürse, bu dosyanın engellemeye çalıştığı şeyin
   * ta kendisi olurdu.
   */
  it("Taninmayan_durum_sessizce_gizlenmiyor", () => {
    const unknown = presentStatus("brand_new_status");

    expect(unknown.label).toBe("brand_new_status");
    expect(unknown.meaning).toContain("tanımıyor");
  });

  /**
   * <b>Bakılmayanlar boşsa bölüm kaybolmuyor.</b> Boş liste "her şeye bakıldı"
   * demek ve bu gösterilmeye değer bir bilgi.
   */
  it("Bakilmayanlar_bos_olsa_da_bolum_duruyor", () => {
    const html = renderToStaticMarkup(<ReportView report={report({ not_consulted: [] })} />);

    expect(html).toContain("Bakılmayanlar");
    expect(html).toContain("Her kanıt türüne bakıldı.");
  });
});

describe("zaman güvenilirliği", () => {
  /** "Ölçemedik" ile "sıfır" farklı — ikincisi "sorun yok" diye okunuyor. */
  it("Olculemeyen_zaman_sifirdan_ayri_yaziliyor", () => {
    const unmeasured = honestyLines(
      report({
        trust: { measured: false, total_events: 0, unreliable_time_events: 0, unreliable_ratio: null },
      }),
    );

    expect(unmeasured.map((line) => line.id)).toContain("trust_unmeasured");
    expect(unmeasured[0]?.text).toContain("bilinmiyor");
  });

  /** Her raporda duran bir uyarı hiçbir şey söylemez. */
  it("Guvenilir_zamanda_uyari_yok", () => {
    expect(honestyLines(report())).toHaveLength(0);
  });

  it("Guvenilmez_zaman_sayi_ve_oranla_soyleniyor", () => {
    const lines = honestyLines(
      report({
        trust: { measured: true, total_events: 1000, unreliable_time_events: 142, unreliable_ratio: 0.142 },
      }),
    );

    expect(lines[0]?.text).toContain("142 / 1000");
    expect(lines[0]?.text).toContain("%14.2");
  });

  /**
   * Kapsam dışı satırı <b>yalnızca sayı</b> veriyor — grup adı da bir sızıntı
   * (K17, RCA §3.2).
   */
  it("Kapsam_disi_satiri_sayi_veriyor_grup_adi_vermiyor", () => {
    const lines = honestyLines(report({ out_of_scope_count: 342 }));
    const text = lines.find((line) => line.id === "out_of_scope")?.text ?? "";

    expect(text).toContain("342");
    expect(text).not.toContain("network/core");
  });
});

describe("drilldown bağlantısı", () => {
  const base = {
    from: "2026-08-20T13:00:00Z",
    to: "2026-08-20T14:00:00Z",
    owner_groups: ["network/core"],
    source_ids: ["edge-rtr-07"],
    full_text: null,
    filters: [],
  };

  it("Zaman_araligi_ve_kaynak_baglantiya_giriyor", () => {
    const href = toEventsHref(base);

    expect(href).toContain("from=2026-08-20T13%3A00%3A00Z");
    expect(href).toContain("source_id=edge-rtr-07");
    expect(href).toContain("owner_group=network%2Fcore");
  });

  /**
   * <b>Temsil edilemeyen filtre sessizce düşmüyor.</b>
   *
   * <p>
   * Düşseydi kullanıcı, kanıt satırının gösterdiğinden <b>daha geniş</b> bir
   * kümeye bakar ve baktığı kümenin o satırın kümesi olduğunu sanardı. Alarm
   * bağlantısında aynı sorun <c>eksik</c> parametresiyle çözülmüştü; burada
   * ikinci bir kopya değil aynı mekanizma kullanılıyor.
   * </p>
   */
  it("Temsil_edilemeyen_filtre_eksik_olarak_bildiriliyor", () => {
    const href = toEventsHref({
      ...base,
      filters: [{ field: "signature_hash", operator: "equals", values: ["14733834131172344067"] }],
    });

    expect(href).toContain("eksik=signature_hash");
    expect(drilldownLosesFilters({ ...base, filters: [{ field: "signature_hash", operator: "equals", values: ["1"] }] })).toBe(true);
  });

  /**
   * <b>Olumsuzlama eşitliğe çevrilmiyor.</b> Çevirmek, kullanıcıya dolu ve
   * inandırıcı ama <b>yanlış</b> bir küme göstermek olurdu — sessizce
   * düşürmekten beter.
   */
  it("Desteklenmeyen_operator_esitlige_cevrilmiyor", () => {
    const href = toEventsHref({
      ...base,
      filters: [{ field: "vendor", operator: "notequals", values: ["cisco"] }],
    });

    expect(href).not.toContain("vendor=cisco");
    expect(href).toContain("eksik=vendor");
  });

  it("Desteklenen_esitlik_filtresi_kutuya_giriyor", () => {
    const href = toEventsHref({
      ...base,
      filters: [{ field: "vendor", operator: "equals", values: ["cisco"] }],
    });

    expect(href).toContain("vendor=cisco");
    expect(href).not.toContain("eksik=");
  });

  /** Çoklu değer tek kutuya sığmıyor; birini seçmek diğerlerini atmak olurdu. */
  it("Coklu_deger_eksik_olarak_bildiriliyor", () => {
    const href = toEventsHref({
      ...base,
      filters: [{ field: "vendor", operator: "equals", values: ["cisco", "fortinet"] }],
    });

    expect(href).toContain("eksik=vendor");
  });

  /**
   * <c>drilldown</c> null olan satır <b>boş arama açmıyor</b> — kullanıcıyı
   * ilgisiz bir sonuca göndermek, bağlantı vermemekten kötü.
   */
  it("Drilldown_yoksa_baglanti_cizilmiyor", () => {
    const html = renderToStaticMarkup(
      <ReportView
        report={report({
          findings: [
            {
              id: "ev-1",
              provider_id: "change.feed",
              kind: "change",
              timestamp: "2026-08-20T13:50:11Z",
              summary: "ACL push · core-sw-02",
              payload: {},
              drilldown: null,
            },
          ],
        })}
      />,
    );

    expect(html).toContain("inilecek ham log yok");
    expect(html).not.toContain("/olaylar?");
  });
});

/** `data-section` işaretli bölümün HTML'i. */
function section(html: string, id: string): string {
  const start = html.indexOf(`data-section="${id}"`);
  expect(start).toBeGreaterThanOrEqual(0);

  const next = html.indexOf("data-section=", start + 1);
  return next < 0 ? html.slice(start) : html.slice(start, next);
}

/**
 * İnceleme — <b>dört karar</b> ve <b>çelişen kanıt</b> boyutu.
 *
 * <p>
 * Bu blok bir kusurdan doğdu: dördüncü düğme (`unknown`) ekrana eklendi ve 299
 * UI testinin hiçbiri onu sınamıyordu. Gönderilebildiğini hiçbir şey tutmuyorsa,
 * bir gün gönderilemez hâle gelmesi de hiçbir yerde görünmez.
 * </p>
 */
describe("inceleme kararları", () => {
  /**
   * <b>Dört karar değerinin hepsi gönderilebiliyor</b> — özellikle `unknown`.
   *
   * <p>
   * `unknown` bir kaçış kapısı değil bir ölçüm: seçenek olmasaydı gerçekten
   * bilmeyen kişi rastgele birini seçer ve altın küme <b>sessizce gürültüyle</b>
   * dolardı — ölçülemez olmaktan kötü, çünkü ölçülüyormuş gibi görünürdü.
   * </p>
   */
  it("Dort_karar_degeri_de_gonderilebiliyor", () => {
    const verdicts = REVIEW_STATES.map((state) => state.value);

    expect(verdicts).toEqual(["correct", "incomplete", "wrong", "unknown"]);

    for (const verdict of verdicts) {
      expect(reviewRequest(verdict, "unknown", "").verdict).toBe(verdict);
    }
  });

  /** Dördüncü düğme gerçekten çiziliyor — liste doğru olsa da ekranda yoksa basılamaz. */
  it("Bilmiyorum_dugmesi_ekranda", () => {
    const html = renderToStaticMarkup(<ReportView report={report()} />);

    for (const state of REVIEW_STATES) {
      expect(html).toContain(state.label);
    }
  });

  /**
   * <b>Çelişen kanıt karara bağlı değil.</b> Seçim hangi karar düğmesine
   * basılırsa basılsın aynı gövdeyle gidiyor.
   *
   * <p>
   * Karara bağlansaydı ölçüm, tiyatronun en tehlikeli hâlini — raporun bütün
   * olarak <b>doğru</b> olduğu hâli — sistematik olarak hiç örneklemezdi.
   * </p>
   */
  it("Celisen_kanit_karara_bagli_degil", () => {
    for (const verdict of REVIEW_STATES.map((s) => s.value)) {
      expect(reviewRequest(verdict, "trivial", "").contradicting_evidence).toBe("trivial");
    }
  });

  /** Dört çelişen-kanıt değeri de gönderilebiliyor. */
  it("Dort_celisen_kanit_degeri_de_gonderilebiliyor", () => {
    const values = CONTRADICTING_CHOICES.map((choice) => choice.value);

    expect(new Set(values)).toEqual(new Set(["unknown", "not_present", "sound", "trivial"]));

    for (const value of values) {
      expect(reviewRequest("correct", value, "").contradicting_evidence).toBe(value);
    }
  });

  /**
   * <b>Varsayılan `unknown`, `not_present` değil.</b> Ekran bu boyutu bilemiyor
   * — yanıt böyle bir alan taşımıyor — ve kullanıcı adına çıkarım yapmak, F4
   * bölümü eklediğinde sessizce yanlış iddia etmeye devam etmek olurdu.
   */
  it("Celisen_kanit_varsayilani_unknown", () => {
    expect(CONTRADICTING_CHOICES[0]?.value).toBe("unknown");

    const html = renderToStaticMarkup(<ReportView report={report()} />);
    expect(html).toContain('data-testid="contradicting-evidence"');
  });

  /** Kök neden kırpılıyor; yalnızca boşluk yazmak "biliyorum" saymamalı. */
  it("Kok_neden_kirpiliyor", () => {
    expect(reviewRequest("wrong", "unknown", "   ").actual_root_cause).toBe("");
    expect(reviewRequest("wrong", "unknown", "  ACL push  ").actual_root_cause).toBe("ACL push");
  });

  /** İnceleyen gövdede yok — sunucu onu token'dan alıyor. */
  it("Inceleyen_govdede_gonderilmiyor", () => {
    expect(Object.keys(reviewRequest("correct", "unknown", "")).sort()).toEqual([
      "actual_root_cause",
      "contradicting_evidence",
      "note",
      "verdict",
    ]);
  });
});

/**
 * <b>Dürüstlük satırları — ekran ile export aynı şeyi söylüyor mu.</b>
 *
 * <p>
 * Satırları iki ayrı uygulama üretiyor: ekranda <c>honestyLines</c>, export'ta
 * sunucunun <c>AppendHonesty</c>'si. Aynı raporu anlatan iki metin ve aralarında
 * derleyicinin kovaladığı bir bağ <b>yok</b> — tel adlarındaki durumun aynısı.
 * </p>
 *
 * <p>
 * Ayrışmanın bedeli sessiz ve tek yönlü kötü: ekranı okuyan kısıtı görür,
 * PDF'i okuyan görmez. Olay sonrası paylaşılan şey rapor, ekran değil.
 * C# tarafındaki ikizi <c>RcaHonestyParityTests</c>.
 * </p>
 */
describe("dürüstlük satırları", () => {
  /**
   * <b>Dört uyarı cinsi ve sayısı sabit.</b> Sunucu beşincisini eklerse ve
   * ekran onu tanımazsa, export'ta görünen bir kısıt ekranda kaybolur — ve
   * eksik olanı kimse fark etmez. C# tarafı da aynı dördü çiviliyor.
   */
  it("Uyari_cinsleri_sabit", () => {
    const all = honestyLines(
      report({
        out_of_scope_count: 342,
        is_partial: true,
        trust: { measured: false, total_events: 0, unreliable_time_events: 0, unreliable_ratio: null },
      }),
    ).map((line) => line.id);

    expect(all).toEqual(["out_of_scope", "trust_unmeasured", "partial"]);

    // Dördüncüsü üçüncüyle **dışlayan**: ölçüldüyse ya güvenilir ya değil.
    const measured = honestyLines(
      report({
        trust: { measured: true, total_events: 10, unreliable_time_events: 2, unreliable_ratio: 0.2 },
      }),
    ).map((line) => line.id);

    expect(measured).toEqual(["trust_unreliable"]);
  });

  /**
   * <b>Sıfır kapsam dışı kayıtta satır hiç yazılmıyor.</b> Her raporda duran
   * bir uyarı hiçbir şey söylemez — ve bir gün gerçekten 342 olduğunda okuyan
   * kişi onu her zamanki gürültü sanar.
   */
  it("Kapsam_disi_sifirken_satir_yok", () => {
    const ids = honestyLines(report({ out_of_scope_count: 0 })).map((line) => line.id);

    expect(ids).not.toContain("out_of_scope");
  });

  /**
   * Kapsam dışı satırı sayıyı <b>olduğu gibi</b> taşıyor: ekranda yuvarlanan
   * bir sayı, export'takiyle ayrışmanın en sessiz yolu olurdu.
   */
  it("Kapsam_disi_sayisi_yuvarlanmiyor", () => {
    const text = honestyLines(report({ out_of_scope_count: 1_204_337 }))[0]?.text ?? "";

    expect(text).toContain("1204337");
  });
});
