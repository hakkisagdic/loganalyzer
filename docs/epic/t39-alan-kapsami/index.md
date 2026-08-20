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

## Açık kalem

**`Reset-I` bir parser kalemi.** ASA teardown satırlarında `reason` boş kalıyor
(`reason` yalnızca 3 satırda dolu, hepsi AAA mesajı). Gerçek sonlanma sebebi
hiçbir alana inmiyor, dolayısıyla o bilgiye vuran her kural ham metne uzanmak
zorunda. Düzeltilmeden `asa_teardown_rst` doğru sebeple eşleşemez.
