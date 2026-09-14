---
title: "T41 — Prompt redaksiyon tabanı: log metninde sır tanıma"
kind: ticket
status: 2
---

# T41 — Prompt redaksiyon tabanı

> **Bu ticket F4'ün önkoşuludur.** Kapanmadan RCA raporunun `masked` ve `raw`
> içerik düzeyleri açılmıyor; F4 yalnızca `summary` düzeyiyle sevk edilebilir.
> Sıralama kararının gerekçesi
> [RCA raporu özelliği §2.1](../../rca-raporu-ozelligi/index.md#21--prompta-ne-giriyor--karar-2026-08-25)'de.

**Kaynak:** RCA tasarımı §2.1, *"Ama bir taban var ve o ayarlanabilir değil"* ·
**Yöneten kararlar:** K6 (yerel model / veri kurum dışına çıkmaz), K22 (F4'te
LLM yorumu), CLAUDE.md §9 (ikinci kopya yazma), §7 (sessiz yanlış davranış)

---

## 1 · Değişmez

Sır içeren bir satır **hiçbir düzeyde** prompt'a girmemeli.

Bu cümlenin `summary` / `masked` / `raw` üçlüsüyle ilişkisi şu: o üçlü
**ayarlanabilir bir parametre** — ne kadar bağlam gideceğini ölçüm belirliyor.
Taban ayarlanabilir değil. Bir düzey seçimi *"ne kadar bağlam"* sorusunu
cevaplıyor; taban *"hangi bağlam hiç gitmez"* sorusunu cevaplıyor ve cevabı
düzeyden bağımsız.

Bugün o tabanı sağlayan bir bileşen **yok** — ve bu bir arama sonucudur, bir
varsayım değil (§2).

## 2 · Dört aday, dördü de değil — ölçüldü

§2.1'in tablosu burada tekrar edilmiyor, **kod okunarak** doğrulanmış hâliyle
duruyor:

| Bileşen | Nerede | Gerçekte ne yapıyor | Neden taban değil |
| --- | --- | --- | --- |
| `catalog/masks/bizigo-masks.yaml` | 13 maske, Python sidecar + .NET ortak okur | `IPV4`, `NUMBER`, `HOSTNAME`, `URI`… — şablon madenciliği için **değişken** kısımları siler | Amacı imza kararlılığı. Altın satırlarından biri: `Failed password for admin from 10.1.2.3` → `Failed password for admin from <IPV4>`. **IP gitti, "password" durdu** — modelin ihtiyacı olan bağlamı siliyor, sır sınıfına hiç bakmıyor |
| `SecretProtector` | `Bizigo.Contracts.Security` | Saklanan kanal sırlarını AES-256-GCM ile **şifreliyor** | Girdisi yapılandırma alanı; log metni hiç görmüyor |
| `ConfigNormalizer` (T04 config scrub) | `Bizigo.Devices` | Cihaz config satırında `password`/`psksecret`/`pre-shared-key`… anahtar kelimesinin **değerini** maskeliyor | Girdisi cihaz config'i. Deseni `^`'a bağlı ve *"≤4 belirteç + anahtar kelime + ayırıcı + değer"* biçimini varsayıyor — bu biçim config satırının biçimi, log satırının değil |
| **`SecretRedactor`** | `Bizigo.Contracts.Security` | **Bilinen** bir dizgeyi ve türevlerini metinden söküyor: URL parçaları, `MinFragment = 6` tabanı, uzundan kısaya sıra | Girdisi *"şu dizgeyi maskele"*. **Keşif yapmıyor** |

Sonucu §2.1'in cümlesi: **taban bir ikame sorunu değil bir keşif sorunu.**
`SecretRedactor` ikame yarısını zaten çözüyor; eksik olan ona *neyi*
maskeleyeceğini söyleyecek taraf.

Karar (§3) bu tabloyu ortadan kaldırmıyor, **iki satırını birleştiriyor**:
keşfi `ConfigNormalizer`'ın deseni yapıyor, ikameyi `SecretRedactor` yapıyor,
ve ikisi de kendi yerinde kalıyor. Dördünden hiçbiri tek başına taban değildi;
taban ikisinin **bağı**. Deseni olduğu gibi taşımanın neden yetmediği §3'ün
tuzak bölümünde.

### `ConfigNormalizer` neden yine de okunmalı

Taban değil ama bu depodaki **tek keşif denemesi** o, ve iki kez kırıldı — ikisi
de aynı sınıf, ikisi de kendi XML yorumunda yazılı:

1. Desen anahtar kelimeyi satır başına bağlıyordu; ASA'nın
   `snmp-server community …` satırı maskelenmeden kaldı.
2. Sınır bir belirteçe çıkarıldı; ASA'nın gerçek IKEv2 söz dizimi **iki**
   belirteç taşıyor (`ikev2 remote-authentication pre-shared-key`) ve ham
   anahtar normalize metinde kaldı. Bunu **simülatör fixture'ı ilk koşumunda**
   yakaladı (FS · S01) — birim testi değil.

Üçüncü bir ders sırada: maskeleme bölüm izlemeden **önce** koşuyor, çünkü ASA'da
`snmp-server community …` aynı zamanda bir bölüm başlığı; ters sırada sır
maskelenmiş metinden çıkıyor ama **bölüm adının içinde** kalmaya devam ediyordu.

Bu üçünün ortak dersi T41'e doğrudan giriyor: **gerçek söz dizimi, tahmin edilen
söz diziminden uzun.** Ve fark her seferinde bir fixture ile görüldü, akıl
yürütmeyle değil.

## 3 · Kapsam kararı — C → dar B → gölge A

Bu ticket'ın açtığı asıl soru şuydu: **bir log satırında sır neye benzer?**
Karar verildi (2026-08-25, koordinatör). **D değil** — üç katman, her birinin
gerekçesi ayrı ve her birinin ürüne giriş biçimi ayrı.

| Sıra | Katman | Ne yapıyor | Gerekçe |
| --- | --- | --- | --- |
| **1** | **C — üretici söz dizimi** | **Maskeliyor** | Yeni bir liste değil, **mevcut desenin taşınması**: `ConfigNormalizer.SecretAssignment()` `Bizigo.Contracts`'a çıkıyor ve iki taraf da aynı desenden okuyor (§9 — ikinci kopya yazma). O desen **iki kez kırıldı ve iki kez sertleşti** (§2); bakım maliyeti zaten ödeniyor |
| **2** | **B — ama tamamı değil** | **Maskeliyor** | Yalnızca **kendini tarif eden ve eskimeyen** biçimler: PEM başlığı, JWT'nin üç parçası, `Authorization: Bearer/Basic <token>`. Biçim sabit, bakım kalemi yok, ve `nginx.access` katalogda olduğu için JWT gerçekten düşüyor |
| **3** | **A — entropi** | **Maskelemiyor — sayıyor** | Gölge mod: *"C+B'nin üstüne kaç belirteç daha maskelerdim"* sorusunun cevabını yayıyor, hiçbir şeye dokunmuyor |

**Kapsam dışı — B'nin ikinci yarısı:** sağlayıcı anahtar kataloğu
(`AKIA…`, `ghp_…`, `xoxb-…`). Gerekçe K2: ürünün alanı ağ cihazı. O liste bulut
için yazıldı, dışarıdan besleniyor ve eskiyor — hizmet etmediğimiz bir alan
için dış dünyaya abone olmak.

### C'nin taşınmasında bir tuzak var — ölçülmezse kapı boş çalışır

Desen **taşınacak**, ama olduğu gibi taşınırsa log metninde **hiçbir şey
bulmaz**, ve bulmadığı için de yeşil görünür.

Sebep deseninin kendi biçiminde yazılı:

```
^(?<prefix>\s*(?:[\w./-]+[\s=]+){0,4}(?:password|psksecret|pre-shared-key|…)[\s=]+…)(?<value>\S.*)$
```

`^`'a bağlı ve anahtar kelimeden önce **en çok dört** belirtece izin veriyor.
Bu, bir **config satırının** biçimi — orada `ikev2 remote-authentication
pre-shared-key …` satır başından itibaren dört belirteç içinde başlıyor. Bir
**syslog satırında** aynı ifadenin önünde öncelik, tarih, host, uygulama etiketi
ve üretici kodu var; üstelik `%ASA-6-113012:` gibi belirteçler `[\w./-]`
sınıfına hiç uymuyor. Yani desen log satırında **eşleşmez**.

Bu, uygulama turunda verilecek bir karar ve ticket'ın kapsamına giriyor:
**anahtar kelime listesi ortak, çapa ortak değil.** İki taraf aynı listeden
okumalı ama config tarafı satır başına, log tarafı satır içine bakmalı — ve
log tarafında serbest bir `.*?` öneki geri izlemeye kapı açar, o yüzden
`ConfigNormalizer`'ın "serbest joker yok" kısıtı orada da geçerli.

**Neden burada yazılı:** ölçülmeseydi bu tam olarak §7'nin sınıfı olurdu.
Kapı koşar, hiçbir şey bulmaz, altın korpusta yanlış pozitif **0** çıkar,
§8'in 7. kriteri **sağlanmış görünür** — ve sır taşıyan fixture'lar
eklenmeseydi (§5) hiçbir şey kırmızı yanmazdı. İki kriter birbirini bu yüzden
tutuyor: 7 "fazla maskeleme yok" der, 3 "gerçekten buluyor" der; **yalnızca
biri sağlanırsa kapı yoktur.**

### A neden gölgede — §4'ün doğrudan sonucu

Ölçülebilen tek hata yönü fazla maskeleme (§4). A tam olarak **maliyeti fazla
maskeleme olan** katman: hash, UUID, session id, base64 gövde — ağ logunda
bunlar bol. O yüzden A ürüne girmeden önce **kendini ölçüyor**. Sayı kabul
edilebilir çıkarsa ikinci turda maskelemeye terfi eder; **o terfi ayrı bir
ticket, T41'in kapsamında değil.**

Bu, bu deponun kendi kuralının tersten uygulanışı: bir şeyin çalıştığı
varsayılmıyor, ama burada ölçülen şey *"çalışıyor mu"* değil *"kaç şeye
dokunurdu"*.

> **Terfi kararı verildi ve terfi ETMEDİ** —
> [T60](../entropi-terfi-karari/index.md). Aşağıdaki *"sayı kabul edilebilir
> çıkarsa ikinci turda terfi eder"* cümlesi örtük olarak **doğru bir eşiğin var
> olduğunu** varsayıyordu; T60 o varsayımı süpürerek ölçtü ve yanlışladı: sahte
> sırların hepsini aday yapan her eşik altın korpusun adreslerini ve imza
> adlarını da aday yapıyor. Sorun eşikte değil **eksende** — entropi
> kodlamayı ölçüyor, hassasiyeti değil. Sayaç kalıyor ve **kalıcı bir ölçüme**
> dönüyor (`ShadowLayerPurpose.PermanentInstrument`).

#### A gölge modda ne yayıyor

İki sayı, ikisi de **prompt başına** ve ikisi de kanıt paketine yazılıyor —
paket saklandığı için (§3 RCA) sayı sonradan da okunabilsin:

| Ad | Ne | Neden bu |
| --- | --- | --- |
| `redaction_shadow_candidates` | Entropi eşiğini geçen ve **C+B'nin maskelemediği** belirteç sayısı | Sorunun tamamı bu: *"ek olarak"*. C+B'nin zaten maskelediğini saymak terfi kararını şişirirdi |
| `redaction_shadow_ratio` | Aynı sayının **değerlendirilen belirteç sayısına** oranı | Ham sayı prompt uzunluğuyla büyür; payda söylenmeden oran okunamaz — bu deponun ayrı bir kavram sayfası olan kuralı |

İki kural:

- **Sıfırken de yazılıyor.** Gizlenen bir sıfır *"henüz ölçülmedi"* ile
  *"ölçüldü, sıfır"* farkını siler — altın kümede aynı karar zaten verildi.
- **Eşik yayınla birlikte kaydediliyor.** Eşiksiz bir sayı, eşiği değiştiren
  bir sonraki turda kendi geçmişiyle karşılaştırılamaz hâle gelir.

Terfi kararı bu iki sayıya bakacak ve **ayrı bir ticket**.

### Neyin arasından seçildi

Karar bu tabloya bakılarak verildi; tablo kayıt olarak duruyor çünkü
katmanlardan biri terfi ya da tenzil edilirse tartılan şey bu:

| # | Yaklaşım | Ne yakalar | Ne kaçırır | Yanlış pozitif kaynağı | Bakım maliyeti |
| --- | --- | --- | --- | --- | --- |
| **A** | **Yüksek entropi** — token başına Shannon entropisi + uzunluk eşiği | Biçimi bilinmeyen anahtarlar, rastgele üretilmiş parolalar, base64 gövdeler | Düşük entropili gerçek sırlar (`admin123`, sözlük parolası); kısa tokenlar | Hash, UUID, session id, base64 payload, `sha256:…` özet — **ağ logunda bunlar bol** | Düşük — eşik ayarı |
| **B** | **Bilinen anahtar biçimleri** — `AKIA…`, `ghp_…`, `xoxb-…`, `-----BEGIN … PRIVATE KEY-----`, JWT üç parçası | Bilinen sağlayıcıların anahtarları; çok yüksek kesinlik | Listede olmayan her şey — ve K2'nin alanı (ağ cihazı) bu listelerin **kör noktası** | Neredeyse yok | Orta — liste eskiyor, kaynağı dış dünya |
| **C** | **Üretici söz dizimi** — `pre-shared-key`, `snmp-server community`, `password`, `psksecret`, `key-string`, `set psksecret ENC` | Bu ürünün gerçekten baktığı alan (K2). `ConfigNormalizer`'ın zaten bildiği liste | Anahtar kelimesiz taşınan sırlar; bilinmeyen üretici söz dizimi | **Ölçüldü, aşağıda** | Yüksek — her yeni üretici bir bakım kalemi |
| **D** | **Üçü birden**, ayrı ayrı gerekçelenmiş katmanlar hâlinde | Üçünün birleşimi | Üçünün kesişimi | Üçünün toplamı | Üçünün toplamı |

Karar iki satırı revize etti. **C'nin bakım maliyeti "yüksek" değil**: liste
zaten var ve zaten bakılıyor — T41 onu taşıyor, çoğaltmıyor. **B ikiye
bölünebiliyor**: eskiyen yarısı (sağlayıcı katalogları) dışarıda kaldı,
eskimeyen yarısı (PEM, JWT, `Authorization`) içeride. Tablo bunu tek bir satır
olarak görüyordu; ayrıştırılabilir olması kararı değiştirdi.

### C'nin yanlış pozitifi ölçüldü — iki kez, ve iki sayı çıktı

Depodaki altın örnek kümesi: `catalog/parsers/**/*.log`, **87 satır**.

| Desen | Eşleşme |
| --- | --- |
| Düz anahtar kelime taraması (`password\|passwd\|secret\|community\|pre-shared\|token\|api[_-]?key\|authorization\|bearer`) | **3** |
| Gerçek atama deseni (`ConfigNormalizer.SecretAssignment()`) | **0** |
| İçinde gerçek sır olan | **0** |

Üç eşleşmenin üçü de aynı FortiGate olayı: `reason="passwd_invalid"` ve
`msg="… because of invalid password"`. Anahtar kelimeden sonra `_` ya da `"`
geliyor; desenin istediği `[\s=]+` gelmiyor, dolayısıyla **atama deseni
tutmuyor.** Yani C'nin bu korpustaki yanlış pozitif oranı **0/87**.

**İki sayı da burada duruyor, çünkü farkları bir bulgu:** *kaba bir yaklaşımın
maliyetini ölçmek, uygulanmış hâlinin maliyetini ölçmez.* İlk ölçüm C'yi hak
ettiğinden üç kat pahalı gösteriyordu ve "yüksek bakım maliyeti" değerlendirmesi
kısmen ona dayanıyordu. Bu, bu deponun §7'de adını koyduğu sınıfın **ölçüm
katmanındaki** karşılığı: ölçtüğün şeyin ölçmek istediğin şey olduğu
varsayılmaz. Sayılardan biri silinirse ders de silinir.

Not: 3 eşleşmenin maskelenmesi ucuz da olmazdı. O satırlarda "password" bir
*hata sebebi*, bir *atama* değil; maskelenselerdi modelin gördüğü şey
`Admin login failed … [gizli]` olur — RCA'nın ihtiyacı olan sebep bilgisi
gider, korunan bir şey olmazdı.

İkinci sonuç aynı ölçümden çıkıyor ve §5'i belirliyor: **bu korpus bir
redaksiyon kapısının ölçüm korpusu olamaz, çünkü içinde sır yok.**

### Seçimi belirleyen ölçütler

1. **K2'nin alanı ne diyor?** Ürün ağ cihazı ve syslog alanına bakıyor. B'nin
   sağlayıcı listeleri bulut için yazıldı → **dışarıda**. C'nin listesi bu alan
   için yazıldı → **birinci katman**. Ama prompt'a giden metin yalnızca ağ
   cihazından gelmiyor — `nginx.access` katalogda, ve JWT oradan düşüyor →
   B'nin eskimeyen yarısı **içeride**.
2. **Kaçırma maliyeti hangi katmanda ödeniyor?** §4 — ve A'nın gölgede
   kalmasının tek sebebi bu.
3. **Bakım kalemi kim?** Ölçüldü ve karar bunu revize etti: C'nin listesi zaten
   bakılan bir liste. Eskiyen tek şey B'nin sağlayıcı kataloğuydu, o da
   kapsam dışı kaldı.
4. **Kapının kendisi ölçülebilir mi?** A'nın eşiği ölçülebilir bir sayı; B ve C
   ikili karar. Üçünü tek kapı yapmak kırmızı yanma koşulunu karıştırırdı —
   katmanların **ayrı** olması bunun için.
5. **`ConfigNormalizer` ile ortak yüzey doğar mı?** C seçildi, dolayısıyla
   **evet ve zorunlu**: desen `Bizigo.Contracts`'a taşınıyor, iki yerde iki
   liste olmuyor (§9).

## 4 · Yanlış pozitiflerin asimetrisi — bu ticket'ın en önemli cümlesi

İki hata yönü **eşit değil** ve ikisi de **görünürlük bakımından** eşit değil:

| | Sırrı kaçırmak | Fazla maskelemek |
| --- | --- | --- |
| Sonucu | Sır prompt'a girer | Bağlam daralır |
| İkincil sonucu | K6'nın çizdiği sınır **prompt'un içinden** delinir | §2 gereği kanıta bağlanamayan cümle sayısı artar |
| **Görülür mü** | **Hayır.** Hata yok, sayaç yok, belirti yok | **Evet.** Atılan cümle sayacı zaten var ve gösteriliyor |
| Ölçülebilir mi | Yalnızca sırrın ne olduğu **önceden biliniyorsa** | Her koşumda, kendiliğinden |

Bu tablo tasarımı tek yöne itiyor: **fazla maskelemek ucuz, çünkü ölçülüyor.**
Bir düzeltme döngüsü var — sayaç yükselirse kapı gevşetilir. Kaçırmanın böyle
bir döngüsü yok; §7'nin adını koyduğu sınıf tam olarak bu — *"hata yok, sayaç
yok, belirti yok."*

Ama bu, "her şeyi maskele" demek **değil**: §2.1'in ölçtüğü şey içeriği kısmanın
halüsinasyonu **artırdığı**. Yani fazla maskeleme de bir maliyet ödetiyor,
sadece **görünür** bir maliyet ödetiyor. Karar, ödenen bedelin görünür olanı
seçmek.

## 5 · Kapının kırmızı yanabildiği nasıl ölçülür

Bu depoda bir bekçi *"kırmızı yanabildiği ölçülmeden"* yazılmış sayılmıyor
(§6). Burada özel bir sorun var: **kapının kırmızı yanması "bir sır bulundu"
demek** — yani ölçümün kendisi depoya bir sır koymayı gerektiriyor gibi
görünüyor.

Gerektirmiyor, ve bu ayrımın kendisi bir kabul kriteri:

- Ölçüm **sahte bir sır** ile yapılır: biçimi gerçek, değeri uydurma.
- Fixture dosyası ne olduğunu **adında söyler** ve gerçek bir sırla
  karışmayacak bir işaret taşır.
- Depoda bugün `gitleaks`/`trufflehog` türü bir sır tarama adımı **yok** —
  arandı, bulunamadı. Yani "yanlışlıkla gerçek sır girmesin" güvencesini
  sağlayan bir dış bekçi yok; fixture disiplininin kendisi tek savunma.

### Korpus FS·S01'den geliyor — ama yalnızca yarısı hazır

Bu ticket'ın en somut bağı burada ve şu ana kadar hiçbir yerde yazılı değildi:
**sır taşıyan fixture üreten bir düzenek zaten var.**

`catalog/simulators/profiller/<cihaz>/sir-dondu.conf` gerçek üretici söz
dizimini taşıyor ve değeri sahte:

```
ikev2 remote-authentication pre-shared-key qX2cV6bN8mK4jH7g
```

`SimulatorFixtureChainTests` (FS · S01) bunu `Beklenti.FarkVarSirYok` ile
koşuyor — enum'ın kendi tanımı iddiayı yazıyor: *"fark çıkmalı ama gizli değer
hiçbir çıktıda görünmemeli."* **T41'in ihtiyacı olan şeklin tamamı bu**:
gerçek söz dizimi + sahte değer + fixture adında yazılı beklenti. Ve zaten bir
kez işe yaradı: `ConfigNormalizer`'ın IKEv2 kırığını yakalayan koşum buydu.

**Hazır olmayan yarı — ve bu ölçüldü.** Simülatör profillerinin `syslog:`
bölümü kendi satırlarını taşımıyor, **altın örnek dosyalarını işaret ediyor**:

```yaml
syslog:
  samples:
    - catalog/parsers/cisco.asa/samples/network.log
    - catalog/parsers/cisco.asa/samples/auth.log
```

Yani sır makinesi **config kolunda**, T41'in kapısı ise **log kolunda** duruyor
ve o kol §3'te ölçüldüğü gibi sırsız. Aradaki boşluk bu ticket'ın işi:

| Kol | Sır taşıyan fixture | Durum |
| --- | --- | --- |
| Config | `profiller/*/sir-dondu.conf` | **var**, ve bir kırık yakaladı |
| Syslog | — | **yok**; altın satırları işaret ediyor, altın satırlarda sır yok |

**Yapılacak:** log kolunda, config kolunun konvansiyonuna birebir uyan bir
fixture kümesi. Aynı üç şart: gerçek üretici söz dizimi, sahte değer, adında
yazılı beklenti. Kümeyi altın satırların yanına koymak **yanlış** olur — altın
satırlar ayrıştırma kapsamının ölçüsü ve `syslog.samples` onları işaret ediyor;
sır taşıyan bir satır oraya girerse simülatör onu tel üzerinden basar.

Bu bağ yazılmasaydı T41 **ölçülemez bir kapıyla** kapanırdı: kapı yeşil
yanardı, çünkü bulacak bir şey yoktu.

## 6 · `SecretRedactor` genişletilecek, kopyalanmayacak

§9 gereği. Bugün orada duran ve **kopyalanırsa kaybolacak** kararlar:

- `MinFragment = 6` — altı karakterin altı maskelenmiyor. Gerekçesi yazılı:
  `https`, `api`, `v1` her mesajda geçiyor ve hepsini maskelemek metni
  okunamaz hâle getirir. **Bu eşik prompt bağlamında yeniden düşünülmeli**
  ama kaldırılırken gerekçesi bilinerek kaldırılmalı — ticket'ın kapsamına
  giren bir soru, gövdesine giren bir varsayım değil.
- **Uzundan kısaya sıra** — önce kısa parçayı maskelersek uzun parça artık
  metinde bulunamaz ve geri kalanı açıkta kalır.
- **Parça parça maskeleme** — URL'in host'u, yolu, sorgu değeri ayrı ayrı,
  çünkü bir mesaj çoğu zaman tamamını değil bir parçasını taşıyor.

Bugünkü tüketicileri — `NotificationDispatcher`, `EmailChannel`,
`HttpNotificationChannel`, `NotificationChannelService`, `ChangeConnectorService`
— hepsi *"bilinen sırrı ver"* biçiminde çağırıyor. Keşif tarafı **ayrı bir
yüzey** olmalı ve bu tüketicilerin çağrı biçimini değiştirmemeli.

## 7 · Kapsam

1. **`SecretAssignment()` deseninin `Bizigo.Contracts`'a taşınması.**
   `ConfigNormalizer` ve keşif tarafı **aynı** listeden okur. İki liste
   doğmaz (§9). Desenin bugünkü XML yorumu — iki kırığın kaydı — taşınırken
   **birlikte gider**; o yorum desenin niye böyle olduğunun tek kaydı.
   Çapa kararı bu maddenin içinde: config satır başına bakar, log satır içine
   (§3'ün tuzak bölümü), ve log tarafında da **serbest joker yok**.
2. **Katman 1 (C):** taşınan desenle log metninde sır keşfi.
3. **Katman 2 (B, dar):** PEM başlığı, JWT'nin üç parçası,
   `Authorization: Bearer/Basic <token>`. Sağlayıcı anahtar katalogları
   **girmiyor**.
4. **Katman 3 (A, gölge):** entropi hesabı **hiçbir şeyi maskelemez**;
   `redaction_shadow_candidates` ve `redaction_shadow_ratio` sayılarını
   eşiğiyle birlikte yayar (§3).
5. Katman 1 ve 2'nin bulduklarının **`SecretRedactor` üzerinden** maskelenmesi
   (ikame tarafı yeniden yazılmıyor).
6. Prompt'a giden yolda kapının **tek geçiş noktası** olması — kanıt paketinden
   prompt'a giden her metin oradan geçer. İki giriş varsa biri unutulur.
7. **Log kolunda sır taşıyan fixture kümesi** (§5): gerçek üretici söz dizimi,
   sahte değer, adında yazılı beklenti — config kolundaki `sir-dondu.conf`
   konvansiyonuna uyarak, ama **altın örneklerin içine değil**.
8. Kapının kırmızı yanabildiğinin o fixture'larla ölçülmesi (§5).
9. **Neyi tanıyamadığının yazılması** (§8).

### Kapsam dışı

- **A'nın maskelemeye terfisi.** Gölge sayıları okunduktan sonra verilecek
  ayrı bir karar ve ayrı bir ticket — [T60](../entropi-terfi-karari/index.md),
  ve karar **terfi etmemek** oldu.
- **Sağlayıcı anahtar katalogları** (`AKIA…`, `ghp_…`, `xoxb-…`) — §3'ün
  gerekçesiyle.
- `masked` / `raw` düzeylerinin açılması — ayrı bir karar, bu ticket'ın çıktısı
  onun **önkoşulu**.
- `catalog/masks/*.yaml`'a dokunmak — o dosya iki dilin ortak sözleşmesi ve
  amacı imza kararlılığı; sır redaksiyonu oraya eklenirse `template_id`
  sessizce değişir.
- **`ConfigNormalizer`'ın davranışını değiştirmek.** Desen taşınıyor,
  davranışı taşınmıyor: config kolunun çıktısı bugünkü hâliyle kalmalı ve
  `SimulatorFixtureChainTests` bunu tutuyor. Taşımanın **tek** sınavı bu —
  ortak desene çıkmak config tarafında hiçbir farka yol açmamalı.

## 8 · Kabul kriterleri

| # | Kriter |
| --- | --- |
| 1 | Prompt'a giden metin **tek** bir kapıdan geçiyor ve bu kapıyı atlayan ikinci bir yol olmadığı bir testle gösteriliyor |
| 2 | **Üç katmanın sırası ve rolü kodda görünür:** C ve B maskeliyor, A yalnızca sayıyor. Bir testle A'nın çıktıyı **değiştirmediği** gösteriliyor — gölge katmanın sessizce maskelemeye başlaması tam olarak bu deponun sessiz-yanlış sınıfı olurdu |
| 3 | Kapı, `sir-dondu` konvansiyonundaki sahte sır fixture'larında sırrı buluyor ve maskeliyor (§5) |
| 4 | Kapı **kırmızı yanabiliyor**: kapı devre dışı bırakıldığında ya da sahte sır biçimi değiştirildiğinde test düşüyor — ölçüldü ve geri alındı, rapora yazıldı |
| 5 | Maskeleme `SecretRedactor` üzerinden yapılıyor; ikinci bir ikame uygulaması yok (§9) |
| 6 | **Desen tek yerde:** `SecretAssignment()` `Bizigo.Contracts`'ta ve `ConfigNormalizer` onu oradan okuyor. `SimulatorFixtureChainTests` taşımadan sonra **aynı sonucu** veriyor — config kolunun davranışı değişmedi |
| 7 | Yanlış pozitif ölçüldü: 87 satırlık altın korpusta kaç satırın maskelendiği sayıldı ve sayı **gerekçesiyle** yazıldı. §3'ün ölçümüne göre beklenen **0/87**; 0 değilse neden 0 olmadığı açıklanmalı |
| 8 | **A'nın gölge sayıları yayılıyor ve sıfırken de yazılıyor**, eşikle birlikte (§3) |
| 9 | **Neyi tanıyamadığı yazılı.** Bir belge bölümü ya da sınıfın XML yorumu, kapının **kapsamadığı** sır sınıflarını açıkça sayıyor — en azından: sağlayıcı anahtar katalogları (bilinçli kapsam dışı), anahtar kelimesiz taşınan sırlar, ve A gölgede olduğu sürece yüksek entropili bilinmeyen biçimler. Bu kriter isteğe bağlı değil: hiçbir redaksiyon kapısı tam değildir ve **tam olduğu iddiası, olmadığı iddiasından tehlikelidir** — çünkü tam sanılan bir kapı, arkasındaki düzeyi (`raw`) gerekçesiz açtırır |
| 10 | Depoya gerçek bir sır girmedi: fixture'lardaki her değer sahte, ve sahte olduğu dosyanın kendisinden okunuyor |
| 11 | `MinFragment = 6` eşiğinin prompt bağlamında geçerli olup olmadığına **karar verildi ve gerekçesi yazıldı** — korunduysa neden, değiştiyse neden |

## 9 · Koordinatöre sorular

### Kapandı

| Soru | Cevap (2026-08-25) |
| --- | --- |
| **§3'ten hangisi?** A / B / C / D | **C → dar B → gölge A.** D değil. Ayrıntısı ve gerekçesi §3'te; C bir taşıma, B ikiye bölündü, A maskelemiyor sayıyor |
| **Tanınamayan bir şey bulunduğunda ne olur — yalnızca yazılır mı, sayaç mı doğar?** | **Sayaç doğuyor**, ve gölge katmanın kendisi o sayaç: `redaction_shadow_candidates` + `redaction_shadow_ratio`. T38'in oranıyla aynı rolde — yüksekse ya kapı dar ya eşik yanlış, ikisi de karar gerektiren bilgi |

### Kapandı (devamı)

| Soru | Cevap (2026-08-26) |
| --- | --- |
| **Kapı bir satırı mı yoksa bir parçayı mı atar?** | **Parça.** Değer maskelenir, satır kalır. Satırı düşürmek RCA'nın ihtiyacı olan bağlamı siler — `reason="passwd_invalid"` satırında zaten değer yok, maskelenseydi korunan bir şey olmaz sebep bilgisi giderdi. **İstisna:** değerin sınırı belirsizse **satır sonuna kadar** maskele, tahmin etme (koordinatör, 2026-08-26). Uygulama §11'de |
| **`tickets-f4/index.md` yazılsın mı?** | **Sahibi başkası** (koordinatör, 2026-08-25) |

## 11 · Uygulama turu — ne yapıldı, ne ölçüldü (2026-08-26)

### Nerede duruyor

| Parça | Yer |
| --- | --- |
| Ortak anahtar kelime listesi + iki çapa | `src/Bizigo.Contracts/Security/SecretPatterns.cs` |
| Kapı (C + B + gölge A) | `src/Bizigo.Contracts/Security/RedactedPrompt.cs` |
| Keşif yolunun ikamesi | `SecretRedactor.RedactExact` — ikame gövdesi ortak (`Apply`) |
| Log kolunda sır taşıyan fixture | `catalog/simulators/profiller/<profil>/sir-tasiyan.log` (4 dosya) |
| Testler | `tests/Bizigo.UnitTests/RedactionGateTests.cs` (17 test) |

### Kararlar ve gerekçeleri

**Kapı ile çıktısı aynı tip (`RedactedPrompt`), yapıcısı `private`.** Kriter
1'in *"kapıyı atlayan ikinci bir yol yok"* iddiası bir çağrı alışkanlığına
değil **derleyiciye** bağlandı: `RedactedPrompt` örneği yalnızca
`RedactedPrompt.Redact`'ten çıkabiliyor. Kapı `string` döndürseydi, unutulduğu
gün hiçbir şey kırılmazdı — §7'nin sınıfı. **Bu, F4'e bir şart koyuyor:**
prompt'u kuran taraf girdisini `string` değil bu tipten almalı, yoksa garanti
yarım kalır.

**Değerin sınırını ayırıcı belirliyor — üç kural** (koordinatör, 2026-08-26).

| Ayırıcı | Sınır |
| --- | --- |
| Tırnak | Kapanış tırnağına kadar |
| `=` / `:` | Satır sonuna kadar |
| **Boşluk** | **Yalnızca ilk belirteç** |

İlk uygulama boşluk ayırıcıda da satır sonuna kadar maskeliyordu ve
*"sınırı belirsizse tahmin etme"* kuralının literal uygulanışıydı. **Düzeltildi,
çünkü boşluk ayırıcıda sınır belirsiz değil:** boşluk ayırıcılı üretici söz
diziminde değer zaten tek belirteç — `snmp-server community <anahtar>`,
`pre-shared-key <anahtar>`, `key-string <anahtar>`. Sınırlamak gerçek bir şey
kaybettirmiyor ve kapıyı doğuran ASA IKEv2 örneğini de kaçırmıyor. `=`/`:`
tarafında sınır gerçekten belirsiz (MikroTik `password=…`, tırnaksız etiketli
biçimler) ve kural orada geçerliliğini koruyor.

Belirleyici gerekçe **asimetri argümanının burada uygulanmaması**: *"fazla
maskeleme ölçülebilir"* demek **bilinmeyen** fazla maskelemeyi sayaçla
keşfetmek demek. `Failed password for admin from 10.1.2.3` kaybı bilinmiyor
değil, **öngörülüyor** — ve öngörülen bir kaybı sayaca havale etmek sayacın
işini yapmıyor. Altın korpustaki 0/87, sayı 0 olduğu için değil **katalogda
sshd olmadığı** için; `nginx.access` zaten içeride, yani katalog o yöne açık.

**Sınır ilk belirtece çekilince ikinci bir kusur açığa çıktı ve kapatıldı.**
Satırın geri kalanı kurtuluyor ama ilk belirteç (`for`) maskelenmeye devam
ediyordu — ve ikame **bütün metinde** çalıştığı için `information` →
`in[gizli]mation` olurdu: korunan hiçbir şey yok, prompt bozuk. Ölçüt tek ve
mekanik: *boşlukla ayrılmış bir değer, içinde küçük harf olmayan en az bir
karakter taşımalı.* Üretici anahtarları rakam, büyük harf ya da ayraç taşıyor
(`pub1`, `S3cret`, `qX2cV6bN8mK4jH7g`); düz sözcükler taşımıyor. Bir sözcük
listesi yazılmadı — bu depo elle tutulan listelerin bekçiyi körleştirdiğini
ölçtü. **Kaçırdığı:** boşlukla ayrılmış, tamamı küçük harf bir parola
(`password correcthorse`). `=`/`:` ayırıcıda ve config kolunda bu ölçüt hiç
çalışmıyor, yani oradaki aynı parola maskeleniyor.

**Şifreleme işareti değer sanılmıyor.** `set psksecret ENC <blob>` satırında
boşluk ayırıcı ilk belirteci `ENC` olarak alırdı; işaret atlanıyor ve
maskelenen şey blob oluyor. Config çapasında bu parça zaten vardı, log
çapasına bu turda girdi.

**`MinFragment = 6` korundu — ama yalnızca bilinen-sır yolunda.** Eşiğin
gerekçesi (`https`, `api`, `v1`) **türetilmiş** parçalarla ilgili: bir webhook
URL'inden çıkan `api` sır değil gürültü. Keşif yolunda türetme yok — değer bir
atamanın sağ tarafı olarak bulundu. ASA'nın dört karakterlik bir SNMP
community'si gerçek bir sır ve altı karakterlik bir taban onu **sessizce**
atlardı. Tek eşik iki soruya birden cevap veremiyordu; yol ikiye ayrıldı,
eşik yerinde kaldı (kriter 11).

**Gölge katman maskelenmiş metin üzerinde sayıyor.** *"C+B'nin üstüne"*
sorusunun tanımı bu; ham metin üzerinde sayılsaydı C+B'nin zaten maskelediği
her değer aday olarak da görünür ve terfi kararını şişirirdi.

### Ölçüldü

| Ölçüm | Sonuç |
| --- | --- |
| `dotnet build` | 17 proje, 0 hata, 0 uyarı |
| `dotnet test tests/Bizigo.UnitTests` | 1006 geçti / 1 düştü / 4 atlandı. Düşen `WikiSourceDigestTests` ve **T41'e ait değil**: `CLAUDE.md` §2 değişikliğinden dolayı 14 vault sayfası damgasız — bu turda başka bir ajanın işi |
| `RedactionGateTests` | 17/17 |
| **Kriter 7 — altın korpusta yanlış pozitif** | **0/87.** Ticket §3 aynı sayıyı *config* çapasıyla ölçmüştü; log çapası yeni bir desen olduğu için ölçüm tekrarlandı. Boşluk ayırıcılı, `=`/`:` ayırıcılı ve karma varyantların **üçü de 0/87** verdi |
| **Kriter 8 — gölge sayıları (altın korpus, 87 satır)** | `maskelenen=0 gölge_aday=59 gölge_oran=0.6146 payda=96 eşik=3.50bit/krk min_uzunluk=20` |
| Gölge sayıları (fixture'lar) | asa-dc-01 `4/0`, fw-ankara-01 `2/0`, rb-sube-07 `2/0`, lb-web-01 `3/1` (maskelenen/gölge_aday) |

**Gölge oranı bu ticket'ın en karar-verdirici sayısı ve beklenenden yüksek
çıktı: 0.61.** Değerlendirilen 96 uzun belirtecin 59'u eşiği geçiyor. Ne
oldukları da ölçüldü — UUID'ler (`ae28f494-5735-51e9-f247-d1d2ce663f4b`),
oturum kimlikleri, ve **FortiGate imza adları**
(`Adobe.Flash.newfunction.Handling.Code.Execution`,
`HTTP.BROWSER_Firefox`). Yani A bugün terfi ettirilseydi maskelenecek şeylerin
başında **saldırı imzasının adı** gelirdi — RCA'nın cümle kurmak için ihtiyaç
duyduğu tam da o. §3'ün *"A neden gölgede"* gerekçesi ölçümle doğrulandı;
terfi ticket'ı bu iki sayıyla açılmalı.

> **Açıldı ve kapandı:** [T60](../entropi-terfi-karari/index.md). Aday kümesi
> mekanik olarak sınıflandırıldı ve dağılım bu paragrafın söylediğinden bir adım
> öte çıktı: 59'un **48'i** `Composite` ve içinin çoğu ASA'nın
> `arayüz:ip/port` üçlüleri — yani terfi ettirilseydi maskelenecek ilk şey
> imzanın adı değil, **kimin kime hangi porttan konuştuğu** olurdu.

### Kırmızı yanabildiği ölçüldü — altı kusur, hepsi geri alındı

Her kusur uygulandıktan sonra **dosyada gerçekten olduğu doğrulandı**, sonra
koşuldu, sonra geri alındı.

| # | Kusur | Düşen test |
| --- | --- | --- |
| 1 | Kapı devre dışı (C+B keşfi kapalı) | **6** test |
| 2 | **§3'ün tuzağı:** log çapası yerine config çapası (`^`'a bağlı) | **5** test |
| 3 | Gölge katman sessizce maskelemeye başladı | **2** test |
| 4 | Fixture'da sahte sır biçimi bozuldu (`pre-shared-key[…]`) | 1 test |
| 5 | `sir-tasiyan.log` bir profilin `syslog.samples`'ına girdi | 1 test |
| 6 | Altın korpusa gerçek bir atama satırı eklendi | 1 test |
| 7 | Boşluk ayırıcı yine satır sonuna kadar maskeliyor | 1 test |
| 8 | Düz sözcük ölçütü kaldırıldı | 1 test |
| 9 | Şifreleme işareti (`ENC`) atlanmıyor | 1 test |

7 ve 8 **aynı** testi düşürüyor — kuralın iki yönü tek yerde duruyor. Ayrı
testlere bölünseydi biri değiştirildiğinde diğerinin hâlâ geçerli olup
olmadığı görünmezdi.

**2 numaralı ölçüm ticket'ın en önemli iddiasını kanıtlıyor:** deseni olduğu
gibi taşımak gerçekten hiçbir şey bulmuyor, ve fixture'lar olmasaydı bu
**yeşil** görünecekti — 0/87 kriteri sağlanmış olarak okunacaktı.

### Yapılmayanlar ve bilinen boşluklar

- **A'nın terfisi yapılmadı** — kapsam dışı, ayrı ticket. **T60 o ticket'ı
  koşturdu ve cevap "terfi etmiyor" çıktı**; gölge sayaç kalıcı bir ölçüm oldu
  ve tek oran yerine mekanik bir sınıf dağılımı yayıyor
  ([T60](../entropi-terfi-karari/index.md)).
- **Sağlayıcı anahtar katalogları girmedi** — bilinçli, K2 gerekçesiyle. Bir
  test bunların maskelenmediğini de yazıyor: kapsam dışı olmak bir *karar*,
  bir eksiklik değil.
- **`cfgattr="psksecret[…]"` biçimi tanınmıyor.** 4 numaralı ölçümde görüldü:
  anahtar kelimeden sonra `[` geliyor, ayırıcı gelmiyor. Gerçek FortiGate bu
  alanda değeri kendi maskeliyor (`psksecret[*]`), o yüzden fixture'a
  konmadı — ama biçim ailesi kapının kör noktası.
- **Boşlukla ayrılmış, tamamı küçük harf bir parola** (`password correcthorse`)
  tanınmıyor — düz sözcük ölçütünün bilinen ve dar bedeli. `=`/`:` ayırıcıda
  ve config kolunda ölçüt hiç çalışmadığı için oradaki aynı parola
  maskeleniyor.
- **Kriter 9 kapının XML yorumunda**, ayrı bir belge bölümü olarak değil:
  tanıyamadıklarının listesi koda yapışık durursa desen değiştiğinde aynı
  ekranda görünüyor.

## 10 · Bu belge yazılırken ne ölçüldü, ne arandı

**Ölçüldü — ticket yazarken:**

- Altın korpus: `catalog/parsers/**/*.log`, 8 dosya, boş ve `#` satırları
  dışlandıktan sonra **87 satır**. Düz anahtar kelime taraması **3** satır
  eşleştirdi, üçünde de sır değeri **yok** (§3).
- Simülatör profillerinin `syslog.samples` alanı **altın örnek dosyalarını
  işaret ediyor**, kendi satırlarını taşımıyor. Yani log kolunda sır taşıyan
  fixture **yok** (§5).
- `catalog/simulators/profiller/asa-dc-01/sir-dondu.conf` gerçek ASA IKEv2 söz
  dizimini sahte değerle taşıyor ve `SimulatorFixtureChainTests`
  `Beklenti.FarkVarSirYok` ile koşuyor — config kolundaki şekil **hazır** (§5).

**Ölçüldü — koordinatör tarafından, karar turunda:**

- Aynı 87 satıra **gerçek atama deseni** (`SecretAssignment()`) koşturuldu:
  **0** eşleşme. Yani C'nin bu korpustaki yanlış pozitif oranı 0/87, benim
  ölçtüğüm 3/87 değil. İki sayı da §3'te duruyor ve farkları kayda geçti.

**Arandı, bulunamadı** (— "aramadım" ile aynı şey değil):

- Depoda entropi hesabı yapan hiçbir kod yok (`entropy`/`shannon` —
  `src`, `tests`, `sidecar`: sıfır eşleşme).
- CI'da sır tarama adımı yok (`gitleaks`, `trufflehog`, `detect-secrets` —
  `.github/`: sıfır eşleşme).
- `SecretRedactor`'ın keşif yapan bir sarmalayıcısı yok; beş dosyadaki yedi
  çağrının hepsi bilinen sırrı parametre olarak veriyor.

**Aranmadı:**

- Sidecar'ın (Python) Drain3 tarafında sır sınıfına bakan bir davranış olup
  olmadığı ayrıca incelenmedi; `bizigo-masks.yaml` okundu ve 13 maskesinin
  hiçbiri sır sınıfı değil, ama sidecar'ın kendi kodu bu ticket için
  taranmadı.
- Dış sır-tanıma kütüphaneleri (sağlayıcı biçim listeleri) karşılaştırılmadı.
  Karar sonrası bu bir eksiklik değil: o listeler §3'te **kapsam dışı** kaldı.
- B'nin içeride kalan yarısının (PEM, JWT, `Authorization`) altın korpusta kaç
  eşleşme ürettiği **ölçülmedi** — beklenen 0 ama ölçülmedi, ve §8'in 7.
  kriteri bunu uygulama turunda istiyor.
