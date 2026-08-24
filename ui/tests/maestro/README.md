# Maestro — uçtan uca akışlar (web)

YAML ile yazılmış, derleme gerektirmeyen E2E akışları. Aynı komutlar
(`tapOn`, `inputText`, `assertVisible`) web, Android ve iOS'ta birebir
çalışıyor — bugün web'de koşuyorlar, yarın bir mobil eşlikçi uygulama
gelirse akışlar taşınıyor.

## Neden Playwright'ın yanında

`ui/tests/e2e/` altındaki Playwright paketi **ekran görüntüsü** üretiyor:
ürünün nasıl göründüğünü kaydediyor. Buradaki akışlar **davranışı** sınıyor:
kimlik zinciri kuruluyor mu, yetki kapısı kapalı mı, ekran veriyle mi
çiziliyor. İkisi ayrı sorular ve ayrı dizinlerde duruyorlar — tek dizinde
yaşamaları, `CiCoverageTests`'i bir kez kör eden şeyin ta kendisiydi.

## Koşturmak

Yığın ayakta olmalı: Keycloak, `Bizigo.Api`, ve `next dev`.

```bash
maestro test ui/tests/maestro \
  -e MAESTRO_APP_URL=http://localhost:3000/olaylar \
  -e MAESTRO_USER=analyst.core \
  -e MAESTRO_PASSWORD=analyst
```

Tek akış:

```bash
maestro test ui/tests/maestro/03-yetki-kapisi.yaml -e MAESTRO_APP_URL=http://localhost:3000/olaylar -e MAESTRO_USER=analyst.core -e MAESTRO_PASSWORD=analyst
```

Adım adım izlemek ve seçici denemek için:

```bash
maestro studio
```

## `analyst.core`, `admin` değil

`admin` rolü kapsam filtresinden muaf (`AccessScope.System`). Onunla giren bir
akış, ürünün en pahalı hata sınıfının tam ortasını atlar: kapsamın gerçekten
uygulandığını hiçbir şey göstermez. `analyst.core` `/network/core` claim'iyle
geliyor ve gördüğü her satır aynı zamanda kapsam yolunun kanıtı.

Aynı gerekçe `ui/tests/e2e/screenshots.spec.ts`'te de yazılı — iki paket aynı
kullanıcıyı aynı sebeple kullanıyor.

## İskelet tuzağı

Her rotanın `loading.tsx`'i ekranın **aynı başlığını** basıyor:

```tsx
// ui/src/app/kaynaklar/loading.tsx
<h1>Kaynak envanteri</h1>
<LoadingState label="Envanter yükleniyor" rows={6} />
```

Yani `assertVisible: "Kaynak envanteri"` **iskelete de uyuyor**. Yalnızca onu
beklemek, akışın ekranın değil iskeletin üstünde yeşil yanması demek — ve
yeşilliğin hiçbir şey ifade etmediği bir koşum, kırmızı bir koşumdan kötü.

Bu yüzden ekran akışları önce iskeletin **gitmesini** bekliyor:

```yaml
- extendedWaitUntil:
    notVisible: "Envanter yükleniyor"
    timeout: 60000
- assertVisible: "Kaynak envanteri"
```

Aynı tuzak Playwright tarafında ölçüldü (bkz. `ui/tests/e2e/screenshots.spec.ts`
içindeki `hazir` alanı).

## ⚠️ Bu paket şu an CI'da KOŞMUYOR

Açıkça yazılıyor çünkü sessiz olması tehlikeli olurdu.

`CiCoverageTests` depodaki test köklerini işaret dosyalarından buluyor
(`pytest.ini`/`conftest.py`, `vitest.config.*`, `playwright.config.*`, Test
SDK'lı `*.csproj`) ve her birinin `ci.yml`'da onu koşturan bir adımı olduğunu
sınıyor. **Maestro bu ailelerden hiçbiri değil**, dolayısıyla bu paket bekçiye
şu an *görünmüyor* — bekçi kırmızı yanmıyor, sadece bakmıyor.

Kapatılması gereken iki bağ:

1. `CiCoverageTests`'e `Families.Maestro` eklenmesi. İşaret: **`maestro` adlı
   bir dizinin içindeki `config.yaml`** — bu dosya zaten Maestro'nun gerçek
   çalışma alanı yapılandırması, uydurma bir bekçi yemi değil.
   Koşucu: `maestro test`.
2. `ci.yml`'daki mevcut `e2e` işine bir `maestro test ui/tests/maestro` adımı.
   Ayrı bir iş değil: `e2e` işi compose yığınını zaten kaldırıyor ve ikinci bir
   tam yığın işi CI süresini boşuna ikiye katlar. Adım `npm run e2e`'den
   **sonra**, `docker compose down -v` temizliğinden **önce** gelmeli.

Neden şimdi yapılmadı: ikisi de başka bir dalda değişiyor ve o dal henüz main'de
değil. Ayrıca Maestro'nun web sürücüsünün ubuntu runner'da kendi Chromium'unu
indirmesi **ölçülmedi** — ölçülmeden eklenen bir adım, `CLAUDE.md` §7'nin
yasakladığı üçüncü hâli üretir: *koşuma giriyor ama ortam hazır değil*.

## Ölçülenler ve ölçülmeyenler

Bu bölüm ayrı duruyor çünkü aradaki fark önemli: *"aradım, yok"* ile
*"aramadım"* farklı şeyler.

### ⛔ Uçtan uca yeşil koşum YOK — ve sebebi Maestro'da

Akışlar yazıldı, yığın (Keycloak + API + ClickHouse + Postgres + Redis +
sidecar + üretim derlemesi UI) gerçekten ayağa kaldırıldı, ve akış **yine
koşmadı**. Maestro'nun web sürücüsü bu ürünün sayfasında `assertVisible`
adımında **sonsuza kadar bekliyor**.

**En tehlikeli yanı bu:** hata basmıyor, zaman aşımına da düşmüyor.
`extendedWaitUntil: timeout: 90000` verilmiş olmasına rağmen 90 saniye hiç
dolmuyor — 43 dakika ölçüldü, java süreci %0 CPU, tarayıcı süreci ortada yok.
Bir CI adımı olarak eklenseydi iş kırmızı yanmaz, **işin zaman aşımına kadar
asılı kalırdı**.

### Dokuz hipotez, dokuz deney

Sebep aranırken her adımda tek değişken değiştirildi:

| # | Hipotez | Deney | Sonuç |
| --- | --- | --- | --- |
| 1 | Makine yükü (swap %93) | aynı anda `example.com` | ✅ 2 dk — **makine değil** |
| 2 | Keycloak'a yönlendirme | yönlendirmesiz `/giris` | ⛔ takıldı |
| 3 | `runFlow` / alt akış | `runFlow`suz tek dosya | ⛔ takıldı |
| 4 | `localhost` / http / port | düz statik http sunucu | ✅ — **o değil** |
| 5 | Next dev, HMR websocket | `next build` + `next start` | ⛔ takıldı |
| 6 | Telemetri entegrasyonu | `TELEMETRY_ENABLED=false` | ⛔ takıldı |
| 7 | Aranan metin sayfada yok | curl ile sayfa metni | metin **var** |
| 8 | `X-Frame-Options: DENY` | statik sayfa + aynı başlık | ✅ — **o değil** |
| 9 | Next'in sunma biçimi | Next'in HTML'i statik sunucudan | ⛔ takıldı |

Dokuzuncusu belirleyici: **aynı byte'lar, Next devrede değil, yine takılıyor.**
Yani sebep sunucu, protokol ya da başlık değil — **sayfanın içeriği**.

Onuncu adım içeriği böldü: HTML'den **13 script etiketinin tamamı** çıkarıldı
(9.444 → 1.107 bayt) ve **yine takıldı**. JavaScript de değil.

### Kalan şüpheliler — bir sonraki kişi buradan devam etsin

Asılı kalan en küçük hâl `asili-kalan-sayfa.html` olarak yanında duruyor.
1.107 bayt, script yok, ve içinde üç şüpheli var:

1. **React Suspense sınır işaretçileri** — `<!--$-->` / `<!--/$-->`. Çözülmemiş
   bir sınırı "içerik hâlâ yükleniyor" diye okuyan bir sürücü sonsuza kadar
   bekler.
2. **404'e giden iki `<link rel="stylesheet">`** — `/_next/static/css/…`
3. **404'e giden `<link rel="preload" as="script">`**

Sıradaki deney: bu dosyadan sırayla (a) yorum işaretçilerini, (b) `link`
etiketlerini çıkarıp aynı probe'u koşturmak. Her biri tek satırlık bir
düzenleme ve dakikalar sürer.

### Ölçüldü — çalışan kısımlar

- Maestro 2.3.0 CLI, Java 26 ile koşuyor.
- Web sürücüsü Chromium'u başlatıyor ve **dış siteye karşı yeşil koşuyor**.
- Düz yerel http sayfasına karşı da yeşil koşuyor (`X-Frame-Options: DENY`
  başlığıyla bile).
- `launchApp` eklendikten sonra gezinme gerçekleşiyor — Next günlüğünde
  `GET /api/auth/login … 307` görüldü, yani akış Keycloak'a kadar gidiyor.

### Ölçüldü — çalışmayan ve SESSİZ kırılan dört şey

- **`id:` seçicisi web'de bulmuyor.** Keycloak sayfasında `id="kc-login"`,
  `id="username"`, `id="password"` HTML'de duruyor (curl ile doğrulandı) ama
  Maestro 90 sn boyunca bulamadı. Sürücü erişilebilirlik metnine bakıyor.
- **`${DEĞİŞKEN}` yalnızca komut alanlarında yorumlanıyor.** `url:` başlığında
  ve `openLink:` içinde birebir metin olarak kalıyor; tarayıcı o metne gitmeye
  çalışıp boş sayfada bekliyor. Adresler bu yüzden **sabit**.
- **`launchApp` zorunlu ve yokluğu sessiz.** `url:` başlığı hedefi yalnızca
  bildiriyor, tarayıcıyı oraya götürmüyor. Onsuz: 12 dakika, chromium %0 CPU,
  uygulamaya tek istek gitmedi, hiçbir yerde "gezinmedim" yazmadı.
- **Zaman aşımı ateşlemiyor.** Yukarıdaki asılı kalma.

## Sınırlar

- Maestro'nun web desteği **Beta** ve yalnızca Chromium.
- Java 17+ gerekiyor (bu makinede 26 ile ölçüldü).
- Akışlar gerçek Keycloak'a giriyor; sahte oturum enjekte edilmiyor. Yığın
  ayakta değilse akış giriş formunu bulamayıp zaman aşımına düşer — sessizce
  geçmez.
- Chromium başlatmak yüklü makinede pahalı. Koşumdan önce:
  `~/.claude/scripts/machine-resources.sh check`
