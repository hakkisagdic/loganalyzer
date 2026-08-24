import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { defineConfig } from "@playwright/test";

/**
 * Uçtan uca koşum — **çalışan ürünün** ekran görüntüleri.
 *
 * <p>
 * `tests/screenshots/capture.test.tsx` bileşenleri doğrudan çiziyor ve bunu
 * bilerek yapıyor: Next sunucusu + Keycloak + API üç uzun ömürlü proses demek
 * ve protokolün §3'ü tam olarak o riski anlatıyor. Kanıtladığı şey **görünüm**,
 * kanıtlamadığı şey yönlendirme, kimlik akışı ve gerçek veri.
 * </p>
 *
 * <p>
 * Burası o boşluğu kapatıyor. Aynı işi iki kez yapmıyor: o paket bileşenin
 * dört durumunu (dolu/boş/yükleniyor/hata) çiziyor, bu paket <b>tek</b> durumu
 * — gerçek olanı — uçtan uca çekiyor.
 * </p>
 *
 * <h3>Ön koşul, ve neden sessizce atlamıyor</h3>
 *
 * <p>
 * Yığın ayakta değilse koşum <b>kırmızı yanıyor</b>, atlanmıyor
 * (<c>tests/e2e/prepare.ts</c>). §7: koşuma girip ortamı bulamayan bir test,
 * olmayan bir testten kötüdür — çünkü üstüne "bu soru sorulmuş" yanılsaması
 * bırakır.
 * </p>
 *
 * <pre>
 *   cd deploy && docker compose up -d --wait clickhouse postgres rustfs keycloak sidecar
 *   cd ui && npm run e2e            # başsız  (hazırlık + koşum)
 *   cd ui && npm run e2e:headed     # tarayıcı görünür
 *   cd ui && npx playwright test    # yalnız koşum — hazırlık ZATEN yapılmış olmalı
 * </pre>
 */

const UI_DIR = fileURLToPath(new URL(".", import.meta.url));
const REPO_DIR = fileURLToPath(new URL("..", import.meta.url));

/**
 * Üç port da SABİT ve üçü de bir yerden zorunlu:
 *
 * <ul>
 *   <li><b>3000</b> — realm dosyasındaki <c>redirectUris</c> birebir
 *       <c>http://localhost:3000/signin-oidc</c>. Port değişirse Keycloak
 *       dönüşü <c>invalid_redirect_uri</c> ile reddediyor.</li>
 *   <li><b>5080</b> — <c>deploy/.env.example</c>'daki <c>BIZIGO_ENDPOINT</c> ve
 *       <c>ui/.env.example</c>'daki <c>BIZIGO_API_URL</c> bu portu yazıyor.
 *       (Projenin <c>launchSettings.json</c>'ı 5058 diyor; o profil IDE içindir
 *       ve compose ile ayrışıyor — burada ortam değişkeniyle 5080'e sabitleniyor.)</li>
 *   <li><b>8180</b> — Keycloak'ın <c>KC_HOSTNAME</c>'i issuer'ı buraya
 *       sabitliyor; API'nin <c>Auth:Authority</c>'si ile birebir aynı olmak
 *       zorunda, yoksa her token issuer uyuşmazlığından 401 alıyor.</li>
 * </ul>
 */
export const UI_PORT = 3000;
export const API_PORT = 5080;
export const KEYCLOAK_ORIGIN = "http://localhost:8180";

/*
 * İçerik kökü MUTLAK olmak zorunda: göreli verilince ASP.NET Core onu çalışma
 * dizinine değil ikilinin kendi dizinine (`AppContext.BaseDirectory`) ekliyor ve
 * `<bin>/src/Bizigo.Api/bin/...` diye var olmayan bir yol çıkıyor. Ölçüldü —
 * koşum tam bu yüzden `DirectoryNotFoundException` ile düştü.
 */
const API_DIR = join(REPO_DIR, "src/Bizigo.Api/bin/Debug/net10.0");

export default defineConfig({
  testDir: "./tests/e2e",
  outputDir: "./.playwright",
  /*
   * `globalSetup` YOK ve bu bilinçli: Playwright `webServer`'ı globalSetup'tan
   * ÖNCE başlatıyor, yani derleme oraya konulduğunda sunucu henüz var olmayan
   * bir ikiliyi çalıştırmaya çalışıyor. Hazırlık `npm run e2e`'nin ilk adımında
   * (`tests/e2e/prepare.ts`) duruyor.
   */

  // Keycloak dönüşü + Next'in ilk çizimi + ClickHouse sorgusu tek testte
  // zincirleniyor; varsayılan 30 sn dar.
  timeout: 180_000,
  expect: { timeout: 20_000 },

  // Tek API ve tek Next sunucusu var. Paralel worker'lar aynı oturum deposunu
  // (BFF_SESSION_STORE=memory) paylaşır ve birbirinin çerezini geçersiz kılar.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,

  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],

  use: {
    baseURL: `http://localhost:${UI_PORT}`,

    // T28 ile aynı: ticket 1280–1920 aralığını istiyor, 1440 ortası. İki paketin
    // ayrı genişlik kullanması, aynı ekranın iki görüntüsünü kıyaslanamaz yapardı.
    viewport: { width: 1440, height: 900 },

    locale: "tr-TR",
    timezoneId: "Europe/Istanbul",

    // Varsayılan ikisi de SINIRSIZ. Bulunamayan bir seçici ya da dönmeyen bir
    // gezinme, sebebini söylemeyen bir asılmaya dönüşüyordu; sınır koyunca
    // hata seçiciyi ve URL'i yazıyor.
    actionTimeout: 30_000,
    navigationTimeout: 60_000,

    // Yeşil koşum hiçbir şey saklamıyor; kırmızıda iz ve video kalıyor.
    trace: "retain-on-failure",
    video: "retain-on-failure",

    launchOptions: {
      // `npm run e2e:headed` ile tarayıcı önde açılıyor. Yavaşlatma olmadan
      // adımlar gözle takip edilemeyecek kadar hızlı akıyor.
      slowMo: process.env.E2E_SLOWMO ? Number(process.env.E2E_SLOWMO) : 0,
    },
  },

  webServer: [
    {
      /*
       * Derlenmiş ikili, `dotnet run` DEĞİL.
       *
       * `dotnet run` içerik kökünü PROJE dizinine kuruyor; appsettings.json
       * ise çıktı dizininde (bin) duruyor. CLAUDE.md §12'nin anlattığı ayrım
       * bu: çalışma dizini depo kökü (katalog ve maske yolları oradan
       * çözülüyor), içerik kökü bin dizini.
       */
      command: `"${API_DIR}/Bizigo.Api" --contentRoot "${API_DIR}"`,
      cwd: REPO_DIR,
      port: API_PORT,
      // Yerelde zaten koşan bir API varsa ona bağlan; CI'da her koşum kendi
      // prosesini açıyor, çünkü orada "zaten koşuyor" hâli bir artık demek.
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      stdout: "pipe",
      stderr: "pipe",
      env: {
        DOTNET_ROOT: `${process.env.HOME}/.dotnet`,
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
        // EF her sorguyu Information seviyesinde basıyor ve koşum logunda
        // Playwright'ın kendi çıktısı SQL'in altında kayboluyordu.
        "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      },
    },
    {
      // `next start` — `next dev` değil. Dev kipi köşede bir geliştirme
      // göstergesi çiziyor ve o gösterge her ekran görüntüsüne giriyor.
      command: "npm run start",
      cwd: UI_DIR,
      port: UI_PORT,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      stdout: "pipe",
      stderr: "pipe",
      env: {
        KEYCLOAK_ISSUER: `${KEYCLOAK_ORIGIN}/realms/bizigo`,
        KEYCLOAK_CLIENT_ID: "bizigo-ui",
        // Realm dosyasındaki geliştirme değeri; üretimde gizli yöneticiden gelir.
        KEYCLOAK_CLIENT_SECRET: "bizigo-ui-dev-secret",
        BFF_PUBLIC_URL: `http://localhost:${UI_PORT}`,
        BIZIGO_API_URL: `http://localhost:${API_PORT}`,
        BFF_COOKIE_NAME: "bizigo.sid",
        BFF_SESSION_TTL_SECONDS: "28800",
        // Tek süreç, tek worker: bellek içi depo yeterli ve Redis'i koşulun
        // ön koşuluna eklemek gereksiz bir bağımlılık olurdu.
        BFF_SESSION_STORE: "memory",
      },
    },
  ],
});
