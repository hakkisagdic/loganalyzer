---
kind: spec
title: "Alan kapsamı — örnek dosyadan events_ocsf'e ne iniyor"
---

# Alan kapsamı ölçümü (T39)

`bizigo fields coverage`. Sorusu tek: **altın örnek satırının taşıdığı bilginin
ne kadarı `events_ocsf`'te bir ALAN olarak sorgulanabiliyor.**

Soruyu doğuran şey Kapı 3'ün boş kuralları. Bir Sigma kuralı hiçbir satır
bulmuyorsa sebebi birden çok, birbirine hiç benzemeyen şey olabilir ve tabloda
hepsi aynı görünür: **boş kolon**. Yanlış ayırmanın bedeli iki yönlü — olmayan
bir veriyi eşlemeye çalışmak, ya da var olan bir alanı örneklem eksikliği sanıp
geçmek.

## Üç kutu

| Kutu | Ne diyor | Örnek |
| --- | --- | --- |
| **1** | Dosyada var, **hiçbir alana inmemiş** | ASA'nın `Reset-I`'si |
| **2** | İnmiş ama OCSF adına değil **`unmapped`'e** | RouterOS zincir adı → `fw_chain` |
| **3a** | Kolon **hiçbir** vendor'da dolmuyor | — (bugün boş) |
| **3b** | Kolon **bu** vendor'da boş, başkasında dolu | nginx'te hedef uç alanları |

**3b ayrı bir kutu olmak zorunda.** `activity_name` FortiGate'te dolu,
RouterOS'ta hep boş. Küresel bir "eksik alan" listesi bunu ifade edemiyor ve iki
farklı durum için de yanlış iş yaptırıyor. Sigma tarafı aynı ihtiyaca bağımsız
olarak `VENDOR_EMPTY_COLUMNS` diye dördüncü bir tuzak sınıfı açarak vardı.

**3b bir hata listesi değil, bir karakter listesi.** nginx'in
`src_endpoint_port`, `dst_endpoint_ip`, `dst_endpoint_port` alanlarının boş
olması doğru: erişim logunda hedef uç diye bir şey yok.

## Ölçülen (katalog yarısı, ClickHouse'suz)

87 örnek satır, 4 vendor. **Kutu 3a boş** — hiçbir OCSF alanı bütün vendor'larda
boş değil.

| Vendor | Örnek satır | Kutu 2 (satırdan gelip `unmapped`'e inen anahtar) | Kutu 3b |
| --- | --- | --- | --- |
| Cisco | 27 | 44 | — |
| Fortinet | 22 | 96 | — |
| MikroTik | 14 | 24 | — |
| NGINX | 24 | 14 | `src_endpoint_port`, `dst_endpoint_ip`, `dst_endpoint_port` |

## Ölçülen (ClickHouse yarısı) — çapraz kontrol temiz

```
Cisco      tuttu (7426 satır, 71 unmapped anahtarı)
Fortinet   tuttu (78801 satır, 118 unmapped anahtarı)
MikroTik   tuttu (22343 satır, 39 unmapped anahtarı)
NGINX      tuttu (11430 satır, 29 unmapped anahtarı)
Çapraz kontrol temiz: kataloğun doldurabildiği her alan events_ocsf'te de dolu.
```

⚠️ İki tablodaki anahtar sayıları **aynı şeyi saymıyor**: katalog sütunu
yalnızca *satırdan gelip OCSF kolonuna inmemiş* anahtarları, ClickHouse sütunu
`unmapped`'teki **bütün** anahtarları sayıyor (defter kayıtları ve değeri bir
kolona da inen anahtarlar dahil). Yan yana okunurken karıştırılmamalı.

Karşılaştırma **varlık** düzeyinde, oran düzeyinde değil: tohumlama Zipf
ağırlıklı, yani aynı alanın veritabanında bambaşka bir oranla dolu olması arıza
değil. Arıza olan tek şey, katalogda dolan bir alanın veritabanında **hiç**
dolmaması — yükleyici her satırı en az bir kez yazdığı için o fark ancak yazma
ya da görünüm yolunda bir kayıptan gelebilir.

## Sigma tarafının üç kutusuyla eşleşme

İki araç **farklı eksende** bakıyor ve tam da bu yüzden birbirini tamamlıyorlar:

- `explain_misses.py` (T30) — **metin ekseni**: kuralın aradığı dizge örnek
dosyalarda var mı, sözcük sınırında mı yoksa daha uzun bir sözcüğün içinde mi.
- `fields coverage` (T39) — **alan ekseni**: o bilgi bir alan olarak
adreslenebiliyor mu, hangi adla.

24 kural üzerinde ölçülen: `absent 10 · substring_only 1 · present 13`, ve
kapsam oranı `%25` değil **`%43`** (payda `absent` düşülünce).

| Sigma kutusu | Sayı | Alan kapsamının söylediği |
| --- | --- | --- |
| `absent` | 10 | **Hiçbir şey — ve söylememesi doğru.** Dizge dosyada yoksa alan da olamaz. Bu on kural için kutu 1/2/3'ün sessiz kalması beklenen davranış; alan tarafında bir kalem yok, örneklem tarafında var. |
| `present` | 13 | **Asıl kesişim.** Metin var, soru adreslenebilirlik. Kutu 3a/3b: kuralın vurduğu kolon boş. Kutu 2: bilgi geldi, OCSF adıyla değil (`fw_chain`). Kutu 1: hiç alan olmamış. Yani alan kapsamı, `present` kutusunun **içini** açıyor. |
| `substring_only` | 1 | **Sebebini söylüyor.** `asa_teardown_rst` ham metne (`ILIKE '%RST%'`) uzanmak zorunda kalmış, çünkü gerçek işaret `Reset-I` hiçbir alana inmiyor — kutu 1'de 2 satır. Kuralın yanlış sebeple eşleşmesi bir yazım hatası değil, **alan eksikliğinin belirtisi**. |

**Kısacası:** Sigma tarafı "bu kural neden eşleşmedi" sorusunu örneklem
ekseninde kapatıyor; alan kapsamı, kapanmayan `present` kutusunu üçe bölüyor ve
her biri farklı bir iş emri veriyor.

### İki bağımsız yolun aynı bulguya varması

`Reset-I` iki kez, iki yöntemle bulundu: biri örnek dosyayı gözle okuyarak
(`RST` yalnızca `first`/`burst` içinde geçiyor), öbürü kapsanmamış metin
aralıklarını sayarak. Yöntemler ortak hiçbir varsayım paylaşmıyor. Bu, tek bir
aracın "buldum" demesinden başka bir şey.

## Aracın kendi sessiz yalanı — ve düzeltmenin biçimi

**İlk koşumda dört vendor'un dördünde de kutu 1 boş çıktı.** Okunuşu şuydu:
*"parser her şeyi yakalamış."* Doğru değildi.

Sebep: parser'lar ham satırı `message` alanında saklıyor ve o değer gövdenin
**birebir kendisi**. Kapsama sayılınca gövdede hiçbir aralık boşta kalmıyor ve
ölçüm, ölçmesi istenen şeyin tam tersini söylüyor — **yeşil bir sonuçla**.

Düzeltme bir eşik değil, **yapı**: içinde başka bir yakalanmış değer geçen alan
**üst hâl** sayılıyor ve kapsama girmiyor. `message`, `event_message`, `msg`,
`request` — hepsi bu sınıftan. Eşik seçilseydi bugünkü veride aynı sonucu verir,
yarın kayardı.

Ayrımın Sigma tarafında karşılığı var ve rastlantı değil: üst hâl yalnızca
`contains` ile adreslenebiliyor. Yani "alan olarak inmiş" saymak, kuralın
gerçekte yapabildiğini olduğundan iyi göstermek olurdu.

**Ölçüldü:** blob kuralı kaldırılınca iki kutu-1 testi de düşüyor; geri alındı.

## Kesişim — ölçümün kendi kusuruydu, ölçüldü ve kapatıldı

Sigma tarafı `present` kutusunda bir kusur buldu: **ölçüm yüklemleri tek tek
arıyor, kural onları aynı olayda istiyor.** Aynı soru alan ekseninde de
geçerliydi ve cevap **evet, aynı kusur bizde de vardı**: `Populated` sayacı her
alanı bağımsız sayıyordu. İki alan ayrı ayrı %100 dolu görünüp aynı satırda hiç
birlikte olmayabilir.

Artık ölçülüyor. Sonuç:

| Vendor | Ayrı ayrı dolu ama **aynı satırda hiç birlikte olmayan** çiftler |
| --- | --- |
| **MikroTik** | **12 çift.** `activity_name` hiçbir ağ alanıyla birlikte dolmuyor (`+connection_info_protocol_name`, `+dst_endpoint_ip`, `+dst_endpoint_port`, `+src_endpoint_port`); `actor_user_name` de aynı dörtlüyle; `status` ağ alanlarıyla |
| **Fortinet** | 3 çift: `status` + (`connection_info_protocol_name`, `src_endpoint_port`, `dst_endpoint_port`) |
| **Cisco** | 4 çift |
| **NGINX** | yok |

MikroTik'in 12 çifti tek bir sebebin sonucu: `system` parser'ı kimlik alanlarını,
`firewall` parser'ı ağ alanlarını dolduruyor ve **hiçbir satır ikisini birden
taşımıyor**. Yani `activity_name` ile bir port isteyen kural, iki alan da "dolu"
görünse bile eşleşemez. Bu, `MissingIn` listesinin satır düzeyindeki kardeşi.

**Sınırı açık:** bu *alan* düzeyinde kesişim, *değer* düzeyinde değil. "Aynı
satırda ikisi de dolu" ile "aynı satırda ikisi de kuralın aradığı değeri
taşıyor" farklı sorular; ikincisi kural değerlendirmesidir ve ürünün SQL yolunda
yapılıyor — üçüncü bir değerlendirici yazmak §9'un yasakladığı şey. Buradaki
ölçüm yalnızca **sıfırın sıfır olduğunu** gösteriyor: hiç birlikte dolmuyorlarsa
değer düzeyinde kesişim de imkânsız.

## "Kolon dolu" ile "bilgi satırdan geldi" aynı şey değil

`device_hostname` dört vendor'da da **%100 dolu** görünüyordu. Doğru ve
yanıltıcı: `EventNormalizer`, `core.host` boşken kolonu **kaynak anahtarıyla**
dolduruyor (`Host = … : source.Raw.SourceKey`). Kolon hiçbir zaman boş
görünmüyor, ama içindeki değer cihazın adı değil bizim ürettiğimiz kimlik.

Artık ayrıca sayılıyor — **değeri ham satırda geçmeyen** satırlar:

| Vendor | `device_hostname` satırdan gelmiyor |
| --- | --- |
| NGINX | **22/24** |
| Fortinet | 13/22 |
| MikroTik | 0/14 |
| Cisco | 1/27 |

Aynı sayaç sabitleri de yakalıyor (`class_uid`, `device_vendor_name`,
`metadata_version`, `raw_ref` — hepsi 100%) ve dönüştürülmüş değerleri de
(`connection_info_protocol_name`: `UDP` → `udp`, satırda o hâliyle yok).
Üçünü ayırmak `fields values`'ın işi: orada sabitler `KAPALI: … ← sabit` diye
görünüyor.

### `core.host` çelişkisi çözüldü — üçüncü bir açıklamayla

Koordinatörün verdiği iki ihtimal de doğru değil:

| İddia | Ölçülen |
| --- | --- |
| "Combined örneklerimiz vhost taşımıyor" | **Yanlış.** `combined.log`'un 14 satırının **2'si** `lessons.example.com` ile başlıyor |
| "6'nın ölçümü bir dosyayı atlıyor" | Hayır |
| "Parser testi sentetik girdi kullanıyor" | Hayır — `combined.yaml:98`'deki girdi, örnek dosyadaki satırın **birebir kendisi** |

Doğru açıklama üçüncüsü: **örnekler vhost taşıyor ama yalnızca 2/14 satırda**,
ve kalan satırlarda `device_hostname` boş görünmüyor çünkü geri düşüş onu
dolduruyor. Alan kapsamı ölçümü bu sayıyı bağımsız olarak üretti — 24 nginx
satırının 22'sinde `device_hostname` satırdan gelmiyor, yani **2'sinde geliyor**.

Sonucu `nginx_dns_rebind` için keskin: `hostname|contains: 'localhost'` kuralı
`device_hostname`'e vurduğunda satırların çoğunda `golden-nginx.access` görüyor —
cihazın adını değil. Kural bu hâliyle vhost arayamaz.

## Kabul edilmiş sınırlar

- **Kutu 1'de ayraç ve söz dizimi de var.** "Yakalanmamış" bilgi demek değil.
Liste taranarak *veriye benzeyen* parçalar aranmalı. Araç bu ayrımı yapmıyor:
yapabilmesi için neyin veri olduğunu bilmesi gerekirdi — sorunun kendisi bu.
Bu bir **keşif** aracı, kapı değil, ve dar bir eleme `Reset-I` gibi tire taşıyan
bulguları da elerdi.
- **Blob kuralının bedeli fazla raporlamak.** İki alanın değeri tesadüfen iç içe
geçerse (FortiGate'te `eventtime` içinde bir sayı) üstteki kapsanmamış görünüyor.
Fazla raporlamak, eksik raporlamaktan iyi.
- **Eşanlamlı tablosu yok — bilinçli.** Hangi `unmapped` anahtarının hangi OCSF
kolonuna karşılık geldiği iddia edilmiyor; o tabloyu yazmak, ölçümün cevaplaması
istenen soruyu ölçümün girdisine taşımak olurdu. Kutular yan yana basılıyor,
eşleştirmeyi okuyan yapıyor. Tek istisna yargı gerektirmeyen **biçim** farkı:
`proto_token=UDP` ↔ `connection_info_protocol_name=udp` birebir tespit edilebiliyor
ve `[biçim: …]` diye işaretleniyor — cevap "kayıp" değil "dönüştürülmüş".

## Sürüklenemeyen iki liste

- **Görünümün kolonları göç dosyasından okunuyor** (`OcsfViewSchema`). Elle
yazılsaydı görünüme eklenen bir kolon hiç sorulmaz ve eksik tablo tam görünürdü —
`Produces` kapısının 16 ucu görmemesiyle aynı sınıf. İkinci bir `events_ocsf`
tanımı bulunursa araç **duruyor**; dosya adı sırasına bakarak hangisinin geçerli
olduğunu tahmin etmiyor.
- **`EventFieldKinds`** "dolu değil"in tek tanımı: bir tablo, iki renderer (C#
koşulu + SQL ifadesi). İki taraf ayrışsaydı fark *"yazma yolunda kayıp"* diye
raporlanırdı — araç kendi kusurunu ürünün kusuru gibi gösterirdi. `Unknown()`
bekçisi `EventWriter`'ın yazdığı her kolonun tanımlı olduğunu sınıyor.

## Dördüncü ölçüm — kolonun taşıyabildiği değerler (`fields values`)

Üç kutu "bilgi alan oldu mu" diye soruyor. Bu ölçüm bir adım öte gidiyor:
**alan oldu ama hangi değerleri taşıyabiliyor.**

Bir eşleme tablosu, beslediği kolonun değer uzayını **daraltıyor**. `status`
kolonu `outcome`'dan geliyor ve `http_status_outcome.yaml` HTTP kodunu
`success`/`failure`'a çeviriyor — yani o kolonda **hiçbir zaman bir sayı
durmuyor**. `status|startswith: '5'` arayan bir kural, örneklem düzelse de
eşleşemez.

Ölçüm **veriye hiç bakmıyor** ve bakmaması asıl özelliği: örneklemde bir
değerin bulunmaması *"bugün yok"*, şemanın onu üretememesi *"hiçbir zaman
olmayacak"*. İkisi aynı tabloda aynı görünüyor ve verdikleri iş emri zıt.

### Ölçülen değer uzayları

Dört vendor için `status` **kapalı** ve yalnızca `failure` · `success`
taşıyor. `class_uid`, `activity_id`, `severity_id`,
`connection_info_protocol_name`, `device_vendor_name`, `metadata_product_name`
de kapalı. `activity_name`, `actor_user_name`, `device_hostname` ve uç noktalar
**açık** — cihaz ne yazarsa.

### Kural birleştirmesi — 24 kural, 33 dizge

Kuralları **okumuyor**: `explain_misses.py --json` zaten `alan|operatör = değer`
üçlülerini çıkarıyor, bu taraf onu tüketiyor. Alan adı çevirisi de
`bizigo_pipeline.py`'nin `FIELD_MAP`'inden okunuyor. İki ayrıştırıcı yazmak,
iki aracın aynı kuralı farklı kolona bağladığı günü hazırlamak olurdu.

| Sınıf | Sayı | Anlamı |
| --- | --- | --- |
| **ERİŞİLEMEZ** | **0** | Aradım, yok — bugünkü korpusta hiçbir kural, gittiği kolonun üretemeyeceği bir değer aramıyor |
| **PARSER BOŞLUĞU** | 4 | Vendor'da açık ama bazı parser'lar kolonu hiç doldurmuyor |
| **METİN EKSENİ YANILIYOR** | 1 | Ham satırda yok ama kolonda **var** |
| ham metin | 5 | `raw_data ILIKE …` — değer uzayı sorusu anlamsız, ama indeks kullanılmıyor |
| `unmapped` erişimi | 11 | `unmapped['…']` — alan olarak adreslenmiş, yine indekssiz |
| uzay açık | 8 | Şema bir şey demiyor |
| erişilebilir | 5 | — |

**Son üç satır önce tek kutudaydı ("söylenemez, 24") ve o birleşim yanıltıcıydı:**
"ham metne vuruyor" bir **tasarım tercihi**, "uzay açık" bir **iş kalemi**.
Ayrıldıktan sonra çıkan sayı kendi başına bir kapsam göstergesi: **24 kuralın
4'ü** ham gövdeye vuruyor, yani yapısal alan aramıyor. Tam metin taraması
indeksten yararlanmıyor ve maliyet kural sayısıyla çarpılıyor.

### Parser boşluğu — vendor birleşiminde kaybolan fark

```
routeros_drop_input.yml  [MikroTik]  action = 'drop'
    `activity_name` bu vendor'da açık ama mikrotik.routeros.firewall onu HİÇ doldurmuyor.

nginx_dns_rebind.yml     [NGINX]     hostname|contains = 'localhost' (+2 dizge)
    `device_hostname` açık ama nginx.access.json onu HİÇ doldurmuyor.
```

Vendor düzeyinde birleşim yanıltıyordu: `activity_name` MikroTik'te "açık"
görünüyor çünkü `routeros.system` dolduruyor — ama kural firewall satırlarına
vuruyor. Liste olmasaydı ölçüm *"açık uzay, söylenemez"* der ve asıl cevabı
gizlerdi. nginx'inki **yeni bir bulgu**: JSON erişim logunda sanal sunucu adı
yok.

### Metin ekseni yanılıyor — ters yön

```
fortigate_user_auth_fail.yml  [Fortinet]  status = 'failure'
    `status` kapalı uzayında `failure` VAR (failure · success) —
    ham satırda geçmemesi önemli değil, auth_outcome.yaml `failed → failure` çeviriyor.
```

`explain_misses.py` bu dizgeyi **`absent`** kutusuna koymuştu ve gerekçesi
*"kural vendor'ın sözlüğünü değil kendi sözlüğünü kullanıyor"*du. Alan ekseninden
bakınca tersi görünüyor: **ürün zaten çeviriyor**, kolonda gerçekten `failure`
duruyor, kural doğru.

**Bu, metin ekseninin sistematik bir sapması:** bir eşleme tablosu cihazın
sözcüğünü normalleştiriyorsa, kuralın aradığı normalleştirilmiş değer ham
satırda hiç geçmez ve `absent` görünür. `absent` kutusu bu yüzden bir **üst
sınır**; her elemanı örneklem boşluğu değil.

### Sapmanın niceliği — paydayı ne kadar oynatıyor

```
### `absent` KUTUSUNUN DÜZELTMESİ — 10 kuraldan 1'i çıkıyor
Tamamen çıkan   : fortigate_user_auth_fail.yml
Kısmen açıklanan: (yok)
```

Kapsam oranının paydası `absent` düşülerek kuruluyor (24 − 10 = 14). Bu
düzeltmeden sonra payda **15**. Oran yeniden hesaplanmalı.

Hesap **kural düzeyinde**, çünkü metin ekseninin kutusu da kural düzeyinde: bir
kuralın **bütün** `absent` dizgeleri normalleştirmeyle açıklanıyorsa kural
kutudan tamamen çıkar; bir kısmı açıklanıyorsa çıkmaz ama kalemi vardır.

### ⚠️ Düzeltmenin kendisi kuralı kırıyor

`failure` → `failed` değişikliği kuralı **erişilemez** yapıyor. Ölçüldü:

```
fortigate_user_auth_fail.yml (DÜZELTME SONRASI) [Fortinet] status = 'failed'
  `status` kapalı bir değer uzayı taşıyor (failure · success) ve `` ile `failed`
  oradan üretilemiyor. Örneklem düzelse de bu kural eşleşmez.
```

`auth_outcome.yaml`'da `failed` bir **anahtar**, bir çıktı değil: tablo
`failed → failure` çeviriyor. Yani kolonda hiçbir zaman `failed` durmuyor.
Düzeltme, çalışan bir kuralı hiç eşleşmeyen bir kurala çevirir.

## Açık kalem

**`Reset-I` yapısal olarak adreslenemiyor.** ASA teardown satırlarında `reason`
boş kalıyor (`reason` yalnızca 3 satırda dolu, hepsi AAA mesajı). Sonlanma
sebebi hiçbir yapısal alana inmiyor.

Kuralın kendisi düzeltildi — `Teardown` **AND** `Reset` artık doğru satırı
buluyor ve `first`/`burst` tesadüfü kalktı. Ama bunu ancak `raw_data`'ya
uzanarak yapabiliyor: tam metin taraması, indekssiz, ve kolon karşılaştırmasının
verdiği garantiyi vermiyor. Kalem *"doğru sebeple eşleşemez"* değil
**"yapısal olarak adreslenemiyor"** — birincisi artık yanlış, ikincisi kalıcı.
