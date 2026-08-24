---
title: Eşleşmeyen kuralın teşhisi — üç eksen
category: skills
tags: [log-analiz, test, yordam, bizigo]
aliases: [üç eksen, alan kapsamı, explain_misses, vendor sözlüğü]
relationships:
  - target: "[[skills/f3-sigma-derleme-kapilari]]"
    type: extends
  - target: "[[concepts/f3-oranin-paydasi]]"
    type: uses
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
sources:
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t32-derleme-tasarimi/index.md
  - docs/epic/f3-yol-haritasi/index.md
source_digest: "sha256-12/v1 docs/epic/f3-yol-haritasi/index.md=27055ee43212 docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t32-derleme-tasarimi/index.md=84daadf9f9fc docs/epic/t39-alan-kapsami/index.md=d05b57cf00ab"
summary: Bir kural sıfır satır döndürdüğünde sebep en az beş farklı şey olabilir ve tabloda hepsi aynı görünür. Üç bağımsız eksen — metin, alan, değer uzayı — birbirini tamamlayarak sebebi ayırıyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.83
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Eşleşmeyen kuralın teşhisi — üç eksen

Soruyu doğuran şey Kapı 3'ün boş kuralları
([[skills/f3-sigma-derleme-kapilari]]): bir Sigma kuralı hiçbir satır
bulmuyorsa sebebi birbirine hiç benzemeyen birkaç şey olabilir ve tabloda hepsi
aynı görünür — **boş kolon**.

Yanlış ayırmanın bedeli iki yönlü: olmayan bir veriyi eşlemeye çalışmak, ya da
var olan bir alanı örneklem eksikliği sanıp geçmek.

## Üç eksen, üç ayrı soru

| Eksen | Araç | Sorduğu | Cevaplayamadığı |
| --- | --- | --- | --- |
| **Metin** | `explain_misses.py` (T30) | Kuralın aradığı dizge örnek dosyalarda var mı; sözcük sınırında mı yoksa daha uzun bir sözcüğün içinde mi | Bilginin adreslenebilir olup olmadığı |
| **Alan** | `bizigo fields coverage` (T39) | O bilgi bir **alan** olarak adreslenebiliyor mu, hangi adla | Kolonun hangi değerleri taşıyabildiği |
| **Değer uzayı** | `bizigo fields values` (T39) | Kolon o değeri **hiç** taşıyabilir mi | Bugünkü örneklemde var mı |

İkisi/üçü ortak hiçbir varsayım paylaşmıyor ve tam bu yüzden birbirlerini
tamamlıyorlar. `Reset-I` iki kez, iki bağımsız yöntemle bulundu: biri örnek
dosyayı gözle okuyarak, öbürü kapsanmamış metin aralıklarını sayarak. Bu, tek
bir aracın "buldum" demesinden başka bir şey.

Buradaki eksen çokluğu teşhise özgü değil; aynı şekil doğrulama tarafında da
üç kez kuruldu (imza sürüklenme sayacı, altın hash vektörü, imaj sürüm
eşlemesi) — genel hâli [[concepts/olcum-capraz-eksen]].

## 1 · Metin ekseni — üç kutu

`absent` (dizge hiç yok) · `substring_only` (yalnızca daha uzun sözcüğün içinde)
· `present` (var).

24 kuralda ölçüldü: `absent 10 · substring_only 1 · present 13`.

**Ayırt edici soru:** *dizge örneklerde hiç yok mu, yoksa yakını mı var?*
Yakını varsa bu bir örneklem boşluğu **değil**, düzeltilebilir bir kural hatası —
ve ikisinin verdiği iş emri zıt.

### Metin ekseninin sistematik sapması

`absent` kutusu bir **üst sınır**: bir eşleme tablosu cihazın sözcüğünü
normalleştiriyorsa, kuralın aradığı normalleştirilmiş değer ham satırda hiç
geçmez ve `absent` görünür.

Ölçülmüş örnek: `fortigate_user_auth_fail` `status: 'failure'` arıyor, ham satır
`status="failed"` yazıyor. Metin ekseni "yok" dedi, kural `failed`'a çevrildi —
ve **düzeltme kuralı bozdu**: `auth_outcome.yaml` ingest sırasında
`failed → failure` çeviriyor, yani kolonda duran değer zaten `failure`'dı ve
`failed` orada **hiçbir zaman duramaz**. Kural baştan doğruydu; geri alındı.

## 2 · Alan ekseni — dört kutu

| Kutu | Ne diyor | Örnek |
| --- | --- | --- |
| **1** | Dosyada var, **hiçbir alana inmemiş** | ASA'nın `Reset-I`'si |
| **2** | İnmiş ama OCSF adına değil, `unmapped`'e | RouterOS zincir adı → `fw_chain` |
| **3a** | Kolon **hiçbir** vendor'da dolmuyor | bugün boş |
| **3b** | Kolon **bu** vendor'da boş, başkasında dolu | nginx'te hedef uç alanları |

**3b ayrı bir kutu olmak zorunda** ve bu, iki aracın **bağımsız olarak** aynı
ihtiyaca varmasıyla doğrulandı: Sigma tarafı aynı olguyu `VENDOR_EMPTY_COLUMNS`
diye dördüncü bir tuzak sınıfı açarak buldu.

**3b bir hata listesi değil, bir karakter listesi.** nginx'in
`src_endpoint_port` / `dst_endpoint_ip` / `dst_endpoint_port` alanlarının boş
olması doğru: erişim logunda hedef uç diye bir şey yok.

### Aracın kendi sessiz yalanı

İlk koşumda dört vendor'un dördünde de kutu 1 **boş** çıktı ve okunuşu
*"parser her şeyi yakalamış"* oluyordu. Doğru değildi: parser'lar ham satırı
`message` alanında saklıyor ve o değer gövdenin **birebir kendisi**; kapsama
sayılınca hiçbir aralık boşta kalmıyor. Ölçüm, ölçmesi istenen şeyin tam tersini
söylüyordu — **yeşil bir sonuçla**.

Düzeltme bir eşik değil **yapı**: içinde başka bir yakalanmış değer geçen alan
**üst hâl** sayılıyor ve kapsama girmiyor (`message`, `event_message`, `msg`,
`request`). Eşik seçilseydi bugünkü veride aynı sonucu verir, yarın kayardı.

Ayrımın Sigma tarafında karşılığı var ve rastlantı değil: üst hâl yalnızca
`contains` ile adreslenebiliyor — yani "alan olarak inmiş" saymak, kuralın
gerçekte yapabildiğini olduğundan iyi göstermek olurdu.

### "Kolon dolu" ile "bilgi satırdan geldi" aynı şey değil

`device_hostname` dört vendor'da da **%100 dolu** görünüyordu. Doğru ve
yanıltıcı: `core.host` boşken normalizasyon kolonu **kaynak anahtarıyla**
dolduruyor. Kolon hiç boş görünmüyor ama içindeki değer cihazın adı değil bizim
ürettiğimiz kimlik.

Ölçülen: `device_hostname` **satırdan gelmiyor** — NGINX 22/24, Fortinet 13/22,
Cisco 1/27, MikroTik 0/14. Sonucu keskin: `hostname|contains: 'localhost'`
kuralı satırların çoğunda `golden-nginx.access` görüyor, cihazın adını değil.

Aynı sayaç sabitleri (`class_uid`, `device_vendor_name`, …) ve dönüştürülmüş
değerleri (`UDP` → `udp`) de yakalıyor.

### Kesişim — alanların ayrı ayrı dolu olması yetmiyor

Ölçüm yüklemleri tek tek arıyordu, **kural onları aynı olayda istiyor**. Sigma
tarafı bu kusuru buldu, alan tarafında da aynısı vardı ve düzeltildi.

| Vendor | Ayrı ayrı dolu, **aynı satırda hiç birlikte olmayan** çift |
| --- | --- |
| **MikroTik** | **12** |
| Cisco | 4 |
| Fortinet | 3 |
| NGINX | yok |

MikroTik'in 12 çifti tek bir sebebin sonucu: `system` parser'ı kimlik
alanlarını, `firewall` parser'ı ağ alanlarını dolduruyor ve **hiçbir satır
ikisini birden taşımıyor.** Yani `activity_name` ile bir port isteyen kural, iki
alan da "dolu" görünse bile eşleşemez.

Sınırı açık: bu **alan** düzeyinde kesişim, **değer** düzeyinde değil. Ölçüm
yalnızca **sıfırın sıfır olduğunu** gösteriyor — hiç birlikte dolmuyorlarsa değer
düzeyinde kesişim de imkânsız.

## 3 · Değer uzayı ekseni — "bugün yok" ile "hiçbir zaman olmayacak"

Bu ölçüm **veriye hiç bakmıyor** ve bakmaması asıl özelliği: örneklemde bir
değerin bulunmaması *"bugün yok"*, şemanın onu üretememesi *"hiçbir zaman
olmayacak"*. İkisi aynı tabloda aynı görünüyor ve **verdikleri iş emri zıt**.

Bir eşleme tablosu, beslediği kolonun değer uzayını **daraltıyor**. `status`
kolonu `outcome`'dan geliyor ve HTTP kodunu `success`/`failure`'a çeviriyor —
yani o kolonda **hiçbir zaman bir sayı durmuyor**. `status|startswith: '5'`
arayan bir kural örneklem düzelse de eşleşemez.

Ölçülen sınıflar (24 kural, 33 dizge): `ERİŞİLEMEZ 0` ("aradım, yok"),
`PARSER BOŞLUĞU 4`, `METİN EKSENİ YANILIYOR 1`, ham metin 5, `unmapped` erişimi
11, uzay açık 8, erişilebilir 5.

Son üç satır önce tek kutudaydı ve o birleşim yanıltıcıydı: *"ham metne
vuruyor"* bir **tasarım tercihi**, *"uzay açık"* bir **iş kalemi**. Ayrıldıktan
sonra çıkan sayı kendi başına bir kapsam göstergesi: **24 kuralın 4'ü ham
gövdeye vuruyor**, yani yapısal alan aramıyor — tam metin taraması indeksten
yararlanmıyor.

**Vendor düzeyinde birleşim yanıltıyor:** `activity_name` MikroTik'te "açık"
görünüyor çünkü `system` parser'ı dolduruyor — ama kural firewall satırlarına
vuruyor. Parser düzeyinde bakılmazsa ölçüm *"açık uzay, söylenemez"* der ve asıl
cevabı gizler.

## Tekrar eden aile: kural, vendor'ın sözlüğünü kullanmıyor

Dört kural, kolonun/vendor'ın **gerçekte ne tuttuğuna** bakılmadan yazılmış:

| Kural | Aranan | Gerçekte | Katman |
| --- | --- | --- | --- |
| `asa_teardown_rst` | `RST` | `Reset-I` / `Reset-O` | vendor'ın sözlüğü |
| `routeros_forward_new` | `action: forward` | zincir adı `fw_chain`'de | vendor'ın sözlüğü |
| `fortigate_user_auth_fail` | `failure` | ham satırda `failed` (kolonda `failure`) | vendor'ın sözlüğü |
| `nginx_5xx_burst` | `status = '5…'` | `success` / `failure` | **kolonun** sözlüğü |

Dördüncüsü farklı bir katman ve ayrı yazılıyor: ilk üçünde dizge *vendor'ın
sözlüğünde* yoktu; burada dizge **kolonun sözlüğünde** yok.

Buradan çıkan katalog kuralı iki yarımlı:

> Bir kuraldaki her sabit dizge, **(a)** o vendor'ın örneğinde geçtiği **ve**
> **(b)** gittiği kolonun o değeri tutabildiği görülerek yazılmalı.

İkinci yarım eşleme tablosuna bakmayı gerektiriyor ve ilk üç örnek onu
göstermiyordu.

## En tehlikeli kutu: yanlış sebeple eşleşen kural

Üçü de dışarıdan *"eşleşmiyor"* diye görünüyordu — **birincisi eşleşiyordu bile
ve o daha kötü.**

`asa_teardown_rst` üretilen SQL'i `raw_data ILIKE '%RST%'` ve ASA örneklerinde
harf duyarsız `rst` geçen tek yerler **"first"** ve **"burst"** sözcüklerinin
içi. `at_least_one` beyanı yazılsaydı kapı yeşil yanar ve bir **yanlış pozitifi
kutsardı**.

> "Eşleşti" ile "doğru sebeple eşleşti" farklı şeyler — ve bu ayrımı
> ClickHouse'un sayısı değil **örnek dosyanın içeriği** veriyor.

Kuralın iki katmanlı kusuru vardı ve ikisi birbirini gizliyordu:

1. **`RST` vendor'ın kullanmadığı bir kısaltma** — ASA `Reset-I`/`Reset-O` yazıyor.
2. **Tekrarlanan YAML anahtarı** — `message|contains` iki kez geçiyordu, YAML
   sonuncuyu alıyor ve `Teardown` koşulu **sessizce düşüyordu**. Depoda bu sınıfın
   bekçisi zaten vardı (`yamllint key-duplicates`) ama Sigma kuralları kapsamda
   **değildi**; eklendi ve eklendiği anda **kırmızı yandı** (`exit=1`) — bekçinin
   gerçek bir kusuru yakaladığı böyle gösterildi.

Bir detection kuralında bu sınıf compose dosyasındakinden **daha pahalı**: kural
yayınlanır, çalışır, hiçbir sayaç artmaz ve daraltılmış hâliyle "çalışıyor"
görünür.

Düzeltmenin kendi dersi: `|all` şart, çünkü **düz bir liste Sigma'da OR'dur** —
iki koşulu listeye almak tekrarlanan anahtarın kardeşi olurdu: metinsel olarak
geçerli, anlamsal olarak sessizce başka bir kural.

Ve düzeltilirken bir sınır da çizildi: `Reset-I` değil `Reset` arandı. Yöne
bağlamak bugün çalışırdı (örneklemde `Reset-I` 2, `Reset-O` 0) ama **kuralın
sormadığı bir şeyi sormak** olurdu.

## Yordamın sonucu ne söylemeli

Kutu ile sonraki adım eşleşiyor:

| Bulgu | İş emri |
| --- | --- |
| `absent` (gerçekten) | **Korpus** kalemi — örnek dosya eklenmeli, kural masum |
| `substring_only` | Kural yanlış sebeple eşleşiyor — **kuralı düzelt** |
| Kutu 1 (alan yok) | **Parser/şema** kalemi; kural ancak `raw_data`'ya uzanarak çalışır |
| Kutu 2 (`unmapped`) | Eşleme kalemi — indekssiz Map erişimi, hız bedeli |
| Kutu 3b | **Karakter**, kalem değil — o vendor'da o alan yok |
| Değer uzayı kapalı | **Kuralı düzeltmek onu erişilemez yapabilir** — önce eşleme tablosuna bak |

Ve `absent` kutusunun paydaya etkisi ayrı bir sayfa konusu:
[[concepts/f3-oranin-paydasi]].

## Kabul edilmiş sınırlar

- **Bu bir keşif aracı, kapı değil.** Kutu 1'de ayraç ve söz dizimi de var;
  "yakalanmamış" bilgi demek değil. Dar bir eleme `Reset-I` gibi tire taşıyan
  bulguları da elerdi.
- **Eşanlamlı tablosu yok — bilinçli.** Hangi `unmapped` anahtarının hangi OCSF
  kolonuna karşılık geldiği iddia edilmiyor; o tabloyu yazmak, ölçümün
  cevaplaması istenen soruyu **ölçümün girdisine** taşımak olurdu. Tek istisna
  yargı gerektirmeyen **biçim** farkı, ve o `[biçim: …]` diye işaretleniyor.
- **Fazla raporlamak, eksik raporlamaktan iyi** — blob kuralının bedeli bu.
- İki aracın **tek bir ayrıştırıcı** paylaşması bilinçli: kural dizgeleri
  `explain_misses.py --json`'dan, alan adı çevirisi pipeline'ın `FIELD_MAP`'inden
  okunuyor. İki ayrıştırıcı yazmak, iki aracın aynı kuralı farklı kolona bağladığı
  günü hazırlamak olurdu.

## Açık kalem

**`Reset-I` yapısal olarak adreslenemiyor.** ASA teardown satırlarında `reason`
boş kalıyor; sonlanma sebebi hiçbir yapısal alana inmiyor. Kural düzeltildi ama
bunu ancak `raw_data`'ya uzanarak yapabiliyor — indekssiz ve kolon
karşılaştırmasının garantisini vermiyor. Kalem *"doğru sebeple eşleşemez"* değil
**"yapısal olarak adreslenemiyor"**: birincisi artık yanlış, ikincisi kalıcı.

## Kaynaklar

- `docs/epic/t39-alan-kapsami/index.md` — üç/dört kutu, kesişim, değer uzayı ölçümü
- `docs/epic/t30-sigma-olcumu/index.md` — on bir tuzak, metin ekseni, 10. tuzak tablosu
- `docs/epic/t32-derleme-tasarimi/index.md` — `asa_teardown_rst`, tekrarlanan anahtar, `|all`
- `docs/epic/f3-yol-haritasi/index.md` — §2 üç kutulu ölçüm, §6 Sigma tuzakları
