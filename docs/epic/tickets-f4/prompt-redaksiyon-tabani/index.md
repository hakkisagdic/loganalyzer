---
title: "T41 — Prompt redaksiyon tabanı: log metninde sır tanıma"
kind: ticket
status: 0
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

## 3 · Kapsam sorusu — cevaplanacak, burada cevaplanmıyor

Bu ticket'ın açtığı asıl soru: **bir log satırında sır neye benzer?**

Üç yaklaşım var, dördüncü seçenek üçünün birleşimi. **Bu ticket seçmiyor** —
seçim koordinatörün.

| # | Yaklaşım | Ne yakalar | Ne kaçırır | Yanlış pozitif kaynağı | Bakım maliyeti |
| --- | --- | --- | --- | --- | --- |
| **A** | **Yüksek entropi** — token başına Shannon entropisi + uzunluk eşiği | Biçimi bilinmeyen anahtarlar, rastgele üretilmiş parolalar, base64 gövdeler | Düşük entropili gerçek sırlar (`admin123`, sözlük parolası); kısa tokenlar | Hash, UUID, session id, base64 payload, `sha256:…` özet — **ağ logunda bunlar bol** | Düşük — eşik ayarı |
| **B** | **Bilinen anahtar biçimleri** — `AKIA…`, `ghp_…`, `xoxb-…`, `-----BEGIN … PRIVATE KEY-----`, JWT üç parçası | Bilinen sağlayıcıların anahtarları; çok yüksek kesinlik | Listede olmayan her şey — ve K2'nin alanı (ağ cihazı) bu listelerin **kör noktası** | Neredeyse yok | Orta — liste eskiyor, kaynağı dış dünya |
| **C** | **Üretici söz dizimi** — `pre-shared-key`, `snmp-server community`, `password`, `psksecret`, `key-string`, `set psksecret ENC` | Bu ürünün gerçekten baktığı alan (K2). `ConfigNormalizer`'ın zaten bildiği liste | Anahtar kelimesiz taşınan sırlar; bilinmeyen üretici söz dizimi | **Ölçüldü, aşağıda** | Yüksek — her yeni üretici bir bakım kalemi |
| **D** | **Üçü birden**, ayrı ayrı gerekçelenmiş katmanlar hâlinde | Üçünün birleşimi | Üçünün kesişimi | Üçünün toplamı | Üçünün toplamı |

### C'nin yanlış pozitifi ölçüldü

Depodaki altın örnek kümesi — `catalog/parsers/**/*.log`, **87 satır** —
anahtar kelime taramasından geçirildi (`password|passwd|secret|community|
pre-shared|token|api[_-]?key|authorization|bearer`):

| Ölçülen | Sayı |
| --- | --- |
| Altın satır | 87 |
| Anahtar kelime eşleşen | **3** |
| İçinde gerçek sır olan | **0** |

Üç eşleşmenin üçü de aynı FortiGate olayı:
`reason="passwd_invalid"` ve `msg="… because of invalid password"`. Yani
**anahtar kelime var, değer yok.** Bu satırlarda "password" bir *hata sebebi*,
bir *atama* değil — ve o satır maskelenirse modelin gördüğü şey
`Admin login failed … [gizli]` olur: RCA'nın ihtiyacı olan sebep bilgisi gider,
korunan bir şey olmaz.

İkinci sonuç aynı ölçümden çıkıyor ve §5'i belirliyor: **bu korpus bir
redaksiyon kapısının ölçüm korpusu olamaz, çünkü içinde sır yok.**

### Seçimi yaparken bakılacak ölçütler

1. **K2'nin alanı ne diyor?** Ürün ağ cihazı ve syslog alanına bakıyor. B'nin
   listeleri bulut sağlayıcı anahtarları için yazıldı; C'nin listesi bu alan
   için yazıldı. Ama prompt'a giden metin yalnızca ağ cihazından gelmiyor —
   `nginx.access` de katalogda.
2. **Kaçırma maliyeti hangi katmanda ödeniyor?** §4.
3. **Bakım kalemi kim?** B'nin listesi dışarıdan besleniyor ve eskiyor; C'nin
   listesi her yeni üretici parser'ıyla büyüyor. A bakım istemiyor ama eşiği bu
   korpusta ölçülmeden seçilemez.
4. **Kapının kendisi ölçülebilir mi?** A'nın eşiği ölçülebilir bir sayı; B ve C
   ikili karar. Karışık bir kapının kırmızı yanma koşulu da karışık olur.
5. **`ConfigNormalizer` ile ortak yüzey doğar mı?** C seçilirse iki yerde iki
   anahtar kelime listesi olmamalı (§9). A veya B seçilirse böyle bir bağ yok.

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

- Ölçüm **sahte bir sır** ile yapılır: biçimi gerçek, değeri uydurma
  (`AKIAIOSFODNN7EXAMPLE` gibi sağlayıcıların kendi belgelerinde kullandığı
  örnek değerler, ya da açıkça `EXAMPLE`/`FAKE` damgalı üretilmiş dizgeler).
- Fixture dosyası ne olduğunu **başlığında söyler** ve gerçek bir sırla
  karışmayacak bir işaret taşır.
- Ölçüm korpusu §3'te ölçüldüğü gibi **altın satırlar olamaz** — orada sır yok.
  Ayrı bir fixture kümesi gerekiyor, ve o küme `ConfigNormalizer`'ın dersini
  tekrarlamamak için **gerçek üretici söz dizimini** taşımalı (simülatör
  fixture'ları S01'de bunu bir kez yaptı ve kırığı orada yakaladı).
- Depoda bugün `gitleaks`/`trufflehog` türü bir sır tarama adımı **yok** —
  arandı, bulunamadı. Yani "yanlışlıkla gerçek sır girmesin" güvencesini
  sağlayan bir dış bekçi yok; fixture disiplininin kendisi tek savunma.

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

1. Log metninde sır **keşfi** — §3'ten seçilen yaklaşımın uygulanması.
2. Keşfedilen parçaların `SecretRedactor` üzerinden maskelenmesi (ikame tarafı
   yeniden yazılmıyor).
3. Prompt'a giden yolda kapının **tek geçiş noktası** olması — kanıt paketinden
   prompt'a giden her metin oradan geçer. İki giriş varsa biri unutulur.
4. Kapının kırmızı yanabildiğinin sahte sırla ölçülmesi (§5).
5. **Neyi tanıyamadığının yazılması** (§8).

### Kapsam dışı

- `masked` / `raw` düzeylerinin açılması — ayrı bir karar, bu ticket'ın çıktısı
  onun **önkoşulu**.
- `catalog/masks/*.yaml`'a dokunmak — o dosya iki dilin ortak sözleşmesi ve
  amacı imza kararlılığı; sır redaksiyonu oraya eklenirse `template_id`
  sessizce değişir.
- `ConfigNormalizer`'ın davranışını değiştirmek. Ortak liste doğarsa
  paylaşılabilir, ama config tarafının çıktısı bu ticket'ın konusu değil.

## 8 · Kabul kriterleri

| # | Kriter |
| --- | --- |
| 1 | Prompt'a giden metin **tek** bir kapıdan geçiyor ve bu kapıyı atlayan ikinci bir yol olmadığı bir testle gösteriliyor |
| 2 | Kapı, sahte sır taşıyan fixture'larda sırrı buluyor ve maskeliyor (§5) |
| 3 | Kapı **kırmızı yanabiliyor**: kapı devre dışı bırakıldığında ya da sahte sır biçimi değiştirildiğinde test düşüyor — ölçüldü ve geri alındı, rapora yazıldı (§6) |
| 4 | Maskeleme `SecretRedactor` üzerinden yapılıyor; ikinci bir ikame uygulaması yok (§9) |
| 5 | Yanlış pozitif ölçüldü: 87 satırlık altın korpusta kaç satırın maskelendiği sayıldı ve sayı **gerekçesiyle** yazıldı. §3'ün ölçümüne göre beklenen taban 0; 0 değilse neden 0 olmadığı açıklanmalı |
| 6 | **Neyi tanıyamadığı yazılı.** Bir belge bölümü ya da sınıfın XML yorumu, kapının **kapsamadığı** sır sınıflarını açıkça sayıyor. Bu kriter isteğe bağlı değil: hiçbir redaksiyon kapısı tam değildir ve **tam olduğu iddiası, olmadığı iddiasından tehlikelidir** — çünkü tam sanılan bir kapı, arkasındaki düzeyi (`raw`) gerekçesiz açtırır |
| 7 | Depoya gerçek bir sır girmedi: fixture'lardaki her değer sahte, ve sahte olduğu dosyanın kendisinden okunuyor |
| 8 | `MinFragment = 6` eşiğinin prompt bağlamında geçerli olup olmadığına **karar verildi ve gerekçesi yazıldı** — korunduysa neden, değiştiyse neden |

## 9 · Koordinatöre sorular

1. **§3'ten hangisi?** A / B / C / D. Bu ticket seçenekleri ve ölçütleri
   yazıyor, seçimi yapmıyor.
2. **Kapı bir satırı mı yoksa bir parçayı mı atar?** §1 *"sır içeren bir satır
   prompt'a girmemeli"* diyor — satırın **tamamı** mı düşer, yoksa sır
   maskelenip satır kalır mı? İkisi farklı şeyler ve §4'ün bağlam maliyeti
   ikisinde farklı. `SecretRedactor`'ın bugünkü davranışı ikincisi.
3. **Tanınamayan bir şey bulunduğunda ne olur?** Yalnızca yazılır mı, yoksa
   `unknown_ratio` gibi bir sayaç mı doğar? T38'de bir oran vardı ve karar
   verilebilir bilgiydi.
4. **`tickets-f4/index.md` (F4 story'si) yazılsın mı?** Diğer ticket kökleri
   (`tickets`, `tickets-f2`, `tickets-f3`, `tickets-fs`) bir story index'i
   taşıyor. Yazmadım: F4'ün ticket bölünmesini ilan etmek bu ticket'ın işi
   değil ve tahminle doldurulacak bir şey.

## 10 · Bu belge yazılırken ne ölçüldü, ne arandı

**Ölçüldü:**

- Altın korpus: `catalog/parsers/**/*.log`, 8 dosya, boş ve `#` satırları
  dışlandıktan sonra **87 satır**. Anahtar kelime taraması **3** satır
  eşleştirdi, üçünde de sır değeri **yok** (§3).

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
- Dış sır-tanıma kütüphaneleri (biçim listeleri) karşılaştırılmadı — §3'ün B
  seçeneği seçilirse yapılacak iş.
