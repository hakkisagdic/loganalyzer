import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { chromium, type Browser, type Page } from "playwright";
import { renderToStaticMarkup } from "react-dom/server";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { SCENES } from "./scenes";

/**
 * Ekran görüntüleri — **açık ve koyu temada** (T28 kabul kriteri).
 *
 * <p>
 * Kontrast oranı sayıyla ölçülebiliyor (<c>contrast.test.ts</c>), ama "bu ekran
 * gerçekten okunuyor mu" ölçülemiyor: uzun Arapça bir gövdenin hücreyi taşırıp
 * taşırmadığı, boşluksuz CJK metninin satır kırıp kırmadığı, 500 satırlık bir
 * tablonun düzeni bozup bozmadığı ancak bakılarak görülüyor.
 * </p>
 *
 * <h3>Neden sunucu ayağa kaldırılmıyor</h3>
 *
 * <p>
 * Rotaları çekmek için Next sunucusu + sahte Keycloak + sahte API gerekiyordu:
 * üç uzun ömürlü proses. Protokolün §3'ü tam olarak o riski anlatıyor ve bu
 * makinede beş ajan çalışıyor. Bunun yerine bileşenler doğrudan çiziliyor,
 * <b>gerçek jetonlar ve gerçek bileşen CSS'i</b> bağlanıyor. Kanıtlanan şey
 * görünüm; kanıtlanmayan şey yönlendirme ve kimlik akışı — onu T27'nin uçtan
 * uca akışları karşılıyor.
 * </p>
 *
 * <h3>Sınıf adları</h3>
 *
 * <p>
 * Vitest, CSS modül sınıflarını <c>_button_2684fe</c> gibi hash'liyor. Kaynak
 * CSS'te seçiciler düz (<c>.button</c>), dolayısıyla hash sökülüp ham dosya
 * bağlanıyor. Çakışma riski var ama sahne başına yalnızca <b>gereken</b>
 * modüller bağlanarak dar tutuluyor.
 * </p>
 */

const UI = fileURLToPath(new URL("../..", import.meta.url));
const OUT = join(UI, "..", "docs", "ekran-goruntuleri");
const WORK = join(UI, ".screenshots-tmp");

const THEMES = ["light", "dark"] as const;

/** Dizüstü genişliği. Ticket 1280–1920 aralığını istiyor; 1440 ortası. */
const VIEWPORT = { width: 1440, height: 900 };

function css(relativePath: string): string {
  return readFileSync(join(UI, "src", relativePath), "utf8");
}

/** `_button_2684fe` → `button`. */
function unhash(html: string): string {
  return html.replace(/_([A-Za-z][A-Za-z0-9]*)_[a-z0-9]+/g, "$1");
}

/**
 * Her sahnede bağlanan taban.
 *
 * <p>
 * <c>ui.module.css</c> buraya <b>bakılarak</b> eklendi: ilk koşumda yalnızca
 * ekrana özgü modüller bağlanmıştı ve görüntülerde hücreler ortalanmış,
 * rozetler düz metin, uzun gövde kırpılmamış çıktı. Test yeşildi — çünkü
 * sayfanın boyanıp boyanmadığını sınıyordu, doğru göründüğünü değil. Ortak
 * bileşenlerin biçimi orada, dolayısıyla her sahnede gerekiyor.
 * </p>
 */
const BASE_STYLES = [
  "app/tokens.css",
  "app/globals.css",
  "components/ui/ui.module.css",
] as const;

function page(title: string, theme: string, body: string, styles: readonly string[]): string {
  const sheets = [...BASE_STYLES, ...styles].map(css).join("\n");

  return `<!doctype html>
<html lang="tr" data-theme="${theme}">
<head><meta charset="utf-8"><title>${title}</title><style>
${sheets}
/* Sahne çerçevesi: ekranların ana içerik alanıyla aynı ölçüler. */
body { padding: 2rem; }
main { max-inline-size: var(--content-width); margin-inline: auto;
       display: flex; flex-direction: column; gap: var(--space-5); }
h1 { font-size: var(--text-lg); color: var(--text-2); }
</style></head>
<body><main><h1>${title}</h1>${body}</main></body></html>`;
}

let browser: Browser;

beforeAll(async () => {
  mkdirSync(WORK, { recursive: true });
  mkdirSync(OUT, { recursive: true });
  browser = await chromium.launch();
}, 120_000);

afterAll(async () => {
  // Protokol §3: başlattığın prosesi temizle — hata alsan bile. Tarayıcı
  // `afterAll`da kapanıyor, `finally` semantiğiyle: test düşse de çalışıyor.
  await browser?.close();
  rmSync(WORK, { recursive: true, force: true });
});

describe("ekran görüntüleri", () => {
  it.each(
    SCENES.flatMap((scene) => THEMES.map((theme) => ({ scene, theme }))),
  )(
    "$scene.id — $theme",
    async ({ scene, theme }) => {
      const html = page(scene.title, theme, unhash(renderToStaticMarkup(scene.node)), scene.styles);
      const file = join(WORK, `${scene.id}-${theme}.html`);
      writeFileSync(file, html, "utf8");

      const tab = await browser.newPage({ viewport: VIEWPORT });

      try {
        await tab.goto(`file://${file}`);
        await tab.screenshot({
          path: join(OUT, `${scene.id}-${theme}.png`),
          fullPage: scene.fullPage ?? true,
        });

        // Görüntü boş çıkmasın: jetonlar bağlanmadıysa sayfa beyaz kalır ve
        // "aldım" demek yanlış olurdu.
        const painted = await tab.evaluate(() =>
          getComputedStyle(document.body).backgroundColor,
        );

        expect(painted, "jetonlar bağlanmamış — sayfa boyanmamış").not.toBe("rgba(0, 0, 0, 0)");
      } finally {
        await tab.close();
      }
    },
    60_000,
  );
});

/**
 * **Geometri bekçileri** (T27).
 *
 * <p>
 * Yukarıdaki paket 18 test <b>koşuyor</b> ama tek bir şey <b>iddia ediyor</b>:
 * sayfanın arka planı saydam değil — yani boyandı. Bu, bu depoda bir kez
 * ödenmiş bedelin aynısı: ilk koşumda hücreler ortalanmış, rozetler düz metin
 * ve kırpma yoktu, <b>ve test geçti</b>. Görüntüler bir insanın bakması için
 * üretilen çıktılar; bakan olmazsa hiçbir şey söylemiyorlar.
 * </p>
 *
 * <h3>Neden T28'in bekçilerinin kopyası değil</h3>
 *
 * <p>
 * <c>ui-consistency</c> ve <c>contrast</c>'ın <b>hepsi statik</b>: kaynak
 * tarıyor, sınıf adı okuyor, <c>tokens.css</c> ayrıştırıyor. Hiçbiri
 * <b>yerleşim</b> sorusuna cevap veremez, çünkü cevabı yalnızca bir yerleşim
 * motoru biliyor: metin taştı mı, kırpma gerçekten kesti mi, satır hücrenin
 * dışına çıktı mı.
 * </p>
 *
 * <p>
 * <c>ui.module.css</c>'in kendi yorumu bunu zaten söylüyor: <i>"Ekran
 * görüntüsünde yakalandı; hiçbir birim testi bunu göremezdi."</i> Buradaki iş,
 * o cümledeki <b>"görüntüde yakalandı"</b>yı <b>"bir iddia tutuyor"</b>a
 * çevirmek.
 * </p>
 *
 * <h3>Tema başına tekrarlanmıyor — bilerek</h3>
 *
 * <p>
 * Geometri temadan bağımsız. Aynı sondaları iki temaya koşturmak koşan test
 * sayısını iki katına çıkarır ve <b>tek bir yeni iddia</b> üretmezdi — bu
 * paketin zaten eleştirilen yanı tam olarak buydu.
 * </p>
 */
describe("geometri — yalnızca tarayıcının bilebileceği", () => {
  /** Sahneyi açıp sondayı koşturuyor; sekme her koşulda kapanıyor (§3). */
  async function probe<T>(sceneId: string, fn: (tab: Page) => Promise<T>): Promise<T> {
    const scene = SCENES.find((s) => s.id === sceneId);

    // Sahne yeniden adlandırılırsa test SESSİZCE hiçbir şey sınamaz hâle
    // gelmesin: bulunamayan sahne bir hata.
    if (!scene) {
      throw new Error(`'${sceneId}' sahnesi yok — geometri bekçisi bayatlamış.`);
    }

    const file = join(WORK, `${scene.id}-geometri.html`);
    writeFileSync(
      file,
      page(scene.title, "light", unhash(renderToStaticMarkup(scene.node)), scene.styles),
      "utf8",
    );

    const tab = await browser.newPage({ viewport: VIEWPORT });

    try {
      await tab.goto(`file://${file}`);
      return await fn(tab);
    } finally {
      await tab.close();
    }
  }

  it.each(SCENES.map((scene) => scene.id))(
    "%s — sayfa yatayda taşmıyor",
    async (sceneId) => {
      // Ekrandan taşan bir tablo, kullanıcıda yatay kaydırma çubuğu ve kesilmiş
      // sütun demek. DOM'dan görünmüyor; yalnızca yerleşim motoru biliyor.
      const overflow = await probe(sceneId, (tab) =>
        tab.evaluate(() => ({
          scroll: document.documentElement.scrollWidth,
          client: document.documentElement.clientWidth,
        })),
      );

      expect(
        overflow.scroll,
        `${sceneId}: sayfa ${overflow.scroll}px, görünüm ${overflow.client}px`,
      ).toBeLessThanOrEqual(overflow.client + 1);
    },
    60_000,
  );

  it("uzun gövde dört satırda kesiliyor — ve kesilecek kadar uzun", async () => {
    const bodies = await probe("olaylar-dolu", (tab) =>
      tab.evaluate(() =>
        [...document.querySelectorAll(".cellBodyText")].map((el) => {
          const style = getComputedStyle(el);
          return {
            height: el.getBoundingClientRect().height,
            lineHeight: Number.parseFloat(style.lineHeight),
            scrollHeight: el.scrollHeight,
            clientHeight: el.clientHeight,
          };
        }),
      ),
    );

    // Sahne kırpılacak gövde taşımıyorsa iddia boş kümede geçer ve hiçbir şey
    // ifade etmez — bu depodaki "yeşil ama anlamsız" sınıfı.
    expect(bodies.length).toBeGreaterThan(0);
    expect(
      bodies.some((b) => b.scrollHeight > b.clientHeight),
      "hiçbir gövde kırpılacak kadar uzun değil — sahne iddiayı taşımıyor",
    ).toBe(true);

    for (const body of bodies) {
      expect(body.height).toBeLessThanOrEqual(body.lineHeight * 4 + 1);
    }
  }, 60_000);

  it("rozet düz metin değil — zemini ve dolgusu var", async () => {
    const badges = await probe("rozetler", (tab) =>
      tab.evaluate(() =>
        [...document.querySelectorAll(".badge")].map((el) => {
          const style = getComputedStyle(el);
          return {
            background: style.backgroundColor,
            paddingBlock: Number.parseFloat(style.paddingBlockStart),
            paddingInline: Number.parseFloat(style.paddingInlineStart),
          };
        }),
      ),
    );

    expect(badges.length).toBeGreaterThan(0);

    for (const badge of badges) {
      // Saydam zemin = düz metin. İlk koşumda tam olarak bu olmuştu.
      expect(badge.background).not.toBe("rgba(0, 0, 0, 0)");
      expect(badge.paddingBlock).toBeGreaterThan(0);
      expect(badge.paddingInline).toBeGreaterThan(0);
    }
  }, 60_000);


  // ─────────────────────────────────────────────────────────────────────────
  // YAZILDI, ÖLÇÜLEMEDİ, ÇIKARILDI — iki iddia (T27).
  //
  // "Kırpılan gövde satırın dışına taşmıyor" ve "hücre metni ortalanmıyor"
  // yazıldı ve YEŞİL yandı, ama kırmızı yanabildikleri GÖSTERİLEMEDİ:
  //
  //   * kırpmayı `.cellBody`'ye (hücrenin kendisine) taşıdım — ölçülmüş asıl
  //     kusurun birebir hâli — paket 31/31 yeşil kaldı;
  //   * sahne çerçevesine `td { text-align: center }` koydum — orijinal
  //     belirtilerden biri — yine 31/31 yeşil.
  //
  // Yani ikisi de bir şey tuttuğunu kanıtlayamadı. Sevk edilseler koşan test
  // sayısını artırıp iddia sayısını artırmazlardı, ki bu paketin eleştirilme
  // sebebi tam olarak buydu (§6: kırmızı yanabildiğini ölç, sonra geri al).
  //
  // Bir sonraki denemeye not: sentetik sahnede satır boyutlanması gerçek
  // ekrandakinden farklı davranıyor olabilir; kusuru üretmenin yolu önce
  // bulunmalı, iddia sonra yazılmalı.
});
