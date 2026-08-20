import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { chromium, type Browser } from "playwright";
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
