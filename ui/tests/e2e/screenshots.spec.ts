import { execFileSync } from "node:child_process";
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
  { ad: "kaynaklar", yol: "/kaynaklar", hazir: 'h1:text-is("Kaynak envanteri")' },
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
test.beforeAll(async () => {
  mkdirSync(OUT, { recursive: true });

  psql(`INSERT INTO bizigo.idp_group_mapping (idp_group, owner_group, note)
        VALUES ('${IDP_GROUP}', '${OWNER_GROUP}', 'uctan uca ekran goruntusu kosumu')
        ON CONFLICT (idp_group) DO UPDATE SET owner_group = EXCLUDED.owner_group;`);

  await seedInventory();
});

/** Kontrol düzlemine tek ifade — compose'un içindeki `psql` üzerinden. */
function psql(sql: string): void {
  execFileSync(
    "docker",
    [
      "compose", "-f", "deploy/docker-compose.yml", "exec", "-T", "postgres",
      "psql", "-U", "bizigo", "-d", "bizigo", "-v", "ON_ERROR_STOP=1", "-c", sql,
    ],
    { cwd: REPO, stdio: ["ignore", "ignore", "inherit"] },
  );
}

/** ClickHouse'a tek sorgu; satırlar TSV. */
async function clickhouse(sql: string): Promise<string[][]> {
  const response = await fetch("http://localhost:8123/", {
    method: "POST",
    headers: {
      "X-ClickHouse-User": "bizigo",
      "X-ClickHouse-Key": "bizigo",
      "X-ClickHouse-Database": "bizigo",
    },
    body: sql,
  });

  if (!response.ok) {
    throw new Error(`ClickHouse sorguyu reddetti: ${await response.text()}`);
  }

  return (await response.text())
    .trim()
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => line.split("\t"));
}

/**
 * Kaynak envanteri.
 *
 * <p>
 * <b>Liste ClickHouse'tan okunuyor, buraya yazılmıyor.</b> Parser dizinlerini
 * ikinci kez saymak, tohumlayıcı değiştiği gün sessizce ayrışan bir kopya
 * olurdu (§9) — ve ayrıştığı yer, envanterin olayları göstermediği bir ekran
 * olurdu.
 * </p>
 *
 * <p>
 * <b><c>parser_id</c> BASKIN parser'a bağlanıyor ve o da ölçülüyor.</b>
 * Envanterdeki bağ tek parser tutuyor (dispatcher kademe 1), oysa altın kümede
 * her kaynak iki parser tipi basıyor. Bağlamanın zararsız olduğu <b>koda
 * bakılarak</b> doğrulandı: bağlı parser tutmazsa <c>Dispatcher</c> kademe 2'ye
 * düşüyor, satır doğru parser'a gidiyor ve <c>RecordBoundMiss</c> sayacı bunu
 * ayrı tutuyor — yani hiçbir satır kaybolmuyor ya da yanlış atfedilmiyor.
 * </p>
 *
 * <p>
 * Hangi parser'ın baskın olduğu <b>veriden</b> geliyor (<c>argMax</c>), buraya
 * yazılmıyor. Elle yazılmış bir eşleme, örnek korpusu değiştiği gün sessizce
 * yanlış parser'a bağlardı ve bağlama oranı sebebi görünmeden düşerdi.
 * Ölçülen pay: baskın parser'lar satırların <b>%93,5</b>'ini taşıyor.
 * </p>
 */
async function seedInventory(): Promise<void> {
  const rows = await clickhouse(
    `SELECT source_id,
            any(vendor),
            any(product),
            argMax(parser_id, satir) AS baskin_parser
     FROM (
       SELECT source_id, vendor, product, parser_id, count() AS satir
       FROM events
       WHERE owner_group = '${OWNER_GROUP}' AND parser_id != ''
       GROUP BY source_id, vendor, product, parser_id
     )
     GROUP BY source_id ORDER BY source_id FORMAT TSV`,
  );

  if (rows.length === 0) {
    throw new Error(
      `ClickHouse'ta '${OWNER_GROUP}' grubunda hiç kaynak yok. ` +
        "Önce `npm run e2e:prepare` (ya da `bizigo seed golden`) koşturun.",
    );
  }

  // Değerler kendi ClickHouse'umuzdan geliyor ama tırnak taşıyan bir değer
  // ifadeyi bozardı ve bozulduğu yer sessiz olurdu: açıkça reddediliyor.
  for (const row of rows) {
    for (const value of row) {
      if (value.includes("'")) {
        throw new Error(`Envanter değeri tırnak taşıyor, ifade kurulamaz: ${value}`);
      }
    }
  }

  const values = rows
    .map(
      ([sourceId, vendor, product, parserId]) =>
        `('${sourceId}', '${sourceId}', '${OWNER_GROUP}', '${vendor}', '${product}', ` +
        `'${parserId}', 'auto', 'golden', true, now(), now())`,
    )
    .join(",\n         ");

  psql(
    `INSERT INTO bizigo.sources
       (source_id, hostname, owner_group, vendor, product,
        parser_id, encoding, source_class, enabled, created_at, updated_at)
     VALUES ${values}
     ON CONFLICT (source_id) DO UPDATE SET
       owner_group = EXCLUDED.owner_group,
       vendor = EXCLUDED.vendor,
       product = EXCLUDED.product,
       parser_id = EXCLUDED.parser_id,
       updated_at = now();`,
  );
}

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
