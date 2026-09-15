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

**Bölünmenin gerçek ekseni "hangi paket" değil "konteyner gerekiyor mu".**
Tablodaki gerekçe Docker'a bağlı, ama kural paket adına yazılınca ajan
konteyner istemeyen bir entegrasyon testini de koşturamıyor — oysa maliyeti
bir birim testininkiyle aynı. Ekseni düzeltmek "ajan karar versin" demek
değil; yargı çağrıları sessizce genişler. Ölçüt **mekanizmaya** bağlanıyor:

1. **Ajan bir entegrasyon testini yalnızca Docker kapalıyken koşturabilir.**
Konteyner isteyen test daemon'a bağlanamayıp hemen düşer, hiçbir kaynak
tüketmez. Docker'ı **açmak** hiçbir koşulda ajanın işi değil.
2. **Atlanan test kanıt değildir** — yalnızca **geçen** test konteynersiz
sayılır. `3 geçti / 4 atlandı` sonucunda o dört test hakkında hiçbir şey
söylenemez.
3. **Bu çıkarım §7'ye bağlı.** *"Geçti ⇒ konteynersiz"* ancak beyansız atlama
yasakken doğru: konteyner yokluğunu görüp `Skip` yerine erken `return` ile
çıkan bir test "geçti" diye raporlanır ve 2. maddeden de temiz geçer.

Üçüncü madde kuralın kendisi kadar önemli. Yazılmasaydı mekanizma kendi
başına ayakta duruyor gibi görünürdü, ve dayandığı varsayım değiştiğinde
kimse buraya bakmazdı.

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

**Simetrisi de doğru: yerelin gördüğü şey CI'nın koşmadığı şey olabilir.**
`posthog-js` bir merge ile `package.json`'a girdi ve `node_modules`'a girmedi;
CI **yeşildi** çünkü orada `npm ci` koşuyor, ama yerelde çalışan herkes iki
`tsc` hatası görüyordu. Yani CI temiz bir ortamı ölçüyor ve **kimsenin
çalışmadığı** ortamı ölçüyor.

İkisi farklı soru soruyor ve ikisi de gerekli: CI *"temiz bir makinede kurulur
mu"*, yerel *"bu makinede bugün çalışır mı"*. Birinin yeşili diğerinin yerine
geçmiyor — ve `npm install`'ı merge sonrası koşturmak (B16) bu yüzden bir
alışkanlık değil bir kural.

---

## 6 · Ölçüm kültürü

**Bekçinin kırmızı yanabildiğini ölç, sonra geri al.** Geçen bir test geçtiğini
kanıtlamaz; kırılabildiğini göstermek kanıtlar. Rapora "ölçtüm" diye yaz.

**Kusuru uyguladıktan sonra dosyada gerçekten olduğunu doğrula, sonra koştur.**
Kırmızı ölçümünde yeşil bir sonuç iki şey anlatabiliyor: *"kusur etkisiz"* ya
da **"kusur hiç uygulanmadı"**. İkisi aynı çıktıyı veriyor ve birincisi
varsayılıyor.

Bir turda **üç ajan bağımsız olarak** aynı tuzağa düştü ve üçü de kendi
yakaladı — üç örnek bir desen:

| Ne oldu | Sonuç |
| --- | --- |
| `python3 -c` içinde tırnak kaçışı tutmadı | Dosya **hiç değişmedi**, test yeşil geldi |
| Kusur uygulandı ama ölçüme sabit bir zaman damgası verildi | Kova hiç değişmedi, testler yeşil kaldı — "kusur yok" hâliyle **aynı şey** ölçülmüştü |
| Yeni bir kavram eklenirken onu görecek eski kod aranmadı | İki gösterim doğdu, birim paketi sessiz kaldı, CI kırmızı yandı |

Yordam: kusuru yaz → dosyayı **oku** ve kusurun orada olduğunu **iddia et**
(`assert 'KIRMIZI' in dosya`) → koştur → geri al. İddia adımı olmazsa ölçüm
kendi başarısızlığını sessizce başarı diye raporluyor — yani ölçüm aracının
kendisi bu deponun §7'de tarif ettiği sınıfa giriyor.

> **Yeşil bir sonuç, ölçümün yapılmadığı anlamına da gelebiliyor.**

**Geri almanın derlemeye ulaştığı ayrıca görülmeli.** İddia adımı kusurun
*girdiğini* kanıtlıyor; *çıktığını* kanıtlayan bir adım yoktu. T44'ün ölçüm
aracı geri almayı `shutil.copy2` ile yapıyordu ve o **zaman damgasını da geri
yüklüyor**: düzeltilmiş kaynak derlenmiş ikiliden eski göründü, MSBuild projeyi
atladı, `dotnet build` **0 hata 0 uyarı** dedi ve koşan ikili hâlâ kusurluydu.

| | Kaynak | İkili | Görünen |
| --- | --- | --- | --- |
| `grep` | temiz | — | "geri alındı" |
| `build` | — | kusurlu, atlandı | "0 hata, 0 uyarı" |
| `test` | — | kusurlu | **kırmızı** |

Üç araç üç farklı şey söyledi ve üçü de kendi içinde doğruydu. Bu, yukarıdaki
sınıfın **tersten** hâli: orada yeşil bir sonuç *"ölçüm yapılmadı"* olabiliyor,
burada kırmızı bir sonuç *"geri alma görülmedi"* oluyor — ve ikincisi daha
sinsi, çünkü kırmızı bir test insanı kodun kendisini aramaya gönderiyor.

Yordamın son adımı bu yüzden `git checkout` ya da dosya kopyalamakla bitmiyor:
**geri aldıktan sonra tam paketi bir kez daha koştur.** Kusuru yakalayan şey bir
bekçi değildi, o son koşumdu. Kopyalarken zaman damgasını taşıma (`copy` +
`touch`); `copy2` bu depoda bir kez ölçümü sessizce yalancı yaptı.

**`git checkout <dosya>` ile geri alma, commit edilmemiş işin üstüne yazar.**
Aynı turda FS-b'nin ölçüm betiği kusuru böyle geri aldı ve `entrypoint.sh`'i
S03 hâline döndürdü — kendi commit edilmemiş S06 çalışmasını sildi. Fark etti ve
yeniden yazdı, ama fark etmeyebilirdi. Ölçüm yordamı **yedek dosyaya** dayanmalı;
`git checkout` çalışma ağacının tamamına bakan bir araç, tek bir kusurun geri
alınması için fazla geniş.

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

### Bir kapının varlığı, neye baktığını söylemiyor

Bu, aşağıdaki altı kaydın ortak çatısı ve **bir turda beş kez** ölçüldü: bekçi
yerinde, yeşil, ve koruduğunu iddia ettiği şeyi görmüyor. Hiçbiri dikkat
eksikliği değil — hepsi *"kapı var"* ile *"kapı doğru şeye bakıyor"* arasındaki
mesafe.

Ölçülen beş örnek: `McpBoundaryGate.Require` **silinse** K6 testi yeşil kalıyordu
(kap boştu, her hâlde aynı istisna geliyordu) · `IlCallReader` bütün `async`
gövdelerini kaçırıyordu (derleyici onları durum makinesine taşıyor) · kota rezerv
kusuru **dört yerde** birbirini doğruluyordu, o yüzden hiçbiri yanmadı ·
`RcaQuotaUsage.BySource` hesaplanıp **atılıyordu** · `pre-push` kancası CI'nın
değil **bekçi işinin** sonucunu okuyordu.

**Öldürülmüş bir ölçüm geri alınmış bir ölçüm değildir — ve çalışmış bir geri
alma durmuş bir koşum değildir.** Kusur enjekte eden bir betik sinyalle
kesilirse ağaçta kusur bırakıyor. Ve `trap` akışı **durdurmuyor**: sinyali
işleyip bulunduğu yere dönüyor, döngü devam ediyor ve **bir sonraki kusuru
uyguluyor**. Geri alma `finally` içinde ve döngüden **çıkarken** koşmalı; o zaman
bu sıra ifade edilemez hâle geliyor. Bu turda iki worktree'de yaşandı.

**Bir davranışı ölçüp yazmak, onu doğrulamak değil.** Ölçüm *"bugün böyle
çalışıyor"* der; testin çivilediği şey *"böyle çalışması gerekiyor"*. Aradaki
adım bir **karar** ve atlanırsa kusur bekçiye dönüşür. Ölçülen hâli: M05 kota
rezervinin ajanı kapsamadığını ölçtü, doğru yaptı, ve testi o kusuru bir
**özellik** olarak çiviledi.

**Bir alanın okunduğunu ölçmek, okunanın kullanıldığını ölçmek değil.** IL'de bir
`MemberRef` satırı, arızi bir `.Count` çağrısıyla da yazılır. Ve bir raporun
*şeklini* sınayan test (adlar enum'dan, sınırlar hesaptan) taşıdığı **veriyi**
sınamıyor olabilir.

**Bir adı metinde bulmak, o tipe dokunmak değil.** `grep` yorumları da sayıyor:
`IScopedQuery` tüketicisi ararken yedi derleme çıktı, doğru cevap dörttü — üçü
tipi yalnızca belge yorumunda anıyordu. Küme **meta veriden** çıkarılmalı.

**Bir iddiayı tek yerde düzeltmek, düzeltildiği anlamına gelmiyor.** Kaynağı
düzeltilmemiş bir kopya sadakatle yanlış kalır. Ölçülen hâli: *"realm'de yalnızca
`bizigo-claims` var"* **üç** yerde duruyordu — `CLAUDE.md` ve onu kaynak gösteren
iki vault sayfası. Bir kopya düzeltildi ve düzeltme *"yanlış iddia yakalandı"*
diye raporlandı; yakalanan şey bir **kopyaydı**. Refleks: bir iddiayı
düzeltmeden önce **kaç yerin söylediğini say**.

**Damgası düşmemiş bir vault sayfası "doğru" demek değil** — yalnızca kaynağının
değişmediği demek. Damga bir **inceleme** değil; §11'in okuma adımı tam bu yüzden
var.

### Varsayılanla aynı olan bir değer, ölçümün bağını görünmez kılıyor

**Bir testin üretimi okuduğunu, üretimle aynı cevabı vermesi kanıtlamıyor.**

Ölçüldü: bir bekçi üretimin doğrulama parametrelerini okuyordu; testi üretimden
**kopardık** (elle kurulmuş bir nesneyle değiştirdik) ve **hiçbir şey yanmadı**.
Sebep, kütüphanenin varsayılan toleransının da seçilmiş değere **eşit** olması —
kopuk test, bağlı testle aynı sonucu üretiyordu. Test *"üretimin kararını
ölçüyorum"* derken aslında *"bir kararı ölçüyorum"* diyordu.

Refleks: bir bekçinin üretime bağlı olduğunu ölçmek için, ölçtüğü değerin
üretime **özgü** olması gerekiyor.

**Ve bunun en sinsi hâli: bekçiyi körleştiren şey onu AÇIKLAYAN metin olabiliyor.**
Ölçüldü — bir compose bekçisi `ui` bloğunun tamamını okuyordu, ve sağlık
kontrolünün **üstündeki gerekçe yorumu** aranan uç adresini zaten içeriyordu.
Bekçi *"uç yoklanıyor mu"* değil *"ucun adı dosyada geçiyor mu"* diye soruyordu;
kusur konulduğunda **yeşil kaldı**. Yorumlar elenince kırmızı yandı.

Aynı turda ikinci ölçüt aynı kusurda kırmızı yanmıştı ve bu **tesadüf**: kusur
aranan dizgeyi *ekliyordu*, o yüzden `DoesNotContain` düşüyordu. Yani iki
ölçütten biri kör biri sağlamdı, ve **tek bir "yeşil" ikisini temsil ediyordu**. Çözüm bir **parmak izi** iddiası: ürüne ait,
kütüphanenin varsayılanında bulunmayan bir alan (bizde `RoleClaimType`, claim
sözleşmesinden). Elle kurulmuş bir nesnede o alan yok, yani kopma yanıyor.

Ve ikinci yarısı: o turda seçilmiş değeri değiştirmek boşluğu **tesadüfen**
kapatıyordu — yeni değer varsayılandan farklı olduğu için. Ona bırakılmadı.
**Bir boşluğun tesadüfen kapanması, kapatılması değil**: sayı bir gün varsayılana
dönerse bağ yine görünmez olur, parmak izi ise ölçmeye devam eder.

Aynı turun ironisi kuralın kendisini anlatıyor: kapanmayan kriterin kusuru
*"tolerans seçilmemiş"*, bekçinin kusuru *"seçilmemiş varsayılana yapışık"* —
tek kök, iki kılık.

### Bir davranışın ölçülmesi, o davranışı BİLDİREN sayacın ölçülmesi değil

Ölçüldü: WAL dolduğunda ack'in durması **çiviliydi** — `WalFullException` →
`RejectFull()` → `IngestResult(Full, …)` → 503, ve sonucun `Full` olduğu ile
`Retry-After` ipucunun yapılandırmadan geldiği testliydi. **Sayacın kendisine
hiçbir test bakmıyordu.**

Bedeli asimetrik: `RejectFull()` çağrısı düşse **davranış aynı kalıyor** —
istemci yine 503 alıyor, mevcut testler yeşil kalıyor — ve *"ingest durdu mu"*
sorusunu sayaçtan okuyan bir ölçüm **0** görüp *"hayır"* diyor. Yani kusur
davranışta değil **raporlamada**, ve raporlamayı okuyan taraf başka bir ölçüm.

Ayrım şu: bir sayaç bir bekçinin **öznesi** değil, başka bir ölçümün **girdisi**.
Girdisi olan şeyin doğru olduğunu hiçbir davranış testi göstermiyor.

Refleks: bir ölçüm bir sayaç okuyacaksa, o sayacın **arttığı** ayrı bir bekçiyle
tutulacak — ve **karşı yön de**: hem reddeden hem kabul sayan bir hata, okuyana
*"devam ediyor"* dedirtir. İki iddia, tek testte: `RejectedFull = 1` **ve**
`AcceptedBatches = 0`.

### Doğrulama çıktısını `tail`'den geçirmek, aradığın arızayı yutuyor

İki kez oldu ve ikisinde de kaybedilen şey **arızanın kimliği**:

- `npm run api:check | tail -3` → komut **17 hata** verdi, rapora *"koşmadı"*
  diye geçti. Hataların kimliği kayıp; tekrarlanamadı çünkü o koşumda disk
  kapısı da kırmızıydı ve `ENOSPC` sıradan bir test hatası gibi okunuyor.
- `dotnet test | tail -N` → **düşen testin adı** kesildi, yalnızca özet kaldı.

Sebep basit ve o yüzden tekrarlıyor: özet satırı **sonda**, arızanın kimliği
**ortada**. `tail` özeti getiriyor, yani çıkış kodu doğru okunuyor ve *"neyin
düştüğü"* sessizce gidiyor — komut kırmızı, rapor eksik.

Refleks: doğrulama çıktısı **tam loga** yazılıyor; kısaltma yalnızca `grep` ile
ve **hata desenini de kapsayarak** yapılıyor (`grep -E "\[FAIL\]|error|Başarısız"`).
Bir arızanın *var olduğunu* bilmek, *ne olduğunu* bilmek değil.

### Paylaşılan ASP.NET çatısı `Bizigo.Cli`'ye inemez

Üç ayrı yerden aynı duvara çarpıldı: `AddJwtBearer` kullanmak, test derlemesine
`InternalsVisibleTo` vermek, ve namespace seçimi. Sebep üretilen `Program` tipi —
`Bizigo.Api` ile `Bizigo.Cli` ikisi de üretiyor ve çatı paylaşılınca `CS0433`
geliyor. Kimlik doğrulama gibi ortak ihtiyaçlar `Microsoft.IdentityModel`
düzeyinde çözülmeli, ve **sürüm ölçülerek** seçilmeli (API'nin zaten çözdüğü
sürüm): ayrı sürüm, aynı belirteç hakkında farklı karar demek.

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

**Bir "ilk bakılacak yer" işareti, aramanın kapsamı yazılmadan bir kapsam
iddiası gibi okunuyor.** Yukarıdaki kural raporun tamamı için geçerli ama
işaretin yanında ayrıca söylenmesi gerekiyor, çünkü tek satırlık bir işaret
arkasında iki saatlik bir eleme de olabilir tek bir sezgi de — ve okuyan
ikisini ayırt edemeyip birincisini varsayıyor. Biçim:

> **İlk bakılacak yer:** X. **Aradım ve elemedim:** Y, Z. **Aramadım:** W.

Gerekçe ölçüldü. S04'te bir ajan `sir-dondu` testinin maskeleme biçimini
"ilk bakılacak yer" diye işaret etti; işaret **doğruydu** ve koordinatör
doğrudan oraya baktı. Ama aynı turda üç testi birden düşüren şey başka bir
yerdeydi (baseline'ın iki gösterimi) ve ajan onu **aramamıştı** — yazmadığı
için de kimse aramadığını bilmiyordu. Yanlış işaret işaretsizlikten kötüdür;
**kapsamsız doğru işaret** de aynı yöne çekiyor.

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

### Bir epic belgesini değiştirdiysen vault sayfasını da gözden geçir

`docs/wiki/` altındaki sayfalar epic belgelerinden **damıtılmış** ve her biri
kaynaklarının özetini (`source_digest`) taşıyor. `WikiSourceDigestTests` o
özetleri her koşumda yeniden hesaplıyor; kaynak değişip sayfa güncellenmediyse
**birim paketi kırmızı yanıyor**.

Bu bir gürültü değil, kapının kendisi: damıtılmış bir sayfa kaynağından
ayrıldığı an **ölçülmüş gibi okunan yanlış bir metne** dönüşüyor.

Kural: bir `docs/epic/**` belgesine yazdıysan, o belgeyi kaynak gösteren vault
sayfalarını **oku ve gerekiyorsa güncelle**, sonra damgala:

```bash
BIZIGO_WIKI_STAMP=1 dotnet test tests/Bizigo.UnitTests \
  --filter FullyQualifiedName~WikiSourceDigestStamper
```

**Okumadan damgalama.** Damga *"sayfa kaynağıyla uyumlu"* demek; *"kaynağı
okudum"* demek değil. Körlemesine damgalamak bekçiyi bir kayıt olmaktan çıkarıp
gürültü bastırıcıya çevirir — bu deponun beş kez adını koyduğu şey.

Bir sayfa **etkilenmediyse** onu da commit mesajına yaz. Damganın yenilenmesi
her iki hâlde de aynı görünüyor; yazılmazsa bir sonraki kişi *"bakılmadı mı,
bakıldı mı"* diye ayırt edemiyor.

Gerekçe ölçüldü: bu kural konulana kadar aynı kırmızı **üç ayrı turda** main'i
kırdı ve her seferinde farklı bir ajanın belgesi yüzünden — T32, T30, T39.
Sorun ajanların dikkatinde değil, kuralın yazılı olmamasındaydı.

---

## 12 · Ortam

- .NET 10 SDK `~/.dotnet` altında:
`export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"`
- `Bizigo.Api`'yi elle koşturmak: CWD **depo kökü** (katalog/maske yolları
oradan çözülüyor), içerik kökü **bin dizini** (appsettings oradan okunuyor),
`ASPNETCORE_ENVIRONMENT=Development` (WAL dizini aksi hâlde `/var/lib/bizigo`).
- Keycloak realm'inde **iki** client scope var: `bizigo-claims` (varsayılan) ve
`bizigo-mcp` (**isteğe bağlı**, M09 ekledi — RFC 8707 kaynak kimliği için).
Yerleşik `profile`/`email` hiç oluşmuyor: `scope=openid` geçer,
`openid profile email` canlıda `invalid_scope` alır. Ölçüldü.
  - ⚠️ Bu satır bir zamanlar *"**yalnızca** `bizigo-claims` var"* diyordu ve M09'dan
  sonra yanlış oldu. **Üç yerde** tekrarlanmıştı — burada ve iki vault sayfasında —
  çünkü ikisi bu satırı kaynak gösteriyordu. M17 vault'un birini düzeltti, ikincisi
  damgası düşene kadar görünmedi, ve **kök buydu**. Ders: bir iddiayı tek yerde
  düzeltmek, düzeltildiği anlamına gelmiyor; kaynağı düzeltilmemiş bir kopya
  sadakatle yanlış kalır.
- Commit mesajları **İngilizce**, kullanıcıyla iletişim **Türkçe**.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
