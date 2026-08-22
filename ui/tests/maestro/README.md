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

**Ölçüldü — çalışıyor:**

- Maestro 2.3.0 CLI, Java 26 ile koşuyor (`maestro -v`).
- Web sürücüsü kendi Chromium'unu başlatıyor ve gezinme yapıyor: bir dış
  siteye karşı `launchApp` + `assertVisible` yeşil koştu.
- `url:` başlığındaki adres gerçekten açılıyor — Next dev sunucusunun
  günlüğünde `/api/auth/login → 307` zinciri görüldü, yani akış Keycloak'a
  kadar gidiyor.

**Ölçüldü — çalışmıyor, ve ikisi de SESSİZ kırılıyor:**

- **`id:` seçicisi web'de bulmuyor.** Keycloak sayfasında `id="kc-login"`,
  `id="username"`, `id="password"` HTML'de duruyor (curl ile doğrulandı) ama
  Maestro 90 sn boyunca bulamadı. Sürücü erişilebilirlik metnine bakıyor, ham
  `id` niteliğine değil. Akışlar bu yüzden metin tabanlı seçici kullanıyor.
  (Playwright tarafı `page.locator("#username")` ile sorunsuz çalışıyor — bu
  yalnızca Maestro sürücüsüne özgü. İki paket artık farklı seçici stratejisi
  kullanıyor ve bunun bilinçli olması için burada yazıyor.)
- **`${DEĞİŞKEN}` yalnızca komut alanlarında yorumlanıyor.** `url:` başlığında
  ve `openLink:` içinde birebir metin olarak kalıyor; tarayıcı o metne gitmeye
  çalışıp boş sayfada zaman aşımına düşüyor. Bu yüzden adresler akışlarda
  **sabit** yazılı. Başka bir adrese koşturmak için `http://localhost:3000`
  metnini değiştirin — değişkene çıkarmayın, çalışmıyor.

**ÖLÇÜLMEDİ — akışların uçtan uca yeşil koşumu.**

Koşum başlatıldı, Chromium açıldı ve Keycloak'a gitti, ama makine o sırada
**%88 swap**'teydi ve akış giriş formunda ilerlemedi. `CLAUDE.md` §6 yüklü
makinede alınan ölçümün bağlayıcı olmadığını söylüyor; §2 de koşturulamayan
bir testi yeşil göstermeyi yasaklıyor. Dolayısıyla burada yazan tek şey şu:
**akışlar yazıldı, koşumları doğrulanmadı.** Sessiz makinede bir kez
koşturulup bu bölüm güncellenmeli.

## Sınırlar

- Maestro'nun web desteği **Beta** ve yalnızca Chromium.
- Java 17+ gerekiyor (bu makinede 26 ile ölçüldü).
- Akışlar gerçek Keycloak'a giriyor; sahte oturum enjekte edilmiyor. Yığın
  ayakta değilse akış giriş formunu bulamayıp zaman aşımına düşer — sessizce
  geçmez.
- Chromium başlatmak yüklü makinede pahalı. Koşumdan önce:
  `~/.claude/scripts/machine-resources.sh check`
