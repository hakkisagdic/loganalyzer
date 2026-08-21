import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  ApiError,
  ForbiddenError,
  NotFoundError,
  RateLimitedError,
  SessionExpiredError,
  TransportError,
  describeError,
} from "@/lib/api/errors";
import type { SearchCriteria } from "@/lib/events/criteria";
import { errorKind, errorStatus } from "@/lib/telemetry/classify";
import { allowedProperties } from "@/lib/telemetry/events";
import { rangeHours, searchShape, yamlLines } from "@/lib/telemetry/measure";
import { scrubProperties } from "@/lib/telemetry/scrub";

/**
 * Enstrümantasyonun bekçileri — ekranlardan telemetriye giden yolun kendisi.
 *
 * <p>
 * `telemetry-scrub.test.ts` süzgecin doğru çalıştığını ölçüyor. Buradaki
 * testler bir adım öncesini ölçüyor: <b>ekranın elindeki zengin nesneden
 * türetilen şeklin</b> içinde zaten müşteri verisi olmadığını. Süzgece
 * güvenip zengin nesneyi ona vermek "beyaz liste tutar" demek olurdu — tutar,
 * ama tuttuğunu kimse okumaz ve bir gün liste genişler.
 * </p>
 */

/** Gerçekçi bir analist araması: içinde bir IP, bir kullanıcı adı, bir e-posta. */
const SIZINTI_ADAYI: SearchCriteria = {
  fullText: "src_ip=10.0.4.17 AND user=admin AND mail=admin@musteri.com",
  sourceId: "fw-core-01",
  ownerGroup: "golden",
  vendor: "cisco",
  parseStatuses: ["ok", "partial"],
  severityMin: 4,
  proto: "tcp",
  action: "deny",
  from: "2026-08-20T00:00:00Z",
  to: "2026-08-21T00:00:00Z",
  limit: 100,
  cursor: undefined,
  force: false,
};

/** Kanaryalar — çıktının hiçbir yerinde geçmemeli. */
const KANARYALAR = [
  "10.0.4.17",
  "admin",
  "admin@musteri.com",
  "src_ip",
  "fw-core-01",
  "golden",
  "cisco",
  "tcp",
  "deny",
];

describe("searchShape — aramanın şekli, içeriği değil", () => {
  const shape = searchShape(SIZINTI_ADAYI, { kind: "ready" }, { durationMs: 412.7, resultCount: 50 });

  it.each(KANARYALAR)('"%s" gönderilen paylodda GEÇMİYOR', (kanarya) => {
    expect(JSON.stringify(shape)).not.toContain(kanarya);
  });

  it("süzgeçten geçtikten sonra da geçmiyor", () => {
    // İki kapı: `searchShape` zaten türetiyor, `scrubProperties` beyaz liste
    // uyguluyor. İkinciyi de ölçüyoruz çünkü katalog genişlediğinde ilkinin
    // çıktısı yeni bir alan kazanabilir.
    const scrubbed = scrubProperties(
      shape as Record<string, unknown>,
      allowedProperties("event_search_run"),
    );

    for (const kanarya of KANARYALAR) {
      expect(JSON.stringify(scrubbed)).not.toContain(kanarya);
    }
  });

  it("tam metin sorgusunun UZUNLUĞU bile gitmiyor", () => {
    // Uzunluk tek başına zararsız görünüyor ama bir IP (15), bir UUID (36) ve
    // bir kelimeyi ayırt etmeye yetiyor.
    const uzunluk = SIZINTI_ADAYI.fullText.length;

    expect(JSON.stringify(shape)).not.toContain(String(uzunluk));
    expect(shape.has_full_text).toBe(true);
  });

  it("filtre SAYISI doğru — hangi filtreler olduğu değil", () => {
    // sourceId, ownerGroup, vendor, proto, action (5) + parseStatuses (2)
    // + severityMin (1) + fullText var (1) = 9
    expect(shape.criteria_count).toBe(9);
  });

  it("F1'in ölçtüğü sayfalama maliyeti alanı taşınıyor", () => {
    expect(shape.scoped).toBe(true);
    expect(searchShape({ ...SIZINTI_ADAYI, sourceId: "" }, { kind: "ready" }, { durationMs: 1 }).scoped).toBe(
      false,
    );
  });

  it("düşen aramada result_count HİÇ gitmiyor — sıfır olarak değil", () => {
    // Sıfır göndermek "arama çalıştı, sonuç yok" derdi; oysa arama düştü.
    // İkisini birleştiren bir pano yanlış olurdu.
    const dusen = searchShape(SIZINTI_ADAYI, { kind: "ready" }, { durationMs: 90 });

    expect(dusen.result_count).toBeUndefined();
    expect(
      "result_count" in
        scrubProperties(dusen as Record<string, unknown>, allowedProperties("event_search_run")),
    ).toBe(false);
  });
});

describe("rangeHours — genişlik gidiyor, sınırlar değil", () => {
  it("bir günlük aralık 24 saat", () => {
    expect(rangeHours("2026-08-20T00:00:00Z", "2026-08-21T00:00:00Z")).toBe(24);
  });

  it("mutlak zaman damgaları çıktıda yok", () => {
    const sonuc = String(rangeHours("2026-08-20T13:45:00Z", "2026-08-20T14:45:00Z"));

    expect(sonuc).toBe("1");
    expect(sonuc).not.toContain("2026");
    expect(sonuc).not.toContain("13:45");
  });

  const bosDonenler: [from: string, to: string, neden: string][] = [
    ["", "2026-08-21T00:00:00Z", "başlangıç yok"],
    ["2026-08-21T00:00:00Z", "", "bitiş yok"],
    ["olmayan-tarih", "2026-08-21T00:00:00Z", "ayrıştırılamıyor"],
    ["2026-08-21T00:00:00Z", "2026-08-20T00:00:00Z", "ters aralık"],
  ];

  it.each(bosDonenler)("(%s, %s) → undefined (%s)", (from, to) => {
    // Sıfır DEĞİL: sıfır dönmek "aralık yok" ile "aralık sıfır" arasını siler.
    expect(rangeHours(from, to)).toBeUndefined();
  });
});

describe("errorKind — kapalı sözlük", () => {
  const problem = {
    error: "golden grubu kapsamınızın dışında.",
    hint: "Kontrol düzlemindeki /network/core → golden eşlemesi eksik.",
  };

  const eslesmeler: [cause: unknown, beklenen: string][] = [
    [new SessionExpiredError(401, problem), "session_expired"],
    [new ForbiddenError(403, problem), "forbidden"],
    [new NotFoundError(404, problem), "not_found"],
    [new RateLimitedError(429, problem), "rate_limited"],
    [new ApiError(500, problem), "http_500"],
    [new TransportError("ECONNREFUSED 10.0.4.17:5080"), "transport"],
    [new Error("beklenmedik"), "unknown"],
    ["düz metin", "unknown"],
    [undefined, "unknown"],
  ];

  it.each(eslesmeler)("%o → %s", (cause, beklenen) => {
    expect(errorKind(cause)).toBe(beklenen);
  });

  /**
   * Bu testin ölçtüğü şey `classify.ts`'in varlık sebebi: `describeError`
   * sunucunun cümlesini döndürüyor ve döndürmesi DOĞRU — kullanıcı ekranda
   * hangi grubun kapsam dışında olduğunu görmeli. Telemetriye giden şey o
   * cümle olamaz.
   */
  it("sunucunun cümlesi telemetriye SIZMIYOR — ama ekranda duruyor", () => {
    const cause = new ForbiddenError(403, problem);

    const ekranda = describeError(cause);
    const telemetride = errorKind(cause);

    expect(ekranda).toContain("golden");
    expect(ekranda).toContain("/network/core");

    expect(telemetride).toBe("forbidden");
    expect(telemetride).not.toContain("golden");
    expect(telemetride).not.toContain("network");
  });

  it("taşıma hatasının mesajındaki adres gitmiyor", () => {
    const cause = new TransportError("ECONNREFUSED 10.0.4.17:5080");

    expect(describeError(cause)).toContain("10.0.4.17");
    expect(errorKind(cause)).not.toContain("10.0.4.17");
  });

  it("durum kodu ayrı taşınıyor ve yalnızca API hatalarında var", () => {
    expect(errorStatus(new ApiError(503, problem))).toBe(503);
    expect(errorStatus(new TransportError("x"))).toBeUndefined();
    expect(errorStatus(new Error("x"))).toBeUndefined();
  });

  it("error_shown paylodu süzgeçten sonra da temiz", () => {
    const cause = new ForbiddenError(403, problem);

    const scrubbed = scrubProperties(
      { route: "/olaylar", error_kind: errorKind(cause), status: errorStatus(cause) },
      allowedProperties("error_shown"),
    );

    expect(scrubbed).toEqual({ route: "/olaylar", error_kind: "forbidden", status: 403 });
  });
});

describe("error_kind kapalı sözlüğün DIŞINA çıkamıyor", () => {
  /**
   * <p>
   * <b>Asıl bekçi artık tip sistemi, bu dosya değil.</b> `EventFieldTypes`
   * her alanı tipliyor, dolayısıyla `error_kind: identity.message` — yani
   * sunucunun cümlesini doğrudan telemetriye koymak — <b>derlenmiyor</b>.
   * Ölçüldü: `Type 'string' is not assignable to type 'ErrorKind'`.
   * </p>
   *
   * <p>
   * <b>Buradaki tarayıcı küçüldü ve sebebi bir geri adım.</b> İlk hâli
   * kaynaktaki metin sabitlerini arıyordu ve inceleme haklı olarak şunu
   * gösterdi: gerçek çağrı yerleri (`error_kind: kind`,
   * `error_kind: errorKind(cause)`) birer <i>ifade</i>, sabit değil — yani
   * tarayıcı tam da tehlikeli hâli göremiyordu. Tarayıcıyı büyütmek (AST'ye
   * çıkmak) mümkündü ama yanlış cevaptı: tip sistemi o işi zaten daha iyi
   * yapıyor.
   * </p>
   *
   * <p>
   * Geriye tipin kapatamadığı <b>tek delik</b> kaldı: `as ErrorKind` zorlaması.
   * Tarayıcı artık yalnızca onu arıyor, ve doğru geçme hâli <b>sıfır bulmak</b>.
   * Önceki sürümdeki "hiç bulamazsan kırmızı yan" kapısı bu yüzden kaldırıldı —
   * o kapı, tarayıcının yük taşıdığı zamanın kuralıydı; artık taşımıyor.
   * </p>
   */
  function sozluktenMi(deger: string): boolean {
    const sabitler = [
      "identity",
      "session_expired",
      "forbidden",
      "not_found",
      "rate_limited",
      "transport",
      "unknown",
    ];

    // `http_${number}` da geçerli bir üye — `ErrorKind` bir şablon değişmezi
    // taşıyor ve `"http_404"` ondan geliyor.
    return sabitler.includes(deger) || /^http_\d+$/.test(deger);
  }

  /** Tipin kapatamadığı tek delik: açık zorlama. */
  function zorlamalar(metin: string): string[] {
    return [
      ...[...metin.matchAll(/"([^"]*)"\s+as\s+ErrorKind/g)].map((m) => m[1]!),
      ...[...metin.matchAll(/error_kind:\s*"([^"]*)"/g)].map((m) => m[1]!),
    ];
  }

  it("sözlük şablon değişmezini de kabul ediyor", () => {
    expect(sozluktenMi("http_404")).toBe(true);
    expect(sozluktenMi("http_503")).toBe(true);
    expect(sozluktenMi("identity")).toBe(true);
    expect(sozluktenMi("golden grubu kapsam disinda")).toBe(false);
    expect(sozluktenMi("http_")).toBe(false);
  });

  it("tarayıcı zorlamayı buluyor", () => {
    expect(zorlamalar('const k = "uydurma" as ErrorKind;')).toEqual(["uydurma"]);
    expect(zorlamalar('error_kind: "uydurma2"')).toEqual(["uydurma2"]);
    expect(zorlamalar("error_kind: errorKind(cause)")).toEqual([]);
  });

  const KAYNAK_DOSYALARI = (() => {
    const kok = fileURLToPath(new URL("..", import.meta.url));
    const sonuc: string[] = [];

    for (const dizin of ["src/app", "src/lib", "src/components"]) {
      for (const girdi of readdirSync(join(kok, dizin), { withFileTypes: true, recursive: true })) {
        if (girdi.isFile() && /\.tsx?$/.test(girdi.name)) {
          sonuc.push(join(girdi.parentPath ?? girdi.path, girdi.name));
        }
      }
    }

    return sonuc;
  })();

  it("depoda tipi zorlayan hiçbir yer yok", () => {
    const ihlaller: string[] = [];

    for (const dosya of KAYNAK_DOSYALARI) {
      for (const deger of zorlamalar(readFileSync(dosya, "utf8"))) {
        if (!sozluktenMi(deger)) {
          ihlaller.push(`${dosya}: "${deger}"`);
        }
      }
    }

    expect(
      ihlaller,
      "`error_kind` sözlük dışı bir metne zorlanıyor. `as ErrorKind`, tip " +
        "sisteminin kapattığı deliği elle açmak demek — sunucunun cümlesinin " +
        "telemetriye sızma yolu tam olarak budur.",
    ).toEqual([]);
  });

  it("dosya listesi boş değil — tarayıcı gerçekten bir yere bakıyor", () => {
    expect(KAYNAK_DOSYALARI.length).toBeGreaterThan(20);
  });
});

describe("yamlLines — boyut gidiyor, içerik değil", () => {
  it("satır sayıyor", () => {
    expect(yamlLines("a\nb\nc")).toBe(3);
    expect(yamlLines("")).toBe(0);
  });

  it("içerikten hiçbir şey dönmüyor", () => {
    const yaml = 'pattern: "%{IP:src_ip} kullanici=admin"';

    expect(String(yamlLines(yaml))).not.toContain("admin");
    expect(String(yamlLines(yaml))).not.toContain("src_ip");
  });
});
