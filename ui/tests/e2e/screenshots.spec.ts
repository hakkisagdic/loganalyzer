import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { expect, test, type Page } from "@playwright/test";

/**
 * Çalışan ürünün ekran görüntüleri — gerçek Keycloak, gerçek API, gerçek veri.
 *
 * <h3>Neden `admin` ile değil `analyst.core` ile giriliyor</h3>
 *
 * <p>
 * <c>admin</c> rolü kapsam filtresinden muaf (<c>AccessScope.System</c>) ve o
 * yolla çekilen bir görüntü, ürünün en pahalı hata sınıfının (K17) tam ortasını
 * atlardı: kapsamın gerçekten uygulandığını hiçbir şey göstermezdi. Analistin
 * gördüğü şey, <c>/network/core</c> claim'inin <c>idp_group_mapping</c>
 * üzerinden <c>golden</c>'a çevrilmiş hâli — yani görüntüdeki her satır aynı
 * zamanda kapsam yolunun kanıtı.
 * </p>
 *
 * <p>
 * İkincil faydası veri kalitesi: ClickHouse'ta eski kıyaslama koşumlarından
 * kalan gruplar var (250 bin satır, <b>tek</b> imza). <c>admin</c> onları da
 * görürdü ve tablo tekdüze çıkardı.
 * </p>
 *
 * <h3>Tema neden `data-theme` ile değil `colorScheme` ile</h3>
 *
 * <p>
 * Sabit <c>THEME_STORAGE_KEY</c> <c>ThemeToggle.tsx</c>'te ve o dosya CSS
 * modülü içeri aktarıyor; Playwright'ın çeviricisi (vitest'in aksine) CSS
 * modülü okumuyor. Sabiti buraya kopyalamak da §9'un yasakladığı ikinci kopya
 * olurdu. Bunun yerine tarayıcının <b>sistem tercihi</b> ayarlanıyor ve
 * <c>tokens.css</c>'in <c>prefers-color-scheme</c> dalı devreye giriyor —
 * kullanıcıların çoğunun gördüğü hâl zaten bu, çünkü açık bir seçim yapılmadan
 * <c>data-theme</c> hiç yazılmıyor.
 * </p>
 */

const REPO = fileURLToPath(new URL("../../..", import.meta.url));
const OUT = join(REPO, "docs", "ekran-goruntuleri", "uctan-uca");

const USER = process.env.E2E_USER ?? "analyst.core";
const PASSWORD = process.env.E2E_PASSWORD ?? "analyst";

/** Analistin IdP grubu ve tohumlanan verinin kapsam grubu. */
const IDP_GROUP = "/network/core";
const OWNER_GROUP = "golden";

const THEMES = ["light", "dark"] as const;

/**
 * Gezilecek ekranlar. Sıra anlamlı: tanıtımda da bu sırayla okunuyor.
 *
 * <p>
 * <b>Hazır işareti neden çıplak <c>h1</c> değil:</b> her rotanın
 * <c>loading.tsx</c>'i ekranın <b>aynı</b> başlığını basıyor
 * (<c>&lt;h1&gt;Kaynak envanteri&lt;/h1&gt;</c>), kök sınır da
 * <c>&lt;h1&gt;Yükleniyor&lt;/h1&gt;</c>. Ölçüldü: <c>h1</c> beklemek yükleniyor
 * iskeletine takılıp geçiyordu, yani görüntü bazen ekranın değil iskeletin
 * kendisi oluyordu ve koşum yine de yeşildi. Şimdi önce iskeletin gitmesi,
 * sonra ekranın <b>kendi</b> içeriği bekleniyor.
 * </p>
 *
 * <p>
 * <c>parserlar</c> bilerek "yetkisiz" adıyla duruyor: editör <c>author</c> rolü
 * istiyor ve analistte yok, dolayısıyla ekran yetki kartı basıyor. Görüntüyü
 * <c>parserlar</c> diye adlandırmak, olmayan bir editörü varmış gibi
 * gösterirdi.
 * </p>
 */
const SCREENS: ReadonlyArray<{ ad: string; yol: string; hazir: string }> = [
  { ad: "olaylar", yol: "/olaylar?limit=50", hazir: "table tbody tr" },
  // Başlığa DEĞİL içeriğe bakıyor: "hiçbir şey göremiyorsunuz" dalı da aynı
  // başlığı basıyor ve başlığa bakan bir işaret o hatayı ürün sanardı.
  { ad: "kaynaklar", yol: "/kaynaklar", hazir: "table tbody tr" },
  { ad: "parserlar-yetkisiz", yol: "/parserlar", hazir: "text=Parser yazma yetkiniz yok" },
  { ad: "katalog", yol: "/katalog", hazir: 'h1:text-is("Parser kataloğu")' },
  { ad: "alarmlar", yol: "/alarmlar", hazir: 'h1:text-is("Alarm kuralları")' },
  { ad: "degisiklikler", yol: "/degisiklikler", hazir: 'h1:text-is("Değişiklikler")' },
  { ad: "rca", yol: "/rca", hazir: 'h1:text-is("RCA raporları")' },
  { ad: "ana-sayfa", yol: "/", hazir: 'h1:text-is("Giriş yapıldı")' },
];

test.describe.configure({ mode: "serial" });

/**
 * Kapsam eşlemesi — API açıldıktan SONRA yazılıyor.
 *
 * <p>
 * Tablo EF göçüyle doğuyor ve göçler API açılışında uygulanıyor;
 * <c>globalSetup</c> ise <c>webServer</c>'dan önce koşuyor. Satırı oraya
 * koymak "tablo yok" hatası demekti. Eşleme olmadan analistin kapsamı
 * <b>boş</b> kalır — ve boş kapsam "her şey" değil "hiçbir şey" demek, yani
 * ekranlar sessizce boş çıkardı.
 * </p>
 */
/**
 * Yalnızca çıktı dizini.
 *
 * <p>
 * Kapsam eşlemesi ve envanter <b>burada değil</b>, <c>tests/e2e/prepare.ts</c>
 * içinde ve bu bir düzeltme: burada dururken API onları GÖREMİYORDU.
 * <c>Program.cs</c> kapsam eşlemesini açılışta bir kez okuyor, Playwright ise
 * <c>webServer</c>'ları testlerden önce başlatıyor — yani test satırı yazana
 * kadar API'nin önbelleği çoktan boş yüklenmiş oluyordu. Yerelde geçmesinin
 * sebebi satırın önceki koşumdan kalmasıydı; temiz bir veritabanında (CI)
 * ekranlar boş çıkardı.
 * </p>
 */
test.beforeAll(() => {
  mkdirSync(OUT, { recursive: true });
});

/**
 * Keycloak'ın kendi giriş sayfasından geçiyor.
 *
 * <p>
 * Parola uygulamaya hiç uğramıyor: <c>bizigo-ui</c> istemcisinde
 * <c>directAccessGrantsEnabled=false</c>, yani parola akışı Keycloak tarafında
 * da kapalı. Buradaki adımlar kullanıcının yaptığının aynısı.
 * </p>
 */
async function signIn(page: Page): Promise<void> {
  await page.locator("#username").fill(USER);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#kc-login").click();
  await page.waitForURL(/localhost:3000\/olaylar/, { timeout: 60_000 });
}

/**
 * <b>Kapsam boş mu?</b> Girişten hemen sonra, ekran çekilmeden önce.
 *
 * <p>
 * Bu tek iddia bütün bir hata sınıfını kapatıyor. Ürünün birden çok ekranı
 * (<c>olaylar</c>, <c>kaynaklar</c>) "hiçbir şey göremiyorsunuz" dalında da
 * ekranın <b>kendi başlığını</b> basıyor — yani hazır işareti olarak başlığa
 * bakan bir koşum, hata kartının görüntüsünü çekip ürün diye commitler ve
 * yeşil kalır. Bu depoda bunun adı konmuş: yeşilliği hiçbir şey ifade etmeyen
 * bekçi (§7).
 * </p>
 *
 * <p>
 * Seçicileri tek tek sağlamlaştırmak yerine kaynağı soruyoruz: kapsam boşsa
 * koşum <b>burada</b>, sebebi yazılı olarak duruyor.
 * </p>
 */
async function assertScopeIsNotEmpty(page: Page): Promise<void> {
  const me = await page.evaluate(async () => {
    const response = await fetch("/api/bff/auth/me");
    return { status: response.status, body: await response.text() };
  });

  expect(me.status, `/auth/me ${me.status} döndü: ${me.body.slice(0, 200)}`).toBe(200);

  const user = JSON.parse(me.body) as { sees_nothing?: boolean };

  expect(
    user.sees_nothing,
    `Kullanıcının kapsamı BOŞ (${USER}). Ekranlar hata kartı basacak ve bu koşum ` +
      "onları ürün diye çekerdi. Muhtemel sebep: `idp_group_mapping` satırı API " +
      "açılışından SONRA yazıldı — eşleme yalnızca açılışta okunuyor, tazelenmiyor. " +
      "Tohumlama `tests/e2e/prepare.ts` içinde ve Playwright'tan önce koşmalı.",
  ).toBe(false);
}

/**
 * Ekran oturduğunda çek.
 *
 * <p>
 * <c>networkidle</c> DEĞİL: Playwright'ın kendi belgeleri onu önermiyor ve
 * burada ölçüldü — koşum ilk iki görüntüden sonra ilerlemeden asıldı. Ağın
 * sessizleşmesi uygulamanın arka plan isteklerine bağlı ve o koşul hiç
 * gerçekleşmeyebiliyor; bekleyiş de testin tamamını zaman aşımına sürüklüyor.
 * </p>
 *
 * <p>
 * Yerine iki somut koşul: yazı tipleri yerleşmiş olsun (aksi hâlde ilk kare
 * yedek yazı tipiyle çıkıyor ve görüntü ürünü yanlış gösteriyor) ve bir çizim
 * karesi geçsin.
 * </p>
 */
/**
 * Ekran yüklendi mi.
 *
 * <p>
 * İki koşul, ve birincisi ürünün <b>kendi</b> işaretinden geliyor:
 * <c>LoadingState</c> sarmalayıcısına <c>aria-busy="true"</c> koyuyor. İskelet
 * DOM'dan düşmeden çekilen bir görüntü, ekranı değil bekleyişi gösterir.
 * </p>
 */
async function ready(page: Page, hazir: string): Promise<void> {
  await page
    .locator('[aria-busy="true"]')
    .first()
    .waitFor({ state: "detached", timeout: 30_000 });

  await page.locator(hazir).first().waitFor({ state: "visible", timeout: 30_000 });
}

async function capture(page: Page, ad: string, tema: string): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);

  // TAM SAYFA. Görünüm alanı 1440x900 ve log arama ekranında oraya yalnızca
  // filtre formu sığıyor — ekranın asıl işi olan sonuç tablosu katlamanın
  // altında kalıyordu. Yeşil bir koşum, ürünü eksik gösteren bir görüntü
  // üretiyordu; T28'in ilk turunda olanın aynısı.
  await page.screenshot({ path: join(OUT, `${ad}-${tema}.png`), fullPage: true });
}

for (const tema of THEMES) {
  test(`ekran görüntüleri — ${tema} tema`, async ({ browser }) => {
    const context = await browser.newContext({ colorScheme: tema });
    const page = await context.newPage();

    try {
      // Giriş sayfası — oturum açılmadan görülebilen tek ürün ekranı.
      await page.goto("/giris");
      await capture(page, "giris", tema);

      // Çerezi olmayan ziyaretçi middleware ile /api/auth/login'e, oradan
      // Keycloak'a düşüyor. Aradaki her adım gerçek.
      await page.goto("/olaylar");
      await page.waitForURL(/\/realms\/bizigo\/protocol\/openid-connect\/auth/, {
        timeout: 60_000,
      });
      await capture(page, "keycloak-giris", tema);

      await signIn(page);
      await assertScopeIsNotEmpty(page);

      for (const { ad, yol, hazir } of SCREENS) {
        await test.step(ad, async () => {
          await page.goto(yol);
          await ready(page, hazir);
          await capture(page, ad, tema);
        });
      }

      // Olay detayı — kimlik listeden alınıyor. Elle yazılmış bir kimlik,
      // tohumlama değiştiği gün sessizce 404'e düşerdi.
      await test.step("olay-detay", async () => {
        await page.goto("/olaylar");

        const firstRowLink = page.locator("table tbody tr a").first();

        await expect(
          firstRowLink,
          "Listede hiç satır yok: kapsam eşlemesi ya da tohumlama bozuk. " +
            `Beklenen: ${IDP_GROUP} → ${OWNER_GROUP}.`,
        ).toBeVisible();

        await firstRowLink.click();
        await page.waitForURL(/\/olaylar\/[^/]+$/, { timeout: 30_000 });
        await capture(page, "olay-detay", tema);
      });
    } finally {
      await context.close();
    }
  });
}
