---
title: Deklaratif parser motoru yazmak
category: skills
tags: [log-analiz, mimari, yordam, bizigo]
aliases: [YAML parser, grok derleyici, dispatcher kademeleri, altın örnek]
relationships:
  - target: "[[concepts/f1-duvar-saati-tuzagi]]"
    type: uses
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: related_to
  - target: "[[concepts/f1-ham-sadakat-zinciri]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: part_of
sources:
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/tickets/parser-motoru/index.md
  - docs/epic/tickets/dispatcher-ve-envanter/index.md
  - docs/epic/tickets/vendor-katalogu/index.md
  - docs/epic/t08-motor-geri-beslemesi/index.md
  - docs/epic/f1-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=61628e5aed41 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/t08-motor-geri-beslemesi/index.md=5ce87e36f831 docs/epic/tickets/dispatcher-ve-envanter/index.md=5b0c998c5ca7 docs/epic/tickets/parser-motoru/index.md=aeac91406893 docs/epic/tickets/vendor-katalogu/index.md=6603c664a634"
summary: Kod yazmadan log formatı eklenebilmesi için alınan kararlar — kendi grok derleyicisi, dört kademeli dispatcher, zorunlu testler — ve gerçek vendor logunun bu kararlarda açtığı on yer.
provenance:
  extracted: 0.88
  inferred: 0.12
  ambiguous: 0.0
base_confidence: 0.85
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Deklaratif parser motoru yazmak

K3'ün kararı tek cümle: **deklaratif YAML + grok birinci sınıf**, .NET assembly
yalnızca kaçış kapısı. Ürünün kalbi bu — *"kod yazmadan yeni log formatı
eklenebilmesi"* (`docs/epic/tickets/parser-motoru/index.md`). Aşağıdakiler o
kararın ardından gelen ve gerçek logla sınanmış yordamlar.

## 1 · Hazır grok kütüphanesi yerine ~350 satır kendi derleyicin

Tek olgun .NET seçeneği `grok.net` 2.0, v2'den itibaren **PCRE.NET** (native)
kullanıyor. Eksikler sıralanabilir (RID başına ikili, AOT/trim belirsizliği,
pattern kayıt defteri yok) ama belirleyici olan dördüncüsü: **ReDoS.**

Parser YAML'ı kullanıcıdan geliyor (K16 — çok kullanıcılı büyük kurum). Kötü
niyet gerekmiyor; dikkatsiz tek bir pattern ingest'i durdurmaya yeter. .NET'in
`RegexOptions.NonBacktracking` seçeneği girdi uzunluğunda **doğrusal zaman
garantisi** veriyor ve PCRE tarafında bunun karşılığı yok
(`docs/epic/mimari-kararlar/index.md` §3.4).

Üç kademeli strateji (`docs/epic/f1-teknik-plan/index.md` §4.1): önce
`NonBacktracking` ile derlemeyi dene → lookaround/backref varsa geri izlemeli
motora düş (+ `matchTimeout` + derleme zamanı linter) → sürekli timeout veren
parser'ı **karantinaya** al.

**Pattern kütüphanesi kod değil veri.** Logstash/Elastic setleri repoda
sürümleniyor. Bu tercih karşılığını T08'de verdi: bir vendor pattern'inde çıkan
ReDoS, pattern dosyasına dokunulmadan `pattern_definitions` ile parser içinde
geçersiz kılındı.

### Doğrusal motor gerçek katalogda neredeyse hiç devreye girmedi

En değerli ölçüm bu ve karar belgesinin gerekçesini güncelliyor: kataloğun
tamamında **15 pattern** geri izlemeli motora düştü, sebep bizim pattern'lerimiz
değil Logstash'in temel tanımları — `IPV4` `(?<![0-9])`, `TIME` `(?![0-9])`
kullanıyor ve bunlara dokunan her ifade geri izlemeye düşüyor. Ağ cihazı logunda
IP geçmeyen pattern neredeyse yok (`docs/epic/t08-motor-geri-beslemesi/index.md`
§8b).

Çözüm F1 kapanışında sevk edildi: `legacy` setinin üstüne **`bizigo-v1`
kaplaması** — lookaround'suz `IPV4`/`TIME` varyantları. Zincir ölçülerek kapandı:
GROK003 uyarısı **21 → 8 → 4 → 2 → 0**, ve kapsam boyunca altın örnek sonucu
86/1/0 sabit kaldı (`docs/epic/f1-kapanis/index.md`).

Yan kazanç, [[concepts/f1-duvar-saati-tuzagi]] sayfasının konusu: lookaround'suz
set = timeout'suz set, ve test paketi 45–75 sn'den **11 sn**'ye indi.

## 2 · Varsayılanları dispatcher'a göre seç

Motorun tek başına doğru görünen varsayılanları, dispatcher'la birlikte yanlış
olabiliyor. T05'in kayda geçmiş kararları
(`docs/epic/tickets/parser-motoru/index.md`):

| Konu | Karar | Neden |
| --- | --- | --- |
| `on_failure` varsayılanı | **`fail`** | Dispatcher'ın "ilk `ok` kazanır" kuralı ancak eşleşmeyen parser'ın açıkça başarısız olmasıyla anlam kazanıyor; `continue` olsaydı her parser her satırı sahiplenirdi |
| ReDoS bulgu şiddeti | Doğrusal motorda **bilgi**, geri izlemelide **hata** | `NonBacktracking` ile derlenen pattern zaten doğrusal; orada hata vermek gerçek bulguları gürültüde boğardı |
| Şablon çözülemezse | Atama **yapılmaz** | Boş string yazmak olayda "kaynak IP boş" gibi görünüp sorgu sonuçlarını **sessizce kirletir** |
| Bilinmeyen YAML anahtarı | **Hata** + Levenshtein önerisi | `seperator` yazan kullanıcı parser'ının neden çalışmadığını saatlerce aramasın |
| Aynı pattern iki dosyada | Birebir aynıysa serbest, **çelişiyorsa hata** | Sessizce üzerine yazmak hangi pattern'in koştuğunu takip edilemez yapardı |

Üçüncü satır, boru hattının başka yerlerinde de tekrarlanan bir ilke: **yokluğu
sahte bir değerle doldurma.** T08 aynı davranışın gerçek logda işe yaradığını
ölçtü — RouterOS firewall kaydı kuralın sonucunu içermiyor, `core.action` boş
kalıyor; boş string yazılsaydı `action=''` diye sorgulanabilir sahte bir değer
oluşurdu.

## 3 · Dört kademeli dispatcher — sıra performans için değil doğruluk için

`docs/epic/f1-teknik-plan/index.md` §4.2 ve
`docs/epic/tickets/dispatcher-ve-envanter/index.md`:

1. **Envanter bağı** (`source_id → parser_id`) — hedef, trafiğin **>%95'i**.
2. **Literal ön filtre** — tüm `match.contains` literalleri tek bir Aho-Corasick
   otomatına derleniyor, satır **bir kez** taranıyor.
3. **Aday denemesi** — `specificity` sırasıyla, ilk `ok` kazanıyor.
4. **Düşüş** — `parse_status=failed`; olay ham arşive ve sidecar keşif kuyruğuna.

Uygulamada üç ince karar çıktı:

- **Anlık görüntü satırın başında alınıyor** — sıcak yeniden yükleme tam o sırada
  olursa satır tutarlı tek bir katalogla işleniyor. Ayrıca bütün dosyalar bozuksa
  katalog **değiştirilmiyor**: hatalı bir dağıtım çalışan boru hattını
  parser'sız bırakamıyor.
- **Literali olmayan parser her satırda aday** — ön filtre onu eleyemez, listeye
  konmazsa sessizce hiç denenmezdi. *"Ön filtre bir güvenlik ağı, zorunluluk
  değil."*
- **Bağlı parser tutmazsa aday taramasına düşülüyor**, ama ayrı sayaçla
  (`bound_misses`): satır kaybolmuyor, envanterin bakıma ihtiyacı olduğu görünür
  kalıyor. Aynı amaçla `bound_ratio` bir sağlık metriği.

### Ölçülen tuzak: `match` bir doğruluk garantisi değil

`match.contains` yalnızca **2. kademede** çalışıyor. Kademe 1 (envanter bağı) ve
gömülü `tests` bloğu onu tamamen atlıyor — yani hedef %95 tutarsa `match`
üretimde neredeyse hiç çalışmıyor. Somut sonuç: FortiGate traffic parser'ı bir
`type="event"` satırını `ok` ayrıştırdı; ayırt edici tek şey `match.contains`
içindeydi (`docs/epic/t08-motor-geri-beslemesi/index.md` §4).

Kataloğun çözümü her parser'ın boru hattını bir **kapı adımıyla** başlatmak.
Motorda kalıcı çözüm F2'ye ertelendi — ve orada kataloğun kendisi ikinci bir
kaynak kazandı: [[skills/f2-atomik-yayin-akisi]].

## 4 · Test ve altın örnek disiplini

- **Testsiz parser yayınlanamaz** — en az bir geçen test. Kalite için tek en ucuz
  kaldıraç (`docs/epic/f1-teknik-plan/index.md` §3).
- **Altın örnekler gerçek cihaz çıktısı olmalı**, elde uydurulmuş değil:
  *"uydurulmuş örnek, motorun eksiğini değil kendi hayal gücümüzü test eder"*
  (`docs/epic/tickets/vendor-katalogu/index.md`).
- Ticket'ın kendi ölçütü en sert olanı: *"Hiçbir eksik bulunmadıysa muhtemelen
  örnekler yeterince gerçek değil."* T08 gerçek FortiOS 6.2/6.4/7.2/7.4, 13 ASA
  mesaj kodu, RouterOS 6.x/7.x ve nginx çıktısıyla çalıştı ve motorda **on ayrı
  eksik** açtı; dördü aynı turda kapandı.

Bir bekçi de bu turda düzeltildi: `VendorCatalogTests` `legacy`'yi tek başına
yükleyerek **sevk edilmeyen** bir yapılandırmayı sınıyordu; artık üretimin
kütüphanesini kullanıyor ve kataloğun sıfır GROK003 verdiğini `dotnet test`
içinde sabitliyor.

### Sapmayı gerekçesiyle kabul etmek

Ticket dört parser istiyordu, sekiz yazıldı. Sebep formatın bilinçli kısıtı:
`map` bloğu satır içeriğine göre **dallanamıyor** ve aynı vendor'ın farklı mesaj
aileleri farklı OCSF sınıflarına ait. Alternatif `class_uid`'i yarı yarıya yanlış
yazmaktı; katalog kuralı **"OCSF sınıfı ailesi başına bir parser"** oldu
(`docs/epic/tickets/vendor-katalogu/index.md`).

## 5 · Sessiz yanlış veri üreten eksikleri önceliklendir

T08'in on maddelik listesinde tehlike sırası açıkça yazılmış: çoğu madde satırı
düşürüyor ya da `partial` yapıyor — **ikisi de görünür**. Tehlikeli olanlar
sessiz olanlar ([[concepts/sessiz-yanlis-davranis]]):

- **`timezone_field` sayısal ofset tanımıyordu ve iz bırakmadan
  `default_timezone`'a düşüyordu.** Katalogda `timezone_field: tz` bu yüzden
  bilinçli olarak yazılmadı: yazılsaydı FortiGate olayları **8 saat kaymış** zaman
  damgasıyla yazılırdı ve ne `parse_status` ne etiket bunu gösterirdi. Kapandı —
  `±HHmm`/`±HH:mm`/`Z` çözülüyor, çözülemeyene `_tz_unresolved` etiketi.
- **`UNIX_NS` yoktu**; nanosaniye epoch `UNIX_MS` ile okununca 51345 yılına
  gidiyordu — hata yok, uyarı yok. Kapandı (`UNIX_NS`/`UNIX_US`/`UNIX_AUTO` +
  aralık kontrolü).
- **`convert` hiç alan bulamayınca satırı düşürüyordu**; tamamen doğru ayrışmış
  bir ASA satırı bu yüzden `failed` oldu. Kapandı.

Etiketlerin ClickHouse'a ulaşması ayrı bir kalemdi ve F1 kapanışında düzeldi:
`ParseResult.Tags` normalizasyonda hiçbir yere yazılmıyordu, yani
`_asa_no_timestamp` etiketi **kayboluyordu**. Artık `time_source` kolonu
(`parsed`/`observed`/`received`) ve `attrs['bizigo.tags']` var; `ReplayDiff`
`time_source`'u karşılaştırıyor — zamanı çözemeyen bir parser düzeltilince
`observed → parsed` geçişi görülebiliyor.

## Kaynaklar

- `docs/epic/mimari-kararlar/index.md` — K3, K15, §3.4
- `docs/epic/f1-teknik-plan/index.md` — §3, §4.1, §4.2, §4.3, §11
- `docs/epic/tickets/parser-motoru/index.md` — karara bağlanan noktalar, upstream sorunları
- `docs/epic/tickets/dispatcher-ve-envanter/index.md` — dört kademe, üç tasarım kararı
- `docs/epic/tickets/vendor-katalogu/index.md` — altın örnek ölçütü, 4 → 8 sapması
- `docs/epic/t08-motor-geri-beslemesi/index.md` — on eksik, motorun iyi çalıştığı yerler
- `docs/epic/f1-kapanis/index.md` — `bizigo-v1` kaplaması, GROK003 zinciri, `time_source`
