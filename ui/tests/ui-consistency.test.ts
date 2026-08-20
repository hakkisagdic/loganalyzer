import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

/**
 * Ekranların **birlikte bir ürün gibi** durduğunu tutan bekçiler (T28).
 *
 * <p>
 * Ticket'ın gerekçesi: tek tek çalışan yedi ekran, yedi ayrı ürün gibi
 * duruyorsa F2 yarım bitmiş demektir. Bu dosyanın işi o cümleyi <b>ölçülebilir</b>
 * hâle getirmek — "şu ekran farklı görünüyor" bir sonraki turda kaybolur, bir
 * bekçi kalır.
 * </p>
 *
 * <p>
 * Kurallar <b>ekran adı geçmeden</b> yazılı ve bu bilinçli: denetimi yapan ajan
 * ekranların bir kısmını kendisi yazdı. Kural herkese aynı uygulanırsa kimin
 * yazdığı sorusu düşüyor — ve yarın inen bir ekran kendiliğinden kapsama giriyor.
 * </p>
 */

const SRC = fileURLToPath(new URL("../src", import.meta.url));
const APP = join(SRC, "app");

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const full = join(dir, entry);
    return statSync(full).isDirectory() ? walk(full) : [full];
  });
}

const ALL_FILES = walk(SRC);
const CODE = ALL_FILES.filter((f) => f.endsWith(".ts") || f.endsWith(".tsx"));
const STYLES = ALL_FILES.filter((f) => f.endsWith(".css"));

function read(file: string): string {
  return readFileSync(file, "utf8");
}

/**
 * Yorumları çıkarıyor.
 *
 * <p>
 * Bu satır bekçinin kendi hatasından doğdu: ilk hâli belge yorumlarındaki
 * örnek <c>&lt;table&gt;</c> etiketlerini ihlal saydı. Bir bekçinin yanlış
 * pozitifi, kırmızı yanmamasından farklı bir tehlike — insanlar onu susturmayı
 * öğreniyor.
 * </p>
 */
function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
}

function rel(file: string): string {
  return relative(SRC, file);
}

// ------------------------------------------------------------ yükleniyor durumu

/**
 * Veri çeken **her rota** bir yükleniyor geri bildirimi taşımalı.
 *
 * <p>
 * Denetimin en ciddi bulgusu buydu: istemcide veri çeken ekranlar iskelet
 * gösteriyordu, <b>sunucu bileşeni olanlar hiçbir şey</b>. Sunucu bileşeni
 * HTML'i veri gelene kadar üretmiyor, yani ClickHouse sorgusu sürerken tarayıcı
 * önceki sayfada duruyor ve kullanıcı bir şeyin çalışıp çalışmadığını
 * anlamıyordu.
 * </p>
 *
 * <p>
 * İki karşılık kabul ediliyor: sunucu tarafında <c>loading.tsx</c> (Suspense
 * sınırı), istemci tarafında <c>LoadingState</c>.
 * </p>
 */
describe("her rotanın yükleniyor durumu var", () => {
  /**
   * Veri çekmeyen rotalar.
   *
   * <p>Muafiyet <b>gerekçeli</b>: bu rota hiçbir uca gitmiyor, dolayısıyla
   * beklenecek bir şey yok. Veri çekmeye başlarsa muafiyet düşmeli.</p>
   */
  const EXEMPT: Record<string, string> = {
    "app/giris/page.tsx": "Giriş sayfası; veri çekmiyor, yalnızca yönlendirme metni gösteriyor.",
  };

  const pages = CODE.filter((f) => f.endsWith(`${"/"}page.tsx`) && f.startsWith(APP));

  /** Rotanın kendi klasörü ya da bir üst klasörü — <b>kök hariç</b>. */
  function hasLoadingFile(pageFile: string): boolean {
    let dir = join(pageFile, "..");

    while (dir.startsWith(APP) && dir !== APP) {
      if (ALL_FILES.includes(join(dir, "loading.tsx"))) {
        return true;
      }

      dir = join(dir, "..");
    }

    // Kök sınırı YALNIZCA kökün kendi rotasını kapsıyor sayılıyor. Next açısından
    // iç içe rotaları da kapsıyor, ama onu geçerli saymak bir ekranın kendi
    // sınırını unutmuş olmasını görünmez kılardı.
    return dir === APP && join(pageFile, "..") === APP && ALL_FILES.includes(join(APP, "loading.tsx"));
  }

  /** Rotanın üst düzey bölümünde herhangi bir yerde `LoadingState`. */
  function hasLoadingState(pageFile: string): boolean {
    const segment = relative(APP, pageFile).split("/")[0]!;
    const root = segment.endsWith(".tsx") ? APP : join(APP, segment);

    return CODE.filter((f) => f.startsWith(root)).some((f) => read(f).includes("LoadingState"));
  }

  it("veri çeken her sayfa ya `loading.tsx` ya `LoadingState` taşıyor", () => {
    const missing = pages
      .filter((page) => !hasLoadingFile(page) && !hasLoadingState(page))
      .map(rel)
      .filter((name) => !(name in EXEMPT))
      .sort();

    expect(
      missing,
      "Yükleniyor durumu olmayan rota(lar). Sunucu bileşeniyse yanına `loading.tsx` " +
        "ekleyin, istemciyse `LoadingState` çizin; gerçekten veri çekmiyorsa " +
        "EXEMPT'e gerekçesiyle yazın.",
    ).toEqual([]);
  });

  it("muafiyetler gerekçeli ve hâlâ var olan rotalar", () => {
    for (const [route, reason] of Object.entries(EXEMPT)) {
      expect(reason.length, `${route} muafiyeti gerekçesiz`).toBeGreaterThan(20);
      expect(pages.map(rel), `${route} artık yok — muafiyeti silin`).toContain(route);
    }
  });
});

// ------------------------------------------------------------ dört durumun sırası

describe("dört durumun sırası tek yerde", () => {
  /**
   * Sıra kırılgan: <b>hata boş durumu bastırmalı</b>, çünkü hata varken "kayıt
   * yok" demek kullanıcıya yanlış bilgi vermek. Her ekranın kendi sırasını
   * yazması, o kararın ekran sayısı kadar kez verilmesi demek.
   */
  it("`screenState` yalnızca bir kez tanımlı ve ortak kitte", () => {
    const definitions = CODE.filter((f) => /export function screenState\b/.test(read(f))).map(rel);

    expect(definitions).toEqual(["lib/ui/screen-state.ts"]);
  });

  /**
   * Kör noktanın <b>ucuz yarısı</b>.
   *
   * <p>
   * Yukarıdaki iki test <c>screenState</c> <b>tanımlarını</b> ada göre buluyor;
   * bir ekranın sırayı JSX içinde satır içi yazmasını yakalamıyordu. Sırayı
   * JSX'te analiz etmek kırılgan, o yüzden kovalanmadı — ama sorunun ucuz bir
   * yarısı var: <b>veri çeken</b> ve <c>EmptyState</c> çizen bir dosya,
   * <c>ErrorState</c>'i ondan <b>önce</b> çizmek zorunda.
   * </p>
   *
   * <p>
   * Kaynak sırası kaba bir vekil ama bu depoda anlamlı: erken dönüşler ve JSX
   * dalları yukarıdan aşağı yazılıyor. Kaçırdığı hâller kalıyor ve bu bilinçli —
   * kural <b>ölçülerek</b> daraltıldı. İlk hâli "her <c>EmptyState</c> çizen
   * ekran" idi ve 16 dosyanın 13'ünü boşuna işaretliyordu; üçü tek tek açılıp
   * doğru sırada oldukları görüldü. İkinci daraltma veri çekmeyen sunum
   * bileşenlerini (<c>GateReport</c>, <c>TriggerHistory</c>) dışarıda bıraktı:
   * yükleme hatası olmayan bir bileşenden hata durumu istemek yanlış pozitiftir.
   * </p>
   */
  it("veri çeken ekranda hata, boş durumdan önce geliyor", () => {
    const offenders = CODE.filter((f) => f.startsWith(APP))
      .map((f) => ({ file: f, source: stripComments(read(f)) }))
      .filter(({ source }) => source.includes("<EmptyState"))
      // Veri çekmeyen sunum bileşeninin yükleme hatası olamaz.
      .filter(({ source }) => /\b(api|serverApi)\.\w+\(|\bfetch\(/.test(source))
      // Ortak sırayı kullanan zaten doğru; tekrar sınamanın anlamı yok.
      .filter(({ source }) => !source.includes("screenState"))
      .filter(({ source }) => {
        const empty = source.indexOf("<EmptyState");
        const error = source.indexOf("<ErrorState");

        return error === -1 || error > empty;
      })
      .map(({ file }) => rel(file))
      .sort();

    expect(
      offenders,
      "Hata durumu boş durumdan sonra çiziliyor. Hata varken \"kayıt yok\" demek " +
        "kullanıcıya yanlış bilgi vermek: kayıt olabilir, yalnızca okunamadı. " +
        "`screenState` kullanın ya da `ErrorState`'i öne alın.",
    ).toEqual([]);
  });

  it("hiçbir ekran kendi `ScreenState` tipini yazmıyor", () => {
    const offenders = CODE.filter(
      (f) => /export type ScreenState\b/.test(read(f)) && rel(f) !== "lib/ui/screen-state.ts",
    ).map(rel);

    expect(offenders).toEqual([]);
  });
});

// ------------------------------------------------------------ jeton disiplini

describe("renk yalnızca jetonlardan geliyor", () => {
  /**
   * <c>tokens.css</c> paletin tanımlandığı yer, dolayısıyla ham değer <b>orada</b>
   * olmak zorunda. Başka her yerde bir ham renk, temaya uymayan ve koyu temada
   * ne olacağı düşünülmemiş bir değer demek.
   */
  it("`tokens.css` dışında ham renk yok", () => {
    const offenders = STYLES.filter((f) => !f.endsWith("tokens.css"))
      .flatMap((f) =>
        read(f)
          .split("\n")
          .map((line, index) => ({ file: rel(f), line: index + 1, text: line }))
          .filter(({ text }) => /#[0-9a-fA-F]{3,8}\b/.test(text) && !text.trim().startsWith("*"))
          .filter(({ text }) => !/rgb|hsl/.test(text)),
      )
      .map(({ file, line, text }) => `${file}:${line} ${text.trim()}`);

    expect(offenders, "Ham renk değeri — `tokens.css`'ten bir jeton kullanın").toEqual([]);
  });
});

// ------------------------------------------------------------ Türkçe kasa

describe("Türkçe kasa dönüşümü", () => {
  /**
   * <c>toUpperCase</c> Türkçede <b>yanlış</b>: <c>i</c> → <c>I</c> üretiyor,
   * oysa Türkçede <c>İ</c> olmalı; <c>I</c> → <c>i</c> de aynı şekilde ters.
   * .NET tarafında bu kural derleme zamanında zorlanıyor (CA1304/CA1311); UI
   * tarafındaki karşılığı bu bekçi.
   *
   * <p>
   * Kural <b>düz yasak değil</b>: girdisi kanıtlanabilir biçimde ASCII olan
   * çağrılar meşru. Muafiyet dar, gerekçeli ve sayısı sabit — kaçış kapısı
   * sessizce genişleyemesin.
   * </p>
   */
  const EXEMPT: Record<string, string> = {
    "lib/api/client.ts": "HTTP yöntemi; tipi `\"get\"|\"post\"|…`, yani ASCII olduğu kanıtlanabilir.",
    "lib/api/server.ts": "HTTP yöntemi; aynı gerekçe.",
  };

  const EXPECTED_EXEMPT_COUNT = 2;

  it("kasa dönüşümü yalnızca gerekçeli yerlerde", () => {
    const offenders = CODE.filter((f) =>
      /\.to(Locale)?(Upper|Lower)Case\s*\(/.test(read(f)),
    )
      .map(rel)
      .filter((name) => !(name in EXEMPT))
      .sort();

    expect(
      offenders,
      "Kasa dönüşümü Türkçede yanlış sonuç veriyor (i→I yerine i→İ olmalı). " +
        "Girdi kanıtlanabilir biçimde ASCII ise EXEMPT'e gerekçesiyle yazıp " +
        "EXPECTED_EXEMPT_COUNT'u güncelleyin.",
    ).toEqual([]);
  });

  /**
   * CSS <c>text-transform: uppercase</c> Türkçede ancak <c>lang="tr"</c> ile
   * doğru çalışıyor: <c>i</c> → <c>İ</c>. Öznitelik düşerse altı etiket sessizce
   * yanlış kasada yazılır (<c>KESIF</c> gibi) ve hiçbir test bunu görmez —
   * <c>toUpperCase</c> bekçisi JavaScript'e bakıyor, CSS'e değil.
   */
  it("CSS kasa dönüşümü `lang=\"tr\"` ile korunuyor", () => {
    const usesTransform = STYLES.some((f) =>
      /text-transform:\s*(uppercase|lowercase|capitalize)/.test(read(f)),
    );

    if (!usesTransform) {
      return;
    }

    const layout = CODE.find((f) => rel(f) === "app/layout.tsx");

    expect(layout, "kök düzen bulunamadı").toBeDefined();
    expect(
      read(layout!),
      'CSS kasa dönüşümü var ama kök düzende `lang="tr"` yok — Türkçe i/İ yanlış çıkar',
    ).toContain('lang="tr"');
  });

  it("muafiyet listesi sessizce büyümüyor", () => {
    // Sayının sabitlenmesi, listeyi genişletmeyi ayrı ve görünür bir karar
    // yapıyor — `ProducesContractTests` kalıbının aynısı.
    expect(Object.keys(EXEMPT)).toHaveLength(EXPECTED_EXEMPT_COUNT);

    for (const [file, reason] of Object.entries(EXEMPT)) {
      expect(reason.length, `${file} muafiyeti gerekçesiz`).toBeGreaterThan(20);
      expect(CODE.map(rel), `${file} artık yok — muafiyeti silin`).toContain(file);
    }
  });
});

// ------------------------------------------------------------ yinelenen yardımcılar

describe("aynı işi yapan iki yardımcı yok", () => {
  /**
   * Denetim üç yinelenme buldu ve üçü de <b>farklı davranıyordu</b>:
   * <c>describeError</c> iki dosyada (biri <c>Error.message</c>'ı koruyor, öbürü
   * atıyordu) ve <c>formatInstant</c> iki dosyada (biri UTC, öbürü <b>yerel
   * saat</b>). Aynı hatanın ekrana göre başka metinle çıkması ve aynı anın
   * ekrana göre başka saatle görünmesi, "yedi ayrı ürün" hissinin tam kaynağı.
   */
  const FRAMEWORK_EXPORTS = new Set(["dynamic", "metadata", "revalidate", "runtime", "default"]);

  it("dışa açılan yardımcı adları benzersiz", () => {
    const seen = new Map<string, string[]>();

    for (const file of CODE) {
      const matches = read(file).matchAll(/^export (?:function|const) ([A-Za-z_][A-Za-z0-9_]*)/gm);

      for (const match of matches) {
        const name = match[1]!;

        if (FRAMEWORK_EXPORTS.has(name)) {
          continue;
        }

        seen.set(name, [...(seen.get(name) ?? []), rel(file)]);
      }
    }

    const duplicates = [...seen.entries()]
      .filter(([, files]) => files.length > 1)
      .map(([name, files]) => `${name}: ${files.join(", ")}`)
      .sort();

    expect(
      duplicates,
      "Aynı ad iki modülde. İkisi aynı işi yapıyorsa ortak kite taşıyın; " +
        "farklı işler yapıyorsa adları ayrışmalı.",
    ).toEqual([]);
  });
});

// ------------------------------------------------------------ zaman biçimi

describe("zaman biçimi tek yerde", () => {
  /**
   * Denetimin en ciddi bulgusu üç ayrı zaman biçimlendiricisiydi ve biri
   * <b>yerel saat</b> gösteriyordu — bir alarmı log satırıyla eşleştiren
   * kullanıcı saat farkı kadar sapmış iki zaman görüyordu.
   *
   * <p>
   * Yinelenen ad bekçisi o bulguyu <b>yakalayamazdı</b>: üç kopyadan ikisinin
   * adı aynıydı, üçüncüsü <c>formatTimestamp</c> idi — aynı iş, başka ad. Bu
   * test o kör noktayı kapatıyor.
   * </p>
   *
   * <p>
   * <b>Kural "her <c>format*</c> ortak kitte" DEĞİL.</b> Ölçtüm:
   * <c>formatSeverity</c> ve <c>formatParseStatus</c> alana özgü etiket
   * tabloları ve onları ortak kite taşımak daha kötü olurdu. Yasaklanan şey
   * <b>zaman görüntüleme</b>.
   * </p>
   *
   * <p>
   * <b>Tel serileştirmesi serbest.</b> Bir form değerini API gövdesine ISO
   * olarak yazmak (<c>new Date(x).toISOString()</c>) sözleşme gereği ve üç
   * yerde meşru olarak yapılıyor. Ayrım şu: çıplak <c>toISOString()</c>
   * serileştirme, <c>slice</c>/<c>replace</c> ile parçalanmışı görüntüleme.
   * </p>
   */
  const HOME = "lib/ui/time.ts";

  /** Tartışmasız tarih görüntüleme API'leri. */
  const DATE_DISPLAY = /\.toLocaleDateString\s*\(|\.toLocaleTimeString\s*\(|Intl\.DateTimeFormat/;

  /** Tarih seçenekleriyle çağrılan `toLocaleString` — sayı biçimlemesi değil. */
  const DATE_OPTIONS =
    /\.toLocaleString\s*\([^)]*(dateStyle|timeStyle|year|month|day|hour|minute|second|timeZone)/;

  /** `toISOString()` sonrası kesme/değiştirme: görüntü için biçimleme. */
  const ISO_SLICING = /\.toISOString\s*\(\s*\)\s*\.\s*(slice|replace|substring|substr)\s*\(/;

  it("tarih görüntüleme yalnızca ortak zaman modülünde", () => {
    const offenders = CODE.map((f) => ({ file: rel(f), source: stripComments(read(f)) }))
      .filter(({ file }) => file !== HOME)
      .filter(
        ({ source }) =>
          DATE_DISPLAY.test(source) || DATE_OPTIONS.test(source) || ISO_SLICING.test(source),
      )
      .map(({ file }) => file)
      .sort();

    expect(
      offenders,
      `Zaman görüntüleme biçimlendirmesi \`${HOME}\` dışında. Aynı anın iki ekranda ` +
        "farklı görünmesi — özellikle biri yerel saatse — kullanıcıyı yanlış sonuca " +
        "götürüyor. API gövdesine ISO yazmak serbest; kesip biçimlendirmek değil.",
    ).toEqual([]);
  });
});

// ------------------------------------------------------------ hücre yerleşimi

describe("tablo hücresi tablo hücresi kalıyor", () => {
  /**
   * Bu turun ekran görüntüsüyle bulunan kusuru: kırpma kuralları doğrudan
   * <c>&lt;td&gt;</c>'ye uygulanmıştı ve <c>display: -webkit-box</c> bir hücreye
   * verilince <b>hücre tablo hücresi olmaktan çıkıyor</b> — satır ona göre
   * boyutlanmıyor ve uzun bir gövdenin son satırı tablodan taşıyor.
   *
   * <p>
   * Bekçi yerleşimi <b>simüle etmiyor</b>; kusurun mekanizmasını yasaklıyor.
   * <c>DataTable</c> hücrelere yalnızca aşağıdaki iki sınıfı veriyor, dolayısıyla
   * "bu sınıflar <c>display</c> tanımlayamaz" demek yeterli ve kesin.
   * </p>
   *
   * <p>
   * Görüntüler kusuru <b>buldu</b> ama <b>tutmuyor</b>: bir görüntü yakalamadır,
   * iddia değil. Kural yarın geri gelse görüntü değişir ve hiçbir test düşmezdi.
   * Bu test o boşluğu kapatıyor.
   * </p>
   */
  const CELL_CLASSES = ["cellBody", "cellNumeric"] as const;

  const UI_MODULE = "components/ui/ui.module.css";

  /** Bir sınıf bloğunun gövdesi. */
  function block(source: string, className: string): string | undefined {
    const start = source.indexOf(`.${className} {`);

    if (start === -1) {
      return undefined;
    }

    const end = source.indexOf("}", start);
    return end === -1 ? undefined : source.slice(start, end);
  }

  it("hücreye verilen sınıflar `display` tanımlamıyor", () => {
    const source = STYLES.map((f) => ({ file: rel(f), text: read(f) })).find(
      ({ file }) => file === UI_MODULE,
    );

    expect(source, `${UI_MODULE} bulunamadı`).toBeDefined();

    const offenders = CELL_CLASSES.filter((name) => {
      const body = block(source!.text, name);
      return body !== undefined && /(^|\n)\s*display\s*:/.test(body);
    });

    expect(
      offenders,
      "Hücreye verilen bir sınıf `display` tanımlıyor. `display` bir `<td>`'yi " +
        "tablo hücresi olmaktan çıkarıyor: satır ona göre boyutlanmıyor ve içerik " +
        "tablodan taşıyor. Kırpma/yerleşim kurallarını hücrenin İÇİNDEKİ öğeye " +
        "verin (`cellBodyText` gibi).",
    ).toEqual([]);
  });

  it("bu iki sınıf gerçekten hücreye veriliyor — bekçi doğru yeri koruyor", () => {
    // Sınıflar bir gün `<td>` yerine başka bir yere taşınırsa bu bekçi anlamsız
    // hâle gelir ve bunu fark etmemiz gerekir.
    const table = CODE.find((f) => rel(f) === "components/ui/DataTable.tsx");

    expect(table).toBeDefined();

    const source = read(table!);
    const tdBlock = source.slice(source.indexOf("<td"), source.indexOf("</td>"));

    for (const name of CELL_CLASSES) {
      expect(tdBlock, `${name} artık hücreye verilmiyor`).toContain(`styles.${name}`);
    }
  });
});

// ------------------------------------------------------------ tablo ve yazı yönü

describe("tablolar ortak bileşenden geçiyor", () => {
  /**
   * Çok dilli gövde davranışı — CJK kırılması, sağdan sola hizalama, dört
   * satırda kesme — <c>DataTable</c>'ın içinde çözülü. Ham bir <c>&lt;table&gt;</c>
   * yazan ekran o davranışın tamamını kaybediyor ve bunu ancak Arapça bir gövde
   * geldiğinde fark ediyoruz.
   */
  it("ham `<table>` yalnızca `DataTable` içinde", () => {
    const offenders = CODE.filter((f) => /<table[\s>]/.test(stripComments(read(f))))
      .map(rel)
      .filter((name) => name !== "components/ui/DataTable.tsx")
      .sort();

    expect(offenders, "Ham tablo — `DataTable` kullanın").toEqual([]);
  });

  /**
   * <c>dir</c> yalnızca iki değer alabilir ve ikisinin de gerekçesi var:
   * <c>auto</c> serbest metinde (Arapça gövde soldan sağa hizalanırsa okunamaz),
   * <c>ltr</c> yapısal metinde (hex dökümünde ve YAML girintisinde konum anlam
   * taşıyor). <c>rtl</c> sabitlemek ikisini de bozardı.
   */
  it("`dir` yalnızca `auto` ya da `ltr`", () => {
    const offenders = CODE.flatMap((f) =>
      // Üç yazım: `dir="auto"`, `dir='auto'` ve `dir={koşul ? "auto" : undefined}`.
      // Sonuncusunda küme parantezinin İÇİNDEKİ dizgiler sınanıyor; ifadenin
      // kendisi (değişken adı) bir yön değeri değil.
      [...stripComments(read(f)).matchAll(/dir=(?:"([^"]*)"|'([^']*)'|\{([^}]*)\})/g)].flatMap(
        (match) => {
          const literal = match[1] ?? match[2];

          if (literal !== undefined) {
            return [{ file: rel(f), value: literal }];
          }

          return [...(match[3] ?? "").matchAll(/["']([^"']+)["']/g)].map((inner) => ({
            file: rel(f),
            value: inner[1]!,
          }));
        },
      ),
    )
      .filter(({ value }) => value !== "auto" && value !== "ltr")
      .map(({ file, value }) => `${file}: dir="${value}"`);

    expect(offenders).toEqual([]);
  });
});
