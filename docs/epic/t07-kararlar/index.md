---
kind: spec
title: "T07 — normalizasyonda alınan kararlar"
---

# T07 — normalizasyonda alınan kararlar

> Bu belge geriye dönük yazıldı: kaynağı kod, commit geçmişi ve F1 kapanışı.
> Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan gerekçeler
> kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen alternatifler
> kayıtta yok.

Ticket: [T07 — Normalizasyon: `core` → OCSF/OTel](../tickets/normalizasyon/index.md) ·
Yöneten kararlar: [K8, K17, K29, K30](../mimari-kararlar/index.md)

**Numara notu:** bu ticket **T07**. Koordinatörün brief'i "normalizasyon (T06)"
diyordu; T06 dispatcher ve envanter. Ticket başlığı, `tickets/index.md`
tablosu ve README üçü de T07 diyor.

## 1 · Ticket ne yaptı

`ParsedEvent` → `LogEvent` çevrimi, `core` alan kümesi, OCSF/OTel türetmesinin
**yeri**, değer eşleme tabloları, IP ve zaman normalizasyonu.

## 2 · Koddan okunan kararlar

### 2.1 Tek gerçek `core`; OCSF ve OTel türetiliyor

`EventNormalizer`'ın sınıf yorumu ve `0003_ocsf_otel_views.sql`'in başlığı aynı
gerekçeyi iki yerden yazıyor. İkisini de materyalize etmek depolamayı ~2 katına,
mapping bakımını iki katına çıkarırdı (K8). Saklanan tek istisna
`ocsf_class_uid` ve `ocsf_activity_id` — "filtrelemede ucuz ve sık".

Türetmenin **nerede** yapılacağı ayrı bir karar ve gerekçesi SQL dosyasının
başında duruyor: API katmanında kalsaydı yalnızca bizim uçlarımız OCSF/OTel
şeklini görürdü. Oysa F3'te Sigma kuralları ClickHouse SQL'ine derleniyor ve
OCSF alan adlarına vuruyor; doğrudan SQL konuşan araçlar da aynı şekli görmek
zorunda (K30).

Diğer OCSF/OTel değerleri `attrs`'a `ocsf.` / `otel.` önekleriyle giriyor —
yorumun yazdığı sonuç: yeni bir alan eklemek **şema göçü değil, YAML
değişikliği**.

### 2.2 Görünümlere kapsam filtresi gömülmedi

SQL başlığı: görünümler `events` tablosunun **şeklini** değiştirir, yetkisini
değil. `owner_group` kolonu aynen taşınıyor, filtreyi `IScopedQuery` uyguluyor.
Görünüme filtre gömmek kapsamı iki yerde tanımlamak olurdu (K17).
`Gorunumler_owner_group_kolonunu_tasiyor` bunu sabitliyor.

### 2.3 `raw_ref` = arşiv ön eki — reddedilen alternatifler **kayıtta var**

Bu, bu turda okuduğum ticket'lar içinde reddedilen seçeneklerin koda yazıldığı
tek yer. `RawRefFor`'un özet yorumu iki alternatifi de tartıyor:

| Alternatif | Neden alınmadı |
| --- | --- |
| Ayrı bir `raw_index` tablosu | O(1) okuma verirdi ama hem yükleyicinin hem replay'in bakması gereken **ikinci bir gerçek kaynak** doğururdu; sessizce sürüklenmesi, manifest'in önlemek için var olduğu hata sınıfının aynısı |
| Yüklemeyi olay yazımından önce yapmak | İki yolu birbirine bağlar ve ClickHouse yazımını S3 gecikmesine tabi kılardı |

Seçilen yolun gerekçesi: ön ek yazma anında **hesaplanabiliyor** (grup, saat,
kaynak sınıfı biliniyor), manifest sorgusunun anahtarıyla birebir örtüşüyor, tek
gerçek kaynak arşivin kendisi kalıyor. Bedeli tek kaydı okumak için nesnenin
açılması — replay zaten tamamını okuduğu için ona ek maliyet yok (K29).

### 2.4 Zaman: değer ve kaynağı birlikte dönüyor

`ResolveTimestamp` üçlü zincir: parser'ın çözdüğü damga → cihazın/collector'ın
gözlem damgası → ingest anı. Yorumun gerekçesi: satır **zamansız kalmasın**;
yanlış zamandansa "yaklaşık doğru" zaman daha kullanışlı ve `ts` bölümleme
anahtarı.

İkinci karar daha ince ve yorumda yazılı: değer ve **nereden geldiği** tek
metottan birlikte dönüyor, "çünkü ayrı hesaplanırlarsa bir gün ayrışırlar ve
ayrıştıkları fark edilmez."

Ama T07 o kaynağı **hiçbir yere yazmıyordu** — bkz. §4.2.

### 2.5 `attrs` içine ne giriyor, ve neden tek anahtarda

Üç ek anahtarın üçünün de gerekçesi yazılı:

| Anahtar | Gerekçe |
| --- | --- |
| `bizigo.unassigned_source_key` | Envanterde olmayan kaynak sorguda görünür olmalı; `_unassigned` grubu tek başına "neden" sorusunu cevaplamıyor |
| `bizigo.tags` | Parser'ın satır hakkında **söylediği** şey kayboluyordu: `cisco.asa` tarih taşımayan satırı `_asa_no_timestamp` ile işaretliyor ama etiket ClickHouse'a ulaşmadan düşüyordu |
| `bizigo.parse_issues` | `parse_status=partial` tek başına "bir şey eksik" diyor ama hangi adımın neden takıldığını söylemiyor; sebepsiz bir `partial`, olay detayında (T16) cevaplanamayan bir soru bırakıyor |

İkisi de **tek anahtarda birleştiriliyor**, öğe başına anahtar açılmıyor, ve
gerekçesi ikisinde de aynı: `mapKeys` bloom filtresi anahtar kümesi üzerinde ve
her etiketi ayrı anahtar yapmak o indeksi seyreltirdi.

### 2.6 Eşleme tabloları veri, kod değil

`catalog/mappings/*.yaml`; dosya adı tablo adı. `map` bloğu
`{ from: <alan>, table: <tablo> }` ile bağlanıyor. Üç karar README'de yazılı:

1. Motor bir tabloyu tanımıyorsa bu bir **şema hatası** — `parser lint` sırasında
bildiriliyor, çalışma anı sürprizi değil.
2. Arama **ordinal**; büyük/küçük harf normalizasyonu yok. Sebep F1 §2.4:
`tr-TR`'de `ToLower()` `I → ı` yapar ve eşleme **sessizce** ıskalar.
3. Dizinin sahibi T07; T05 yalnızca mekanizmayı kurmuş. Buradaki dosyalar tam
katalog değil, mekanizmanın çalıştığını gösteren başlangıç seti.

`0004`'ün başlığı sınırı da çiziyor: `catalog/mappings/` **vendor** değerlerini
taşıyor (FortiGate'in `action=` sözcükleri gibi) — onlar sık değişir ve kod
değişikliği gerektirmemeli. İki standardın birbirine eşlenmesi ise sabit,
dolayısıyla görünümde durabilir.

### 2.7 IP normalizasyonu — ve bilinçli bir birleştirme

IPv4 → `::ffff:a.b.c.d` (`MapToIPv6`), tek kolon. Çözülemeyen değer `::`
oluyor ve yorum sonucu açıkça yazıyor: sorgu tarafında **"adres yok" ile "adres
bozuk" ayrımı yapılmıyor**, ikisi de filtrelenebilir tek bir değere düşüyor.

Bu birleştirmenin **gerekçesi kayıtta yok** — yalnızca sonucu yazılı.

## 3 · Bugün ayakta duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `EventNormalizerTests` (birim) | Tip çevrimi, IPv4→v6 eşleme, zaman önceliği, `raw_ref` ön eki, kapsamın olaydan değil **kaynaktan** gelmesi |
| `OcsfOtelViewTests` (8, entegrasyon, CI'da koşuyor) | Aynı olayın iki görünümden okunması, kolona yazılmayan OCSF alanının `attrs` üzerinden görünmesi, IPv4/IPv6 aynı kolon, saat dilimi sıralaması, severity ölçek çevrimi, bilinmeyen severity'nin bilinmeyen kalması, `owner_group`'un taşınması |
| `parser lint` (CI) | Bilinmeyen eşleme tablosuna bağlanan bir `map` bloğu |
| `ClickHouseMigrator` sağlama kontrolü | `0003`/`0004`'ün elle düzenlenmesi — düzeltme yeni dosyayla yapılmak zorunda |

**Bu bekçilerin kırmızı yanabildiğini bu turda ölçmedim.** Belge geriye dönük;
kod okundu, ölçüm yapılmadı.

## 4 · Açıkta kalanlar

### 4.1 OTel severity ölçeği yanlıştı — sessiz sınıftan, sonradan kapandı

`0003` aynı `severity_num` kolonunu **iki farklı ölçekmiş gibi** okuyordu:
`events_ocsf.severity_id` OCSF ölçeğinde (0-6), `events_otel.SeverityNumber`
OTel ölçeğinde (1-24). Kolona yazılan değer OCSF ölçeğinde olduğu için OTel
görünümü yanlış değer veriyordu — `severity_num=5` (OCSF Critical) OTel'de
**DEBUG** anlamına geliyordu.

Göç dosyasının kendi ifadesiyle: "sorgu çalışıyor, sayı dönüyor, yalnızca anlamı
yanlış." Gerçek vendor logu yazılırken fark edilmiş (T08 geri beslemesi, madde
9) — yani T07 kendi başına bunu göremedi.

**Kapandı:** `fc67960` → `0004_fix_otel_severity_scale.sql`. `severity_num`
kolonunun anlamı bundan sonra kesin: OCSF ölçeği.

### 4.2 Zamanın kaynağı hiçbir yerde durmuyordu — sonradan kapandı

§2.4'teki üçlü zincirin üç dalı da `ts` kolonuna yazılıyordu ve **hangisi olduğu
hiçbir yerde durmuyordu**. `0005_time_source.sql` bedeli yazıyor: gözlem zamanına
düşmüş bir olayın gerçek zamanı dakikalarca önce olabilir, dolayısıyla
korelasyon penceresi kayar ve RCA raporu **yanlış kanıtla** kurulur. Ağ
cihazlarında zaman damgasız satır nadir değil.

**Kapandı:** `af73b27` → `time_source` bir **kolon** olarak eklendi, `attrs`
içine değil; gerekçesi de yazılı — "yalnızca güvenilir zamanlı olaylar" filtresi
RCA'nın sık sorusu ve Map araması kolon kadar ucuz değil.

### 4.3 "Türetme maliyeti ölçüldü ve raporlandı" — yarısı

Kabul kriteri iki şey istiyordu: ölçüm ve rapor.
`Turetme_maliyeti_olculuyor` ölçümü **yapıyor** ama sayıyı test çıktısına
yazıyor; kalıcı bir kayda geçmiyor. Testin kendi yorumu bunu bilerek seçmiş:
"kesin süre makineye göre değişir, o yüzden oran değil **varlık** sınanıyor."
Eşik olarak konan 10× ise "bir performans hedefi değil, bir alarm."

Bu, F1'in duvar saati dersiyle tutarlı bir tasarım — ama kabul kriterinin
istediği **rapor edilmiş sayı** kayıtta yok.

### 4.4 Sayısal daraltmalar sessiz

`Byte`/`UInt16`/`UInt32` `Math.Clamp` ile daraltıyor ve `Int64` ayrıştırılamayan
metin için `0` dönüyor. Yani `severity_num=300` sessizce `255`, sayı olmayan bir
port sessizce `0` oluyor.

`string` dalının **varlığının** gerekçesi yazılı (grok her yakalamayı metin
verir, `convert` adımı kullanılmamış olabilir). Daraltmanın ve `0`'a düşmenin
gerekçesi **kayıtta yok** — ve ikisi de bu deponun "sessiz yanlış davranış"
sınıfına giriyor.

**Aradım:** `EventNormalizerTests` geçerli üst sınırı sınıyor (`src_port=65535`
→ `65535`), sınır **dışındaki** bir değerin ne olduğunu sınayan bir test yok;
`tests/` altında `Clamp` geçen başka bir dosya da yok. Yani daraltmanın bugünkü
davranışı bir bekçiyle sabitlenmiş değil.

### 4.5 Gerekçesi kayıtta olmayanlar

| Kalem | Not |
| --- | --- |
| `::` değerinin "adres yok" ve "adres bozuk"u birleştirmesi | Sonucu yazılı, gerekçesi değil |
| Sayısal daraltma ve `0`'a düşme | §4.4 |
| `core` kümesinin neden tam bu on bir alan olduğu | Ticket "sorguların ~%90'ı bunlara vuruyor" diyor; ölçümün kaynağı kayıtta yok |
