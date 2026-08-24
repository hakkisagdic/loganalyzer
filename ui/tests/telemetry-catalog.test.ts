import { readFileSync } from "node:fs";

import { describe, expect, it } from "vitest";

import { EVENTS, type EventName } from "@/lib/telemetry/events";

import { kaynakDosyalari, kisaYol } from "./source-tree";

/**
 * **Üreticisi olmayan bir olay bir iddiadır.**
 *
 * <p>
 * CLAUDE.md §8 "tüketicisi olmayan bir tip tahmindir" diyor. Bu, o cümlenin
 * tersi. Katalog bir olayı tanımladığında ürün hakkında bir şey <b>iddia
 * ediyor</b>: "bu olay olur, ve olduğunda ölçülür". Hiçbir yerden basılmayan
 * bir olay o iddiayı yerine getirmiyor, ama iddiayı geri de almıyor — panoda
 * bir sütun açılıyor, sütun boş kalıyor, ve boşluk <i>"kimse bu özelliği
 * kullanmıyor"</i> diye okunuyor. §7'nin tarif ettiği sessiz yanlış davranış
 * tam olarak bu: hata yok, sayaç yok, belirti yok.
 * </p>
 *
 * <p>
 * Ölçülen boşluk: bir turda katalog yedi olay tanımlıyordu, ikisi
 * (<c>alert_saved</c>, <c>rca_run</c>) hiçbir yerden basılmıyordu. Derleme
 * temizdi, birim testleri yeşildi, süzgeç testleri o iki olayın alanlarını
 * bile sınıyordu — çünkü hepsi katalogdan okuyordu. <b>Kataloğu hem soru hem
 * cevap sayan bir test yığını, kataloğun yalan söylediğini göremez.</b>
 * </p>
 *
 * <h3>Bekçinin NE ölçtüğü, ne ölçmediği</h3>
 *
 * <p>
 * Ölçtüğü: katalogdaki her olay adı için <c>src</c> altında en az bir
 * <c>track("ad", …)</c> / <c>trackServer("ad", …)</c> <b>çağrı yeri</b> var
 * mı.
 * </p>
 *
 * <p>
 * Ölçmediği: o çağrı yerinin <b>ulaşılabilir</b> olduğunu. Ölü bir dalın
 * içindeki çağrı bu bekçiyi memnun eder. Bunu kapatmak çağrı grafiği çıkarmak
 * demek ve doğru cevap o değil — aynı hatayı bu dosyanın komşusu bir kez
 * yaptı ve geri aldı (<c>telemetry-instrumentation.test.ts</c>, "tarayıcıyı
 * büyütmek mümkündü ama yanlış cevaptı"). Burada sınır <b>yazılı</b>, çünkü
 * yazılmamış bir sınır kapsanmış sanılıyor.
 * </p>
 */

/**
 * **Katalog dosyasının kendisi üretici sayılmıyor.**
 *
 * <p>
 * Bu satır bekçinin en önemli satırı. Kataloğu hem kaynak hem hedef sayan bir
 * test kendi kendini tatmin eder: <c>events.ts</c> her olay adını zaten
 * taşıyor, dolayısıyla onu tarayan bir bekçi <b>her zaman</b> yeşil yanar ve
 * yeşilliği hiçbir şey ifade etmez (§7'deki <c>Produces&lt;T&gt;</c> kapısı
 * aynen böyle üç yeşil test verirken 16 ucu görmüyordu).
 * </p>
 *
 * <p>
 * Dışlama bugün boşta duruyor — <c>events.ts</c>'te çağrı yok, ve bunu
 * aşağıdaki test <b>ölçüyor</b>. Dışlama yine de burada, çünkü bir gün
 * katalogda bir çağrı belirirse bekçinin sessizce kendi kendini onaylamaya
 * başlaması için başka bir hata gerekmesin.
 * </p>
 */
const KATALOG_DOSYASI = "src/lib/telemetry/events.ts";

/**
 * **Bilerek üreticisiz duran olaylar.** Bugün: hiçbiri.
 *
 * <p>
 * Liste <c>ProducesContractTests.Exempt</c> ile aynı disiplinde: sayısı
 * <see cref="BEKLENEN_MUAF_SAYISI"/> ile sabit, yani muafiyet eklemek
 * <b>iki ayrı bilinçli hareket</b> gerektiriyor — listeye satır eklemek ve
 * sayıyı büyütmek. Tek hareketle büyüyebilen bir muafiyet listesi, bekçinin
 * kendisini kapatma düğmesidir.
 * </p>
 *
 * <p>
 * Buraya bir olay eklemek "üreticisi <b>hiç</b> olmayacak" demektir; "bir gün
 * yazılacak" DEĞİL. §8'in ayrımı: ikisi tek listedeyken "liste boşaldı mı"
 * sorusunun cevabı asla evet olamaz. Bekleyen bir olay muafiyet değil, o
 * olayın katalogda henüz işi yok — katalog, üreticisiyle aynı değişiklikte
 * gelir.
 * </p>
 */
const MUAF: readonly EventName[] = [];

/** <see cref="MUAF"/> kaç satır taşımalı. Değiştirmek bilinçli bir karar. */
const BEKLENEN_MUAF_SAYISI = 0;

/**
 * Bir metindeki **olay adı yazılı** çağrı yerleri.
 *
 * <p>Çok satırlı çağrıyı da yakalıyor (`src/app/olaylar/page.tsx` gerçekten
 * öyle yazıyor: <c>await trackServer(</c> ve ad bir alt satırda).</p>
 */
function cagriYerleri(metin: string): string[] {
  return [...metin.matchAll(/\btrack(?:Server)?\s*\(\s*"([^"]+)"/g)].map((eslesme) => eslesme[1]!);
}

/**
 * Bir metindeki **bütün** çağrı yerleri — adı okunabilenler dahil.
 *
 * <p>
 * Fark önemli: <c>cagriYerleri</c>'nden fazlası, adı <b>değişkenden</b> gelen
 * bir çağrı demek (<c>track(ad, …)</c>). Öyle bir çağrı tarayıcıyı kör
 * ediyor — o dosyada hangi olayın basıldığını bu bekçi bilemez, ve bilmediğini
 * de söyleyemez. Bu depoda elle beslenen/kısmi gören listeler defalarca kör
 * çıktı; körlüğün kendisini ölçmek o hatanın panzehiri.
 * </p>
 *
 * <p>
 * Tanım satırlarını saymıyor: <c>export function track&lt;TName …&gt;(</c>
 * adından hemen sonra <c>&lt;</c> taşıyor, desen ise <c>(</c> istiyor.
 * Ölçüldü (aşağıdaki birim testi).
 * </p>
 */
function tumCagrilar(metin: string): number {
  return [...metin.matchAll(/\btrack(?:Server)?\s*\(/g)].length;
}

describe("tarayıcı — desenin ne gördüğü", () => {
  it("tek satırlık çağrıyı buluyor", () => {
    expect(cagriYerleri('track("parser_compiled", { succeeded: true });')).toEqual([
      "parser_compiled",
    ]);
  });

  it("çok satırlı çağrıyı buluyor", () => {
    const metin = 'await trackServer(\n  "error_shown",\n  { route: "/olaylar" },\n);';

    expect(cagriYerleri(metin)).toEqual(["error_shown"]);
  });

  it("`trackScreen` bir çağrı yeri DEĞİL", () => {
    // Sarmalayıcı; olayı kendisi değil içindeki `track` basıyor. İkisini bir
    // saymak, sarmalayıcının adını olay adı sanmak olurdu.
    expect(cagriYerleri('trackScreen("/kaynaklar/17");')).toEqual([]);
    expect(tumCagrilar('trackScreen("/kaynaklar/17");')).toBe(0);
  });

  it("tanım satırı bir çağrı DEĞİL", () => {
    expect(tumCagrilar("export function track<TName extends EventName>(")).toBe(0);
    expect(tumCagrilar("export async function trackServer<TName extends EventName>(")).toBe(0);
  });

  it("adı değişkenden gelen çağrıyı kör nokta olarak sayıyor", () => {
    const metin = "track(ad, {});";

    expect(cagriYerleri(metin)).toEqual([]);
    expect(tumCagrilar(metin)).toBe(1);
  });

  it("katalog metnindeki tanım bir çağrı yeri DEĞİL", () => {
    // Kendi kendini tatmin eden bekçinin tam şekli bu: katalogdaki
    // `name: "rca_run"` satırını üretici sanmak.
    const katalogSatiri = 'rca_run: event({ name: "rca_run", properties: ["signal_count"] })';

    expect(cagriYerleri(katalogSatiri)).toEqual([]);
  });
});

/** Taranan dosyalar — katalog hariç. */
const TARANAN = kaynakDosyalari().filter((dosya) => kisaYol(dosya) !== KATALOG_DOSYASI);

/** Olay adı → onu basan dosyaların kısa yolları. */
const URETICILER: ReadonlyMap<string, readonly string[]> = (() => {
  const harita = new Map<string, string[]>();

  for (const dosya of TARANAN) {
    for (const ad of cagriYerleri(readFileSync(dosya, "utf8"))) {
      const mevcut = harita.get(ad);

      if (mevcut === undefined) {
        harita.set(ad, [kisaYol(dosya)]);
      } else if (!mevcut.includes(kisaYol(dosya))) {
        mevcut.push(kisaYol(dosya));
      }
    }
  }

  return harita;
})();

describe("olay kataloğu — üreticisi olmayan olay", () => {
  const KATALOG_ADLARI = Object.keys(EVENTS) as EventName[];

  it("anahtar ile `name` aynı — tarayıcının dayandığı varsayım", () => {
    // `track` çağrısı ANAHTARI yazıyor (`EventName = keyof typeof EVENTS`),
    // PostHog'a giden ise `name`. İkisi ayrışırsa bu bekçi doğru olayı arıyor
    // ama pano başka bir ad görüyor — ve ikisi de kimseye görünmez.
    for (const ad of KATALOG_ADLARI) {
      expect(EVENTS[ad].name, `katalog anahtarı "${ad}"`).toBe(ad);
    }
  });

  it("katalogdaki her olayın en az bir üreticisi var", () => {
    const uretilmeyen = KATALOG_ADLARI.filter(
      (ad) => !URETICILER.has(ad) && !MUAF.includes(ad),
    );

    expect(
      uretilmeyen,
      "Katalogda tanımlı ama `src` altında hiçbir yerden basılmayan olay(lar). " +
        "Üreticisi olmayan bir olay bir iddiadır: panoda sütun açar, sütun boş " +
        "kalır, ve boşluk \"kimse kullanmıyor\" diye okunur. Ya olayı basan çağrıyı " +
        "yazın, ya olayı katalogdan çıkarın — bilerek üreticisiz duruyorsa `MUAF`'a " +
        "ekleyip `BEKLENEN_MUAF_SAYISI`'nı da güncelleyin (iki ayrı hareket).",
    ).toEqual([]);
  });

  it("basılan her olay katalogda tanımlı", () => {
    // Tersi de sessiz: katalogdan çıkarılmış bir adı basmaya devam eden ekran
    // `scrubProperties`'in beyaz listesini bulamaz. Tip sistemi bunu zaten
    // engelliyor; burası tipin kapatamadığı `as` / `any` deliğine bakıyor.
    const yabanci = [...URETICILER.keys()].filter(
      (ad) => !(KATALOG_ADLARI as string[]).includes(ad),
    );

    expect(yabanci, "Katalogda olmayan bir olay basılıyor.").toEqual([]);
  });

  it("tarayıcının kör noktası yok — her çağrının adı okunabiliyor", () => {
    const korler: string[] = [];

    for (const dosya of TARANAN) {
      const metin = readFileSync(dosya, "utf8");
      const okunabilir = cagriYerleri(metin).length;
      const toplam = tumCagrilar(metin);

      if (toplam > okunabilir) {
        korler.push(`${kisaYol(dosya)}: ${toplam - okunabilir} çağrı`);
      }
    }

    expect(
      korler,
      "Olay adı değişkenden gelen bir `track`/`trackServer` çağrısı var. Bu " +
        "bekçi o dosyada hangi olayın basıldığını göremiyor, yani üreticisiz " +
        "bir olayı da göremez — kapı sessizce açılmış olur.",
    ).toEqual([]);
  });
});

describe("bekçinin kendisi", () => {
  it("tarayıcı gerçekten bir yere bakıyor", () => {
    // Boş bir dosya listesi üzerinde "üreticisiz olay yok" iddiası da yeşil
    // yanardı — ve hiçbir şey ifade etmezdi.
    expect(TARANAN.length).toBeGreaterThan(20);
    expect(URETICILER.size).toBeGreaterThan(0);
  });

  it("katalog dosyası taramanın dışında", () => {
    expect(TARANAN.map(kisaYol)).not.toContain(KATALOG_DOSYASI);
  });

  it("katalog dosyası zaten hiçbir olay basmıyor", () => {
    // Dışlamanın bugün boşta durduğunun ÖLÇÜMÜ. Bu test kırmızı yanarsa
    // dışlama artık yük taşıyor demektir ve yukarıdaki gerekçe okunmalı.
    const katalog = kaynakDosyalari().find((dosya) => kisaYol(dosya) === KATALOG_DOSYASI);

    expect(katalog, `${KATALOG_DOSYASI} bulunamadı — dışlama yanlış yolu tutuyor`).toBeDefined();
    expect(cagriYerleri(readFileSync(katalog!, "utf8"))).toEqual([]);
  });

  it("muafiyet listesi sabit sayıda", () => {
    expect(
      MUAF.length,
      "`MUAF` değişti ama `BEKLENEN_MUAF_SAYISI` değişmedi. Muafiyet eklemek " +
        "iki ayrı bilinçli hareket gerektiriyor (§8).",
    ).toBe(BEKLENEN_MUAF_SAYISI);
  });

  it("muaf olan her ad katalogda gerçekten var", () => {
    // Katalogdan çıkmış bir olayın muafiyeti geride kalırsa liste, artık
    // olmayan bir şeyi affeder ve "liste boşaldı mı" sorusu bozulur.
    for (const ad of MUAF) {
      expect(Object.keys(EVENTS), `muaf "${ad}"`).toContain(ad);
    }
  });

  it("üreticisi doğan bir olay muaf kalamaz", () => {
    const gereksiz = MUAF.filter((ad) => URETICILER.has(ad));

    expect(
      gereksiz,
      "Bu olay(lar)ın artık üreticisi var; muafiyeti kaldırın. Hak edilmemiş " +
        "bir muafiyet, bir sonraki gerçek boşluğu da örter.",
    ).toEqual([]);
  });
});
