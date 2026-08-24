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
maestro test --platform web ui/tests/maestro
```

Tek akış:

```bash
maestro test --platform web ui/tests/maestro/03-yetki-kapisi.yaml
```

Görünür pencere istemiyorsan `--headless` ekle.

Adım adım izlemek ve seçici denemek için:

```bash
maestro studio
```

### `--platform web` zorunlu ve yokluğu SESSİZ

Ölçüldü: bayrak olmadan `maestro test` bu pakette **tek satır çıktı bile
üretmiyor** — hata yok, akış adı yok, özet yok. Cihaz seçimi yapamadığı için
sessizce oturuyor. `launchApp` ile birlikte **ikisi de** gerekli: `--platform
web` sürücüyü seçiyor, `launchApp` tarayıcıyı `url:` hedefine götürüyor.
Birinin eksikliği diğerinin belirtisiyle karışıyor ve ikisi de sessiz.

### Kimlik bilgileri `-e` ile geçilmiyor

Kullanıcı/parola her akışın kendi `env:` bloğunda (`KULLANICI`, `PAROLA`) ve
adresler `url:` başlığında **sabit** yazılı — sebebi aşağıdaki `${DEĞİŞKEN}`
notu. Bu README daha önce var olmayan `MAESTRO_APP_URL` / `MAESTRO_USER` /
`MAESTRO_PASSWORD` değişkenlerini geçen bir komut gösteriyordu; akış
dosyalarında o adlardan hiçbiri geçmiyor (`grep MAESTRO_ *.yaml altakimlar/*.yaml`
→ eşleşme yok). Komut düzeltildi.

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

### Karar: bu paket CI'ya EKLENMEMELİ (şimdilik)

Bu bir "henüz sıra gelmedi" değil, **ölçülmüş bir karar**. Gerekçe tek
cümleyle: *bu paket kırılırsa CI kırmızı yanmıyor, asılı kalıyor.*

Aşağıdaki "sürünme" bölümünde ölçülen davranış şu: akıştaki
`extendedWaitUntil: timeout: 90000` bu koşulda **ateşlemiyor**. 90 saniyeyi
duvar saatiyle çoktan geçmiş bir koşum hâlâ ilerlemeye çalışıyor. Bir CI adımı
olarak eklenseydi sonuç:

- iş **kırmızı yanmaz** — hata yok, başarısızlık yok, çıkış kodu yok,
- runner **iş zaman aşımına kadar tutulur** (dakikalarca değil, saatlerce),
- ve bu, `CLAUDE.md` §7'nin en pahalı sınıfı: sessiz yanlış davranış.

`CLAUDE.md` §7 zaten bunun adını koyuyor — *"koşuma giriyor ama ortam hazır
değil"* üçüncü hâli. Runner'ın Maestro'nun Chromium'unu indirip indiremediği de
**ölçülmedi**; üstüne bir de zaman aşımının ateşlemediği bir adım eklemek iki
bilinmeyeni birden CI'ya taşımak olur.

**Bekçi bağı da bu yüzden kurulmadı.** `CiCoverageTests`'e `Families.Maestro`
eklemek (işaret: `maestro` adlı bir dizinin içindeki `config.yaml`) tek başına
anlamlı değil: bekçi bir test kökü görürse onu **koşturan bir `ci.yml` adımı**
ister, adım yoksa kırmızı yanar. Yani ikisi bir çift — ve çiftin ikinci
yarısını yukarıdaki gerekçeyle eklemiyoruz. Sonuç: paket bekçiye görünmüyor ve
**bu README onun tek kaydı**. Bilerek böyle.

Kapatılabilmesi için önce gereken şey ölçüm, kod değil:

1. **Sessiz makinede** (`machine-resources.sh check` yeşil, swap düşük) tam bir
   akışın uçtan uca yeşil koştuğu görülmeli.
2. Aynı sessiz makinede bir akış **kasten kırılıp** koşumun gerçekten kırmızı
   yandığı ve **makul sürede** bittiği ölçülmeli (`CLAUDE.md` §6: bekçinin
   kırmızı yanabildiğini ölç, sonra geri al).
3. Ubuntu runner'da Chromium indirmesinin çalıştığı görülmeli.

Üçü de yeşilse bağlar şöyle kurulur: `CiCoverageTests`'e `Families.Maestro`
(koşucu `maestro test`), ve `ci.yml`'daki mevcut `e2e` işine bir
`maestro test --platform web ui/tests/maestro` adımı — ayrı bir iş değil, çünkü
`e2e` işi compose yığınını zaten kaldırıyor. Adım `npm run e2e`'den **sonra**,
`docker compose down -v` temizliğinden **önce**. Adıma mutlaka bir dış
`timeout` sarılmalı; akışın kendi zaman aşımına güvenilemeyeceği ölçüldü.

## Ölçülenler ve ölçülmeyenler

Bu bölüm ayrı duruyor çünkü aradaki fark önemli: *"aradım, yok"* ile
*"aramadım"* farklı şeyler.

### ⛔ Uçtan uca yeşil koşum YOK — sebep **ortam**, sayfa değil

Akışlar yazıldı, yığın (Keycloak + API + ClickHouse + Postgres + Redis +
sidecar + üretim derlemesi UI) gerçekten ayağa kaldırıldı, ve akış **yine
tamamlanmadı**.

Uzun süre bunun sebebinin *bu ürünün sayfa içeriği* olduğu sanıldı. **O sonuç
yanlıştı ve ölçülerek çürütüldü.** Doğrusu şu:

> **Kilitlenme yok, sürünme var.** %93 swap'teki bu makinede JVM yaklaşık
> **%14 çalışma oranıyla** ilerliyor. Duvar saatiyle dakikalar geçiyor,
> işlemci saniyeleri geçiyor.

Çürüten ölçümler:

| Deney | Sonuç |
| --- | --- |
| **149 baytlık** sayfa — script yok, link yok, yorum yok, tek bir `<h1>` | takıldı |
| `https://example.com` — "çalışıyor" diye raporlanmış kontrolün ta kendisi | takıldı |
| `jstack` t = 20 / 55 / 95 sn | **ÜÇ FARKLI yığın**; t=95'te toplam **13 sn CPU** |
| t=95'te nerede duruyor | hâlâ `picocli … shouldDetectTerminalSize` — **komut satırı ayrıştırma** |
| `java -version` (bu makinede normalde ~0,1 sn) | **3–6 saniye** |
| `python3 -c pass` | **0,9 saniye** |
| 600 sn sınır + `--platform web --headless` | `Launch app … COMPLETED`, chromium sayfayı istedi, sonra `Assert…` bitmedi |

Belirleyici olan `jstack`: üç farklı yığın, yani süreç **bloke değil,
ilerliyor** — sadece 95 saniyede 13 saniyelik iş çıkarabiliyor. Ve t=95'te
hâlâ argüman ayrıştırmada olması, sayfayla ilgili hiçbir şeyin daha
başlamadığını gösteriyor. Sayfa içeriği hipotezinin ölçebileceği bir şey yok:
sürücü sayfaya varmadan sürünüyor.

`java -version` ile `python3 -c pass` arasındaki fark bunun **ortamsal** ve
**JVM'e özgü** olduğunu söylüyor: aynı makinede Python 0,9 sn'de dönerken JVM
3–6 sn harcıyor.

### Dokuz deneylik tablo — **geçersiz kılındı**, kayıt olarak duruyor

Aşağıdaki tablo silinmedi çünkü hâlâ değerli: **elenen şeyleri** gösteriyor ve
yanlış sonuca nasıl varıldığını anlatıyor. Ama sonucu artık geçerli değil.

> ⚠️ **Bu tablonun dokuz satırının hepsi aynı ortamsal sürünmeyi ölçüyordu.**
> "⛔ takıldı" satırları bir sayfa özelliğini değil, koşumun sürünmesini
> kaydetti; "✅" satırları da bir şeyin çalıştığını değil, o koşumun sürünmeyi
> tesadüfen atlattığını. Tek değişkenin gerçekten tek olduğu varsayıldı; ikinci
> ve görünmez bir değişken (makine yükü) her satıra ortak giriyordu.

| # | Hipotez | Deney | O günkü sonuç |
| --- | --- | --- | --- |
| 1 | Makine yükü (swap %93) | aynı anda `example.com` | ✅ 2 dk — "makine değil" |
| 2 | Keycloak'a yönlendirme | yönlendirmesiz `/giris` | ⛔ takıldı |
| 3 | `runFlow` / alt akış | `runFlow`suz tek dosya | ⛔ takıldı |
| 4 | `localhost` / http / port | düz statik http sunucu | ✅ — "o değil" |
| 5 | Next dev, HMR websocket | `next build` + `next start` | ⛔ takıldı |
| 6 | Telemetri entegrasyonu | `TELEMETRY_ENABLED=false` | ⛔ takıldı |
| 7 | Aranan metin sayfada yok | curl ile sayfa metni | metin **var** |
| 8 | `X-Frame-Options: DENY` | statik sayfa + aynı başlık | ✅ — "o değil" |
| 9 | Next'in sunma biçimi | Next'in HTML'i statik sunucudan | ⛔ takıldı |

**1 numaralı satır kanıtın kendisiydi ve ters okundu.** `example.com` için
**iki dakika patolojiktir** — o satır "makine değil" demiyordu, "makine" diyordu.
Aynı kontrol daha sonra tamamen takıldı. Bir sonucu ✅ yapan şey eşiğin altında
kalması değil, **beklenen büyüklükte** olmasıdır; iki dakikalık bir
`example.com` koşumu hiçbir eşikte yeşil sayılmamalıydı.

Buradaki ders `CLAUDE.md` §6'nın satırı: **ölçüm sessiz makine ister.** Deneyler
doğru tasarlanmıştı, dürüst raporlanmıştı, tek değişkenle daraltılmıştı —
eksik olan disiplin değil, sessizlikti.

### `asili-kalan-sayfa.html` artık kanıt DEĞİL

Dosya (1.117 bayt) yanında duruyor ama **hiçbir hipotezi desteklemiyor**.
Ne olduğu: bu ürünün giriş sayfasının, script etiketleri çıkarılmış üretim
HTML'i. Neden üretilmişti: "sayfa içeriği takılmaya sebep oluyor" hipotezini
daraltmak için. Neden artık kanıt değil: **149 baytlık, hiçbir şüpheli
içermeyen bir sayfa da takıldı** — yani dosyanın içindeki şüpheliler (React
Suspense işaretçileri `<!--$-->`, 404'e giden `<link>`'ler) hiçbir zaman
sınanmadı, sınanamazdı da.

Şimdilik **tutuluyor**, iki sebeple: (a) yanlış sonuca nasıl varıldığının
kaydı, (b) sessiz makinede yeniden ölçüm yapılırken hazır bir küçük hedef.
**Sessiz makinede ölçüm tamamlandığında silinmeli** — o gün ya sayfa içeriğinin
hiç rol oynamadığı görülür (dosyanın işi biter) ya da gerçek bir şüpheli
bulunur (o zaman dosya değil, bulgu yazılır). Bir dosyanın "belki lazım olur"
diye durması, bu depoda çürütülmüş bir hipotezi canlı tutar.

### En uzak gidilen nokta — ve neden hâlâ "çalışıyor" DEMİYOR

10 dakikalık (600 sn) bir sınırla, `--platform web --headless` ile koşulduğunda
ölçülen:

- `Launch app … **COMPLETED**` — yani `launchApp` adımı gerçekten bitti,
- chromium sayfayı **istedi** (uygulamaya istek gitti),
- sonraki `Assert…` adımı **bitmedi**; 600 sn doldu.

**Bu, "Maestro burada çalışıyor" demek DEĞİLDİR.** Tek bir akış uçtan uca yeşil
koşmadı; en iyi hâl, ilk adımın tamamlandığının görülmesi. Sürünme oranı
(~%14) düşünüldüğünde `Assert` adımının hiç mi ilerlemediği yoksa yalnızca
yetişemediği mi **ayrıştırılamıyor** — ikisi bu makinede aynı görünüyor.

**Sessiz makinede yeniden ölçülmesi gerekiyor.** Ölçülene kadar bu paketin
durumu şudur: *yazıldı, koşturulmadı.*

### Ölçüldü — ama sürünen makinede; sessiz makinede DOĞRULANMALI

Aşağıdakiler doğru olabilir; ancak hepsi %93 swap'teki makinede kaydedildi ve
bir kısmının "bulamadı" dediği şey aslında "yetişemedi" olabilir. Ayrımı
yazıyorum çünkü *"aradım, yok"* ile *"aramadım"* kadar, *"ölçtüm"* ile
*"gürültülü ölçtüm"* de farklı şeyler.

- **`id:` seçicisi web'de bulmuyor.** Keycloak sayfasında `id="kc-login"`,
  `id="username"`, `id="password"` HTML'de duruyor (curl ile doğrulandı) ama
  Maestro 90 sn boyunca bulamadı. Muhtemel açıklama: sürücü erişilebilirlik
  metnine bakıyor, ham `id` niteliğine değil (Maestro'nun kendi rehberi de
  görünür metni önceliyor). **Ama 90 sn duvar saati bu makinede ~13 sn'lik iş
  demek** — sessiz makinede yeniden sınanmalı. Akışlar yine de metin tabanlı
  seçici kullanıyor; bu, ölçümden bağımsız olarak Maestro'nun önerdiği yol.
- **`${DEĞİŞKEN}` yalnızca komut alanlarında yorumlanıyor.** `url:` başlığında
  ve `openLink:` içinde birebir metin olarak kalıyor. Bu bulgu **süreden
  bağımsız**: değişken yorumlanmıyorsa çıktıda literal metin görünür, beklemekle
  ilgisi yok. Adresler bu yüzden **sabit**.
- **`launchApp` zorunlu ve yokluğu sessiz.** `url:` başlığı hedefi yalnızca
  bildiriyor, tarayıcıyı oraya götürmüyor. Onsuz uygulamaya **tek istek
  gitmedi** — bu da süreden bağımsız bir kanıt: ne kadar beklenirse beklensin
  gitmeyen bir istek yavaşlıktan olmaz. `--platform web` ile birlikte ikisi de
  gerekli.
- **Zaman aşımı ateşlemedi.** `extendedWaitUntil: timeout: 90000` verilmiş
  koşumlar 90 saniyeyi duvar saatiyle geçtiği hâlde kırmızı yanmadı. **Sebebi
  ayrıştırılamadı**: Maestro'nun zaman aşımı gerçekten mi ateşlemiyor, yoksa
  zaman aşımını ölçen kod da mı sürünüyor? Sessiz makinede ölçülmeli. CI kararı
  açısından fark etmiyor — **gözlenen davranış** kırmızı yanmadan asılı
  kalmaktır, ve bir CI adımı gözlenen davranışa göre değerlendirilir.

### Ölçüldü — ortamla ilgili, sayfayla değil

- Maestro 2.3.0 CLI, Java 26 ile koşuyor.
- Web sürücüsü Chromium'u başlatabiliyor.
- `java -version` bu makinede 3–6 sn (normalde ~0,1 sn); `python3 -c pass`
  0,9 sn. Fark JVM'e özgü ve ortamsal.
- `jstack` üç ayrı anda üç farklı yığın gösteriyor: süreç bloke değil,
  sürünüyor. t=95 sn'de toplam CPU 13 sn.

## Sınırlar

- Maestro'nun web desteği **Beta** ve yalnızca Chromium.
- Java 17+ gerekiyor (bu makinede 26 ile ölçüldü).
- Akışlar gerçek Keycloak'a giriyor; sahte oturum enjekte edilmiyor. Yığın
  ayakta değilse akış giriş formunu bulamaz — ama **zaman aşımına düşeceği
  varsayılmamalı**: bu makinede zaman aşımının ateşlediği görülmedi
  (yukarıya bkz.). Sessizce *geçmez*; sessizce *asılı kalabilir*.
- **Bu paket sessiz makine ister — isteğe bağlı değil, önkoşul.** Yüklü
  makinede sonuç anlamsız çıkıyor: kilitlenme gibi görünen şey sürünme, ve her
  hipotez aynı gürültüyü ölçüyor. Koşumdan önce **zorunlu**:
  `~/.claude/scripts/machine-resources.sh check` — çıkış kodu 1 ise koşturma.
  Swap %80'i geçmişse serbest bellek yüzdesi iyi görünse bile başlama
  (`CLAUDE.md` §6).
