# Bizigo Log Analyzer — çalışma protokolü

Bu depoda iş **bir koordinatör ajan** ve **paralel uygulayıcı ajanlar** ile
yürüyor. Aşağıdakiler o düzenin kuralları. Global kurallar
(`~/.claude/CLAUDE.md`) geçerliliğini korur; bunlar onların üstüne gelir.

Kurallar tek tek yazıldı çünkü her biri bu depoda **gerçekten yaşanmış** bir
olaydan doğdu. Gerekçesi olmayan kural yok.

---

## 1 · Roller

**Koordinatör** planlar, ticket'ları böler, ajanları brief eder, dalları
birleştirir, ve **ağır/Docker'lı doğrulamaları faz bitimlerinde kendisi
koşturur**. Kod yazması istisnadır.

**Uygulayıcı ajan** tek bir ticket'ı kendi git worktree'sinde yazar, kendi hafif
testlerini koşturur, commit eder, raporlar. **Push etmez, birleştirmez.**

### Koordinatör önde boş durur

Koordinatörün birinci işi **kullanıcıya açık olmak**. Uzun süren hiçbir şey onun
turunu tıkamamalı:

- Derleme, test paketi, Docker koşumu, ölçüm — hepsi **arka plan prosesi**
olarak başlatılır, sonuç geldiğinde okunur. Öndeki tur beklemez.
- Yapılabilecek her iş **bir ajana verilir**. Koordinatörün kendi eliyle kod
yazması istisnadır; ölçümü koşturmak ile ölçüm aracını yazmak farklı işlerdir ve
ikincisi ajanındır.
- Bir ajan Docker gerektiren bir şeye ihtiyaç duyuyorsa, **aracı ajan yazar,
koordinatör koşturur** — arka planda. Bu, §2'nin bölünmesini bozmadan
koordinatörü serbest tutuyor.
- Kullanıcı bir şey sorduğunda cevap, koşan bir işin bitmesini beklememeli.
Ne koştuğunu ve ne beklediğini söyle, devam et.

Gerekçe: koordinatör tıkandığında beş ajan da tıkanıyor — birleştirmeyi,
kararları ve ölçümleri o veriyor. Bir oturumda dokuz dakikalık bir ölçüm turu
kapattı ve o sırada dört ajan boşta bekledi.

---

## 2 · Test bölünmesi — bu kural pazarlığa açık değil

| Kim | Ne koşturur |
| --- | --- |
| **Ajan** | `dotnet build`, `dotnet test tests/Bizigo.UnitTests`, `npm run typecheck`, `npm test`, `npm run api:generate` / `api:check` |
| **Koordinatör** | Entegrasyon testleri (Testcontainers), compose, canlı Keycloak/ClickHouse/sidecar, benchmark'lar |

**Ajanlar Docker'a hiç dokunmaz.** Entegrasyon testlerini **yazar, koşturmaz** —
ve her testin özet yorumuna *koşturulduğunda ne kanıtlayacağını* yazar.

Gerekçe: makine 16 GB. Beş ajanın paralel Testcontainers koşumu makineyi swap'e
sürüklüyor ve hiçbiri diğerinin maliyetini göremiyor. Ayrıca ölçüm testleri
yüklü makinede **yanlış sayı** üretiyor (bkz. §6).

**Koşturamadığın bir testi "yazdım" diye yeşil gösterme.** `Skip` ile iskelet
bırakmak dürüst; sahte yeşil değil.

**Yeni bir test paketi eklerken bekçinin tanıdığı bir işaret dosyası bırak ya
da bekçiyi genişlet.** `CiCoverageTests` depodaki test köklerini işaret
dosyalarından buluyor (`pytest.ini`/`conftest.py`, `vitest.config.*`, test
SDK'sı referanslayan `*.csproj`) ve her birinin `ci.yml`'da **onu koşturan** bir
adımı olduğunu sınıyor. Tanımadığı bir konvansiyonla gelen paket görünmez
kalır.

Bu satır bir mekanizma değil, **mekanizmanın kapsama notu** — unutulduğunda
kırmızı yanan hâlâ bekçi. Gerekçe ölçüldü: `sidecar/tests/` altında dört pytest
dosyası vardı, yapılandırma yerindeydi, ve CI onları **hiç koşturmuyordu**.
Yazan kişi doğru şeyi yapmıştı; eksik olan disiplin değil bir bağdı.

---

## 3 · Proses hijyeni

**Başlattığın her prosesi temizle — hata alsan, iş yarıda kalsa bile.** Bir
oturum 29 saat `tail -f` bıraktı; başka bir oturum ölü bir API'yi arka planda
unuttu.

- PID'i dosyaya yaz, işin sonunda o PID'i öldür.
- Öldürme desenini **dar** tut. Geniş bir `pkill -f` bir keresinde oturumun
kendi izleyicilerini öldürdü.
- Konteyner açtıysan kapat; Docker Desktop'ı sen açtıysan sen kapat.
- `machine-resources.sh claim` ile duyur, bitince `release` et.
- **Ağır işten önce `~/.claude/scripts/machine-resources.sh check`; çıkış kodu 1
ise başlama ve koordinatöre haber ver.**

İstisna: `dotnet` MSBuild node havuzu (`/nodemode:1`) SDK'nın kasıtlı davranışı
ve ajanlar arasında paylaşılıyor. Öldürme; faz sonunda
`dotnet build-server shutdown`.

**Başkasının prosesini körlemesine öldürme.** `report` sahibini gösterir.

---

## 4 · Worktree yaşam döngüsü

Her ajan kendi worktree'sinde çalışır — paylaşılan `obj/` bozulmasın diye.

1. Ticket verilirken worktree açılır (`traycer_create_worktree`).
2. İş main'e girip **doğrulandıktan** sonra worktree silinir.
3. Ajan yeni ticket'a geçerken **önce** yeni worktree'ye bağlanır
(`traycer_configure_agent`), **sonra** eskisi silinir. Sıra tersine dönerse
ajan çalışma dizinsiz kalır.
4. `main`'e tamamen girmiş dallar silinir (`git branch -d`, `-D` değil —
`-d` girmemiş commit varsa reddeder, bu bir bekçidir).

Worktree dizini ile içindeki dalın adı ayrışabilir; **dizin adına güvenme**,
`git worktree list` çıktısına bak.

---

## 5 · Birleştirme protokolü

**Sıra önemlidir: önce yapısal değişiklik, sonra satır düzeyindekiler.** Bir
ajan bir dosyayı yeniden yazdıysa onun dalı önce girer; diğerlerinin satır
silmeleri o yapının üstüne uygulanır. Tersi olursa aynı iş iki kez yapılır.

**Üretilen dosyalar elle birleştirilmez.** `ui/openapi/bizigo-api.json` ve
`ui/src/lib/api/schema.d.ts` çakışırsa herhangi bir taraf alınır, sonra
`npm run api:generate` ile **kaynaktan yeniden üretilir** ve `api:check`'in
birebir dediği görülür. Bir üretilen dosya için anlamı olan tek çözüm budur.

**Çakışma büyükse ve iki tarafın anlamını ajan senden iyi biliyorsa, merge'i
ona devret.** Yedi dosyalık bir parser çakışmasında koordinatörün tahmin
etmesi gereken şey çok; ajanın yok.

**Git'in göremediği çakışmalar vardır.** Bir ajan arayüze üye ekler, başka bir
ajanın test sahtesi onu uygulamaz: metinsel merge temiz, derleme kırık. Her
birleştirmeden sonra **derle ve koştur**.

Aynı sınıfın ikinci örneği daha sinsi: iki ajan compose'a ayrı ayrı `redis`
servisi ekledi. Metinsel merge temiz, derleme temiz, testler yeşil — ama YAML
dosyası **hiç ayrıştırılamıyor**, yani compose yığınının tamamı kullanılamaz.
Kırığı yalnızca yığını ayağa kaldırmayı deneyen görüyor. Ne derleme ne birim
testi bu sınıfa bakıyor; ürünün **yapılandırma dosyaları** da birleştirmenin
kurbanı olabiliyor ve onların bekçisi ayrı.

**Push ettikten sonra CI'yı oku. Kendi koşturduğun testler CI'nın yerine
geçmez.** Bu depoda `docker compose config --quiet` T01'den beri duruyordu ve
yukarıdaki kırığı ilk merge'de yakaladı. Bekçi bağırdı; **dört merge boyunca
kimse bakmadı**, çünkü koordinatör kendi yeşil koşumuna bakıp geçti. Kırığı
bulan şey nihayetinde bir CI logu değil, elle `docker compose up` denemesiydi —
yani bekçi olmasaydı da aynı gün bulunacaktı. Bekçinin kazandırdığı dört merge
boşa gitti.

Kural: `git push`'un ardından `gh run list` ile o koşumun sonucuna bak. Kırmızıysa
sıradaki ticket'ı verme. "Bende yeşildi" bir CI kırmızısını kapatmaz — CI'nın
gördüğü şey senin koşmadığın şeydir, zaten o yüzden orada.

**Bir kapının kırmızı yanması ile o kırmızının okunması ayrı olaylardır.** Kapı
eklemek işin yarısı; okunmayan kapı, olmayan kapıyla aynı sonucu veriyor ve
üstüne "bu soru sorulmuş" yanılsaması bırakıyor.

---

## 6 · Ölçüm kültürü

**Bekçinin kırmızı yanabildiğini ölç, sonra geri al.** Geçen bir test geçtiğini
kanıtlamaz; kırılabildiğini göstermek kanıtlar. Rapora "ölçtüm" diye yaz.

**Bir testin geçme sebebinin duvar saatiyle ilgisi olmamalı.** Bu depoda iki kez
yaşandı:
- `DiscoveryWorkerTests` sidecar zaman aşımını 200 ms'ye çekiyordu; aynı sınıfta
20 bin olay basan bir test ThreadPool'u doyurunca sonraki test süreye takılıyordu.
Tek başına geçiyor, sınıfla 3/3 düşüyordu. "Kararsız test" diye raporlanmıştı;
değildi.
- `GrokPropertyTests` 2 saniyelik bütçeyle makineyi ölçüyordu.

Ölçüt: **test neyi ölçmek istiyor?** Duvar saati değilse süreyi denklemden
çıkar, büyütme. Ölçüyorsa mutlak bütçe yerine **aynı süreçte alınan bir tabana
oran** kullan.

**Benchmark'lar yüklü makinede yanlış sayı üretir.** K35 ölçümü ajanda 1,46×,
koordinatörde 1,62× çıktı; ikinci koşumda *yalnız ayrıştırma* kolu
*ayrıştırma+etiketleme*'den yavaş göründü — fiziksel olarak imkânsız, yani
makine sessiz değildi. Bağlayıcı sayı **sessiz makine** ister. Tek sayı seçmek
yerine iki koşumu da kaydet.

**Release/Debug farkı ölçümü tek yönde yanıltabilir.** K35'te Debug tabanı
şişirip değişikliği hak etmediği kadar ucuz gösteriyordu. Benchmark `-c Release`.

---

## 7 · Bu depoda "hata" ne demek

**Sessiz yanlış davranış en pahalı hata sınıfı.** F1'in bütün dersi bu. Yakalanan
örnekler:

- Yanıttaki imlecin adı istekten farklıydı → ekran aldığı imleci geri
gönderemiyordu → **yarım imleç sessizce ilk sayfayı tekrarlıyordu**.
- CSV'de aynı kaynak iki kez geçince son satır sessizce kazanıyordu — kazanan şey
**owner_group**, yani kapsamın kendisi.
- Fark görünümü NFC normalize etmiyordu; ingest zaten NFC'ye çeviriyor, yani ekran
**boru hattının sildiği bir farkı** raporlayacaktı.
- ASA config'inde sır maskelenmiş metinden çıkıyor ama **bölüm adının içinde**
kalmaya devam ediyordu.
- `REPLACE PARTITION` atomik diye replay'in canlı ingest'i bozmadığı
*varsayılmıştı*; okuma ile değiştirme arasındaki pencerede yazılan satırlar
sessizce siliniyor.

Ortak nokta: hata yok, sayaç yok, belirti yok. Bir şey ölçülmediyse **çalıştığı
varsayılmaz**.

**Dış bir ikili gerektiren test ya CI'da o ikiliyle koşmalı ya koşumdan açıkça
dışlanmalı.** Üçüncü hâl — *"koşuma giriyor ama ortam hazır değil"* — sessizce
kırmızı yanan bir CI. Ekran görüntüsü bekçileri korumasız `chromium.launch()`
yapıyordu; yazan ajan raporuna "varsayılan pakete koymadım" yazmıştı ama
`vitest.config`'in `include` deseni dosyayı alıyordu. Kimse ikisinin
ayrıştığını okumadı. İhlali bir kişi değil **yapılandırma** yapıyor, o yüzden
kural burada duruyor: testle kovalamak CI yapılandırmasını test etmek olurdu.

**Bir bekçinin sessizce atlaması, bekçinin kendisinden tehlikelidir.**
`Produces<T>` kapısı uçları elle yazılmış bir listeden topluyordu; üç uç dosyası
listede olmadığı için **16 uç kapıya hiç görünmüyordu ve üç test de yeşildi.**
Yeşilliği hiçbir şey ifade etmiyordu.

---

## 8 · Sözleşme ve kapsam disiplini

**Tüketicisi olmayan bir tip tahmindir.** Yanıt tipleri, o ucu gerçekten tüketen
ekranla birlikte gelir. Bekleyenler `ProducesContractTests.Pending`'de **ticket
atfıyla** durur ve liste **boşalmadan F2 bitmiş sayılmaz**.

**"Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz.** İkisi tek
listedeyken "liste boşaldı mı" sorusunun cevabı asla evet olamaz. `Exempt`
ayrıdır ve sayısı `ExpectedExemptCount` ile sabittir: muafiyet eklemek **iki
ayrı bilinçli hareket** gerektirir.

**Kırmak bedava iken kır.** Bir uç sözleşmesi yanlışsa ve tek tüketicisi ürünün
kendi ekranıysa, düzeltmenin maliyeti sıfırdır. Dışarıdan bir tüketici
doğduktan sonra aynı düzeltme ya pahalı olur ya hiç yapılmaz.

**JSON adlandırma `snake_case`**, `JsonPropertyName` ile. camelCase politikası
`idp_groups`'u `idpGroups` yapıp sözleşmeyi sessizce kırıyordu.

**Depolama tipi tel sözleşmesi değildir.** Anonim nesne yerine response record
kullan; yoksa domain tipine eklenen her alan kimse karar vermeden API'ye sızar.

---

## 9 · Paralel ajanlar arası koordinasyon

**Kesişen bir uç varsa sözleşmeyi önceden çivile**, sıraya sokma. "Kim önce
merge olacak" cevabı ikisinden birini boşta bırakır. Uç gövdesini ve alan
adlarını ikisine aynı anda bildir; sahibini de belirt.

**Aynı satırı iki ajana sildirme.** Uç sahipliğini ticket başlığından değil
**kodun çizdiği sınırdan** türet — bu depoda yetki tablosu (author/admin)
T19/T20 ayrımını ticket'lardan daha iyi çizdi.

**İkinci kopya yazma.** Ortak yüzey varsa (`GetSourceActivityAsync`,
`SecretProtector`, `ui/src/components/ui/*`, `@/lib/api/errors`) genişlet,
kopyalama. Bir ajan bir yardımcıyı ortak yere taşıdıysa diğerlerine bildir.

**Bir ajanın sapması gerekçeliyse kabul et.** Bu turda dört sapma koordinatörün
talimatından iyiydi ve hepsi gerekçesiyle bildirildi.

---

## 10 · Rapor biçimi

Ajan raporu şunları içerir: commit hash'i ve dal, **ölçülen** sayılar (build,
birim, UI), verilen kararlar **gerekçeleriyle**, kırmızı yandığı ölçülen
bekçiler, **yapılmayanlar**, ve tereddüt edilen yerler.

**Tereddüdü sakla, sor.** Koordinatörün cevaplaması gereken bir şeyi tahminle
doldurma.

**"Aradım, yok" ile "aramadım" farklı şeylerdir**; ikisini de yaz.

---

## 11 · Epic artifact'ları depoda

Planlama artifact'larının kanonik yeri Traycer epic dizini
(`~/.traycer/epics/<id>/artifacts`), ama ajanlar ayrı worktree'lerde çalıştığı
için oradan **okuyamıyorlar**. Depodaki kopya `docs/epic/`.

**Senkron yönü depodan epic dizinine.** Ters yön veri kaybettiriyor:

```bash
rsync -a --delete docs/epic/ ~/.traycer/epics/<id>/artifacts/
```

Gerekçe ölçüldü. Ajanlar epic dizinine **erişemiyor** — ayrı worktree'lerdeler —
dolayısıyla yazdıkları tek yer depo kopyası. Koordinatör "kanonik olan epic
dizini" diye ters yönde rsync çalıştırınca ajanın yazdığının üstüne yazıyor.
Bir kez oldu: T27'nin 233 satırlık envanteri 148 satıra düştü ve yeni yazdığı
bölüm tamamen kayboldu. `git checkout <commit> -- <dosya>` ile geri alındı,
ama fark edilmeseydi sessizce kaybolacaktı.

Kural: **depo yazılabilir kaynak, epic dizini görüntü.** Ters yönde
çalıştırman gereken bir durum varsa önce `diff -rq` ile ne kaybedeceğini gör.

`.gitignore`'daki `artifacts/` satırı .NET derleme çıktısı içindir; `docs/epic/`
onu etkilemez.

---

## 12 · Ortam

- .NET 10 SDK `~/.dotnet` altında:
`export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"`
- `Bizigo.Api`'yi elle koşturmak: CWD **depo kökü** (katalog/maske yolları
oradan çözülüyor), içerik kökü **bin dizini** (appsettings oradan okunuyor),
`ASPNETCORE_ENVIRONMENT=Development` (WAL dizini aksi hâlde `/var/lib/bizigo`).
- Keycloak realm'inde **yalnızca `bizigo-claims` client scope var**; yerleşik
`profile`/`email` hiç oluşmuyor. `scope=openid` geçer,
`openid profile email` canlıda `invalid_scope` alır. Ölçüldü.
- Commit mesajları **İngilizce**, kullanıcıyla iletişim **Türkçe**.
