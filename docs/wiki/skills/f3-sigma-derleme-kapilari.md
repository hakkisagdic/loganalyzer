---
title: Sigma derleme hattının üç kapısı
category: skills
tags: [test, veri-deposu, yordam, bizigo]
aliases: [kapı 1 kapı 2 kapı 3, EXPLAIN kapısı, derlendi ama koşmuyor]
relationships:
  - target: "[[concepts/f3-bosluk-tek-cins-degildir]]"
    type: uses
  - target: "[[concepts/f3-determinizm-bir-kapi-sartidir]]"
    type: uses
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: implements
  - target: "[[skills/f3-eslesmeyen-kural-teshisi]]"
    type: related_to
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
sources:
  - docs/epic/t32-derleme-tasarimi/index.md
  - docs/epic/tickets-f3/sigma-derleme/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/tickets-f3/sigma-pipeline/index.md
  - docs/epic/sigma-clickhouse-arastirmasi/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=ed0e0490c47f docs/epic/sigma-clickhouse-arastirmasi/index.md=8715e251b522 docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t32-derleme-tasarimi/index.md=ca8f23c25262 docs/epic/tickets-f3/sigma-derleme/index.md=3ab7c1bd87a4 docs/epic/tickets-f3/sigma-pipeline/index.md=6d7c85d5a5f1"
summary: Derlendi ile koşuyor ve doğru şeyi buluyor üç ayrı iddia; her biri farklı bir yerde sınanıyor çünkü tek yere koymak yakalayamadığı sınıfı sessizce geçiriyor.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.84
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Sigma derleme hattının üç kapısı

Ticket'ın merkez cümlesi: **derleme başarısı, koşabilirlik değildir.** Ölçüm de
bunu doğruladı — prototipte `compiled = 24`, `runs = 14`.

Sorunun cevabı *"nerede yakalanır"* değil **"nerelerde"**: üç ayrı yer, üçü
farklı sınıf yakaladığı için. Tek yere koymak, yakalayamadığı sınıfı sessizce
geçirir.

## Üç kapı

| Kapı | Nerede koşar | Yakaladığı | **Yakalayamadığı** |
| --- | --- | --- | --- |
| **1 · kolon varlığı** | Derleme anında, hattın içinde. Docker yok, ClickHouse yok | Görünümde olmayan kolona referans (ölçülen örneklemde 8 kural) | Tip uyuşmazlığı, fonksiyon yanlış kullanımı, sürüme özgü sözdizimi |
| **2 · `EXPLAIN`** | CI, **kendi işinde**, şema yüklü / veri yok | Tip hatası (`proto: 6` ↔ `LowCardinality(String)`), sözdizimi, ad çözümlemesi | Kuralın gerçekten bir şey eşleştirip eşleştirmediği |
| **3 · altın örnek** | CI, veri yüklü | Yanlış pozitif, sıfır eşleşme | — |

Kapı 1'in *"Docker yok"* satırı bir **yerleşim** iddiası: kontrolü mümkün olan
en erken aşamaya koymak. Kapı 2 ve 3 konteyner **istiyor**, yani tablodan
"Sigma kapıları konteyner istemiyor" diye bir genelleme çıkmıyor — ve o
genellemenin nereye götürdüğü [[concepts/konteyner-gerekmiyor-uc-iddia]]'da yazılı. ^[inferred]

**Çalışma zamanı bu tablonun dışında ve öyle kalmalı.** Bir kuralın sessizce
hiçbir şey yakalamaması `CLAUDE.md` §7'nin sınıfı; Kapı 1 ve 2 birlikte o sınıfı
derleme ve CI'ya taşıyor.

## Kapı 1 — geçemeyen kural **dosya üretmez**

Taşıyıcı ilke tek cümle:

> **Var olan bir dosya "bu kural çalışıyor" iddiasıdır.**

Geçemeyen kural manifest'e `gated` olarak **sebebiyle** yazılıyor
(`unknown_column: url`), ve `detections/` altındaki dosya sayısı `compiled`'a
değil `runs`'a yaklaşıyor. Böylece ticket'ın merkez cümlesi bir yorum satırı
değil **kod düzeyinde bir davranış** oluyor.

### Kolon listesi elle yazılmaz — ve sürüklenme asimetrik

Bu, [[concepts/elle-tutulan-liste-bekciyi-korlestirir]] sayfasının doğrudan
uygulaması. Sürüklenmenin iki yönü **eşit değil**:

| Sürüklenme | Sonuç |
| --- | --- |
| Görünüme kolon eklendi, liste güncellenmedi | Kural yanlışlıkla reddedilir — **gürültülü**, fark edilir |
| Görünümden kolon çıktı, liste güncellenmedi | Kural kapıdan **geçer**, çalışma zamanında kırılır — **sessiz** |

Bu yüzden küme `db/clickhouse/*.sql` göçlerinden **sırayla uygulanarak**
türetiliyor. Sıra şart: bir göç `events_otel`'i `DROP` + `CREATE` ile yeniden
yaratıyor, yani *"en son tanım kazanır"* bu depoda zaten işleyen bir kural.
Yalnızca tek bir göçü okuyan bir çıkarıcı o gün sessizce bayatlar.

Türeticinin kendisi de bir bekçi istiyor: entegrasyon testinde türetilen küme
canlı `events_ocsf`'in `DESCRIBE` çıktısıyla karşılaştırılıyor.

## Kapı 2 — kapının kendi körlüğü, kapının içinde bulundu

İlk yazımda `EXPLAIN SYNTAX` seçilmişti. **Ölçüldü, yanlıştı:** o biçim tip
denetimi yapmıyor, yalnızca AST'yi yeniden yazıyor. Bilinen iki kırık sorgunun
ikisine de 200 dönüyordu — yani kapı 24 kuralın hepsini geçirecek, ikisi üretimde
patlayacaktı, ve `KIND_TYPE_MISMATCH` kolu o yoldan **asla** tetiklenemezdi.

Bunu `--self-test` buldu. Kip olmasaydı kusur, kural seti üretime çıkana kadar
görünmeyecekti — ve *"sıfır sorgu soran kapı"* her iki hâlde de yeşil yanardı.

### Ölçülen tablo (canlı 26.7.3, üç sorgu, üç tur)

| Biçim | Ayırt ediyor mu | Sonuçlar |
| --- | --- | --- |
| `EXPLAIN` | ✓ | `red, red, kabul` |
| `EXPLAIN PLAN` | ✓ | `red, red, kabul` |
| `EXPLAIN ESTIMATE` | ✓ | `red, red, kabul` |
| `EXPLAIN QUERY TREE` | ✗ | `kabul, red, kabul` — **kısmen** |
| `EXPLAIN SYNTAX` | ✗ | `kabul, kabul, kabul` |

Üç yordam dersi buradan çıkıyor:

1. **Ölçüt "üçünün üçü de beklenen mi", "en az bir red üretti mi" değil.**
   Gevşek kriter `QUERY TREE` satırını kaçırırdı — o biçim `ILIKE ↔ IPv6`'yı
   yakalıyor, `tamsayı ↔ LowCardinality(String)`'i kaçırıyor. **Kısmen çalışan
   bir kapı hiç çalışmayandan tehlikeli**: ona geçen biri `ILIKE` hatalarını
   yakalamaya devam edeceği için kapı *çalışıyor görünürdü*. `EXPLAIN SYNTAX` en
   azından her şeye "kabul" diyerek kendini ele veriyordu.
2. **Maliyet ayırt edici değil ve bunu bilmek de bir ölçüm sonucu.** Isınma
   çıkarıldığında üç doğru biçim de ~12–13 ms/sorgu; 269 kural ≈ 3,5 saniye.
   Geriye tek ölçüt olarak doğruluk kalıyor, o da üçünde eşit — bu yüzden en
   açık olanı duruyor.
3. **Süre sütunu bir kez yalan söyledi.** Isınmasız ilk ölçüm çıplak
   `EXPLAIN`'i `EXPLAIN PLAN`'in 2,3 katı gösterdi — fiziksel olarak imkânsız,
   çünkü ikisi aynı şey. Ölçülen şey biçim değil **listedeki sıraydı**. Probe
   artık sayılmayan bir ısınma turu atıyor. (`CLAUDE.md` §6'nın "yüklü makinede
   yanlış sayı" maddesinin bu fazdaki tekrarı.)

İki başarısız biçim aday listesinde **duruyor**: *"denedik, olmadı"* bilgisini
silmek, bir sonraki kişinin aynı seçimi aynı gerekçeyle yapmasına kapı açardı.
Probe CI'da kapı değil **ölçüm** olarak koşuyor — bir ClickHouse yükseltmesi
`QUERY TREE`'yi düzeltebilir ya da `PLAN`'i bozabilir, ve o gün bu tablo söyler.

### Kapı 2 kendi CI işinde koşar

Var olan entegrasyon işine eklemek ek konteyner maliyeti getirmezdi. Yine de
ayrı iş: derleme kapısını entegrasyon işine bağlamak, o iş **ilgisiz bir
sebeple** düştüğünde derleme kapısının da körleşmesi demek. Bu depoda
*"başkasının hatası yüzünden sessizleşen bekçi"* deseninin bedeli zaten ödendi.

### Boş dönen bir kapı iki şeyden birini söyler

T31 ölçüldü: `compiled == runs == 21`. Yani Kapı 2 bugün **hiçbir şey
yakalamıyor**. Not tam olarak *"biri bir gün gereksiz diye kaldırmasın diye"*
yazılmış:

> Boş dönen bir bekçinin iki sebebi olabilir: korunan şey sağlam, ya da bekçi
> kör.

İkisini ayıran şey `--self-test`, ve o kip zaten bir kez kapının kendi körlüğünü
buldu. Kapı 2'nin bugünkü işi, T31'in sınıflandırmasının **ClickHouse ile aynı
fikirde olduğunu** doğrulamak: ikisi ayrışırsa bunu başka hiçbir şey görmez —
Kapı 1 kolon adlarına bakıyor, T31 kendi eşleme tablosuna, ve ikisi de
ClickHouse'a sormuyor.

## Kapı 3 — iki soru, iki ayrı yer

Kapı 3 iki şey ölçebilirdi; ikisini tek kapıya koymak hangisinin kırıldığını
belirsiz bırakırdı:

| Soru | Nerede | Neden |
| --- | --- | --- |
| **Derleme doğruluğu** — beyanlı kural beklediğini buluyor mu | **Kapı** | Kural başına; verinin şeklinden bağımsız |
| **Kapsam** — kaç kural bir şey yakalıyor | **Ölçüm** | Verinin şekline bağlı |

Kural sert: **kapsam hiç kapı değil.** *"En az N kural eşleşmeli"* diyen bir
kapı, bir vendor'ın payı kaydığında kırmızı yanar ve o kırmızının sebebi kural
setinde değil **veride** olur — yani kapı yanlış soruyu sormuş olur.

### Beyanlar: gerekçe zorunlu, iki yön zorunlu

Beyanlar `catalog/sigma/expectations.json` içinde, kural başına `at_least_one`
ya da `none` ve **boş bırakılamayan bir gerekçe**: gerekçesiz bir beklenti,
kırıldığı gün *"herhâlde veri değişmiştir"* diye gevşetilir.

`none` beklentisi yalnızca yanlış pozitif bekçisi değil, **kapının ayırt
edebildiğinin kanıtı**: yalnızca `at_least_one` beyanlarından oluşan bir listeyi,
her şeyi eşleştiren bozuk bir kapı da geçerdi.

`none`'ın iki cinsi ([[concepts/f3-bosluk-tek-cins-degildir]]): `invariant`
kırmızısı **kötü haber** (yanlış pozitif doğdu), `corpus_gap` kırmızısı **iyi
haber** (korpus genişledi).

### Veri yoksa kapı hiç koşmuyor

Çıkış kodu 3, ve üç durum ayrı raporlanıyor: sorgu hata verdi (kurulum), tablo
boş (yükleyici koşmamış), vendor eksik (o vendor ölçülemez). Gerekçesi ölçülmüş:
tabloda önceki turdan kalma tek-vendor'lı veri varken *"boş mu"* sorusunun
cevabı hayırdı, ölçüm geçti ve **%0 eşleşme** üretti — o sıfır, eşlemenin değil
verinin sonucuydu. Ayrıntı: [[concepts/f3-oranin-paydasi]].

### İlk gün kırmızı yanmayan, ama ertelenmeyen kapı

Koşul *"üretilen her kural beyanlı olmalı"*; sıfır kural üretiliyorsa sıfır beyan
gerekiyor. Bu bir gevşetme değil koşulun kendisi, ve iki tuzağın arasından
geçiyor:

- Kapıyı *"kural seti gelince bağlarız"* diye ertelemek → **hazırlanmış ama
  bağlanmamış** deseni (F3'te bunun ölçülmüş bir örneği var: `UNMAPPED_FIELDS`
  bir liste olarak duruyordu ve `unmapped_expression()` hiç çağrılmıyordu).
- Bugün koşulsuz zorlamak → ilgisiz bir işi bekleyen, ilk günden kırmızı yanan,
  dolayısıyla gevşetilecek bir kapı.

İlk kural üretildiği anda kapı **kendiliğinden** diş kazanıyor.

### İlk koşumda gerçek bir kusur buldu

`routeros_forward_new` beyanı düştü ve düşmesi doğru: üretilen SQL
`activity_name='forward'` arıyor, veride `class_uid` ve `tcp` var, eksik olan
`activity_name` — ve boş olması kaza değil, parser `action`'ı **bilerek** boş
bırakıyor, zincir adı `fw_chain`'e gidiyor.

**Bunu ancak bu kapı gösterebilirdi.** Ne derleme, ne birim testi, ne Kapı 2 bu
soruyu soruyor.

## Kırmızı yanabilirlik: iki bilerek kırık fixture

| Fixture | Beklenen red | Hangi kapıyı sınar |
| --- | --- | --- |
| `type_uid` şart koşan kural | `unknown_column: type_uid` | Kapı 1 |
| `proto: 6` (String kolona tamsayı) | `EXPLAIN` reddi | Kapı 2 |

Sayıları `ExpectedGatedCount` ile **sabit** — eşik değil sabit, çünkü eşik
*"şu kadara kadar normal"* der ve o rakam bir gün kimsenin bakmadığı bir sayıya
döner. Artış **ve azalış** testi kırıyor. Negatif fixture eklemek **iki ayrı
bilinçli hareket** gerektiriyor, yoksa bir gün kapı bozulur ve *"korpus büyüdü"*
diye okunur.

## Manifest yalnızca dürüstlük değil, **yol haritası**

T33 ajanının cümlesi tasarımı değiştirdi. Sonuçları:

- **`blockers` bir liste, tekil alan değil.** Bir kural birden fazla sebeple
  takılabiliyor; tekil alan *"`url` eklersek kaç kural açılır"* sorusuna
  **fazla** cevap verirdi.
- **Sebep yapısal, yalnızca metin değil.** `{"kind":"unknown_column","column":"url"}`
  **gruplanabilir** bir kayıt: *"31'i eşlemesi olmayan alan kullanıyor"* bir
  `group by kind`. `message` insan için, yanında duruyor.
- **`gated` kural bazında, teşhis alan bazında. Kısmi derleme yok.** Eşlenemeyen
  alanı düşürmek kuralın anlamını değiştiriyor ve **yönü ağaçtaki yerine bağlı**:
  `and` kolunu düşürmek genişletir (yanlış pozitif), `or` kolunu düşürmek
  daraltır (yanlış negatif). İkisinde de başlığının söylediğinden başka bir şey
  yapan bir kural yayınlanmış olur.
- **`gated` durum kalıcılaştırılmıyor**, her derlemede yeniden hesaplanıyor;
  geçiş `git diff`'ten okunuyor (`--summary`). Kalıcılaştırmak ikinci bir gerçek
  kaynak yaratırdı.

## Kapı 3'ün bilinen sınırı: kayıt kaybolabilir

Kapı 3 "beyan edilen kural beklediğini buluyor mu" diye soruyor ve beyanların
gerekçesi **örnek dosyadan** geliyor. Arada bir katman var: satırın örnek
dosyada olması, veritabanında olmasını garanti etmiyor.

`events` tablosunda `TTL toDateTime(ts) + INTERVAL 90 DAY` var ve vendor örnek
dosyaları 2015–2022 tarihleri taşıyor. ClickHouse süresi dolmuş satırı parçayı
oluştururken atıyor — **ama istemciye "yazdım" diyor.**

Sonucu: bir `at_least_one` kuralla ilgisi olmayan bir sebeple düşer, bir
`corpus_gap` yanlış sebeple geçer.

**Bugün risk değil, ölçüldü** — 90 günü aşan satır yok; yükleyici (`GoldenSamplePlan`)
satırları bir `Anchor` etrafına yayıyor, dosyanın kendi tarihini taşımıyor. Ama
sınır **yapısal değil, yükleyicinin davranışına bağlı**: dosyanın kendi tarihine
geçilirse anında geçerli olur. Ön kontrol vendor başına satır sayısına bakıyor,
yani toplu bir süpürmeyi görür, **kısmi** bir düşmeyi görmez.

## Sıra — hepsi tamamlandı

Tasarımın en pratik çıktısı: hattın **kapsamdan bağımsız** olan yarısı önce
yazılabilir. Sıra tutuldu ve **yedisi de kapandı**; tablo tarihsel kayıt olarak
duruyor.

| Sıra | İş | Bloklayan |
| --- | --- | --- |
| 1 | Kolon kümesi çıkarıcısı + göç sırası + entegrasyon bekçisi | — |
| 2 | Kapı 1 + negatif fixture'lar + kırmızı ölçümü | — |
| 3 | Manifest şeması, takaslı yazım, sürüklenme kapısı | — |
| 4 | Kapı 2 — yazılır, **koşturulmaz** | — |
| 5 | Kural seti çekme (sabit SHA) | Korpus kararı |
| 6 | Gerçek derleme | T31 |
| 7 | Kriter D (canlı ClickHouse + altın örnek) | koordinatör |

**Korpus değişir, hat değişmez.** Ve değişti: korpus T30 prototipinden terfi
ettirildi, hat dokunulmadan kaldı — 21 kural derleniyor, 3'ü `gated`, Kapı 3
canlıda sekiz beyanın sekizini geçirdi.

## Dördüncü eksen: kapının kendi maliyeti

Üç kapı *"ne yakalıyor"* sorusunu bölüyor. Sonradan dördüncü bir soru çıktı ve
üçünden de bağımsız: **kapı ne kadara mal oluyor, ve maliyeti korpusla birlikte
büyüyor mu?**

Önemi somut: sürüklenme kapısını besleyen `build_manifest`, tekrarlanan kural
kimliğini ararken listeyi her kural için yeniden tarıyordu.

| Kural | Kimlik karşılaştırması | Kural başına |
| --- | --- | --- |
| 24 | 576 | 24 |
| 269 | 72.361 | 269 |
| 7.400 | ~55.000.000 | 7.400 |

Bugünkü 24 kuralda görünmüyor. Ve fonksiyon hem `--write` hem **`--check`**
yolunda, yani **kapı, koruduğu şey büyüdükçe kendini yavaşlatıyordu** — bir
bekçinin bir gün "yavaş" diye kaldırılmasının en olağan gerekçesi. Aynı karesel
deyim üç ayrı dosyada tekrarlanmıştı; düzeltme tek bir ortak yardımcıya indi,
çünkü üç yerde ayrı ayrı düzeltmek bugünkü kusuru kapatır, **deyimi** kapatmaz.

### Ölçekleme kapı olabilir, süre olamaz

| Soru | Nerede | Neden |
| --- | --- | --- |
| Kural başına **iş** korpusla büyüyor mu | **Kapı** | Karşılaştırma sayılıyor — makinenin yükünden bağımsız |
| O işin kaç **milisaniye** ettiği | **Ölçüm** | Yalnızca sessiz makinede cevaplanabilir |

Bu, Kapı 3'ün *"doğruluk kapı, kapsam ölçüm"* bölünmesinin süre eksenindeki
kardeşi — ve aynı sebeple: makinenin o günkü yüküne bağlanan bir kapı ilk yoğun
günde gevşetilir. Bu depo o dersi iki kez ödedi (`GrokPropertyTests` 2 sn,
`DiscoveryWorkerTests` 200 ms).

Ayrım turun kendisinde sınandı: ölçüm sırasında makine thrash'teydi ve
`machine-resources.sh check` **exit 1** verdi. Süre ölçümü durdu; ölçekleme
kapısı **aynı makinede** ölçüldü ve karesel terimi buldu. Ölçülemeyen bir soru,
ölçülebilir bir soruya çevrilmişti.

Süre ölçümü (`sigma_build.cost`) tek sayı üretmiyor, deftere **ekliyor** — K35'te
aynı ölçüm ajanda 1,46×, koordinatörde 1,62× çıkmıştı ve üstüne yazan bir defter
ayrışmanın kendisini, yani makinenin sessiz olmadığının kanıtını silerdi.

## Kaynaklar

- `docs/epic/t32-derleme-tasarimi/index.md` — üç kapı, `--self-test`, manifest, sıra, §6 maliyet
- `docs/epic/tickets-f3/sigma-derleme/index.md` — kabul kriterleri, `clicksiem` tuzağı
- `docs/epic/t30-sigma-olcumu/index.md` — `compiled`/`runs` ayrımı, ön kontrol protokolü
- `docs/epic/tickets-f3/sigma-pipeline/index.md` — Kapı 1'in beslendiği eşleme tablosu
- `docs/epic/sigma-clickhouse-arastirmasi/index.md` — backend seçimi ve dosya sayımı tuzağı
