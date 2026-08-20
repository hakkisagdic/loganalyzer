---
title: "T05 — Parser motoru: YAML, grok, CLI"
kind: ticket
status: 2
---

# T05 — Parser motoru: YAML şema, grok derleyici, CLI

**Bağımlılık:** T01 · **Sonraki:** T06, T07, T12
**Yöneten belgeler:** [F1 §3, §4, §11](../../f1-teknik-plan/index.md) ·
[K3, §3.4](../../mimari-kararlar/index.md)

## Amaç

Ürünün kalbi. Kod yazmadan yeni log formatı eklenebilmesi bu ticket'ta gerçek oluyor.
T02/T03'ten bağımsız çalışabilir — girdi bir string, çıktı alan sözlüğü.

## Kapsam

### İçinde

1. **YAML şeması ve doğrulayıcı** — [F1 §3](../../f1-teknik-plan/index.md)'teki
`apiVersion / metadata / match / pipeline / map / tests` yapısı. Şema doğrulaması
anlamlı hata mesajı üretir (satır/sütun ile).
2. **Grok derleyici** — `System.Text.RegularExpressions` üstünde ince katman
([K §3.4](../../mimari-kararlar/index.md)). `%{PATTERN:alan:tip}` özyinelemeli
genişletme + adlandırılmış grup üretimi.
  - **ReDoS savunması, üç kademe:**
  1. Önce `RegexOptions.NonBacktracking` ile derlemeyi dene — girdi uzunluğunda
doğrusal, felç imkânsız.
  2. Pattern lookaround/backref içeriyorsa (Logstash `IPV4` pattern'i `(?<![0-9])`
kullanır) geri izlemeli motora düş + `matchTimeout = 50ms` + derleme zamanı
linter (iç içe niceleyici, `(a+)+`).
  3. Sürekli timeout veren parser **karantinaya** alınır, sahibi uyarılır.
    - Derlenmiş `Regex` nesneleri `parser_id + version` anahtarıyla önbelleklenir.
3. **Pattern kütüphanesi** — Logstash/Elastic grok setleri **veri olarak** repoya
alınır ve sürümlenir. Kod değil.
4. **Pipeline adım tipleri** — `grok`, `kv`, `json`, `csv`, `date`, `convert`, `drop`.
`on_failure: continue | fail | tag`. Yürütme sırası `pipeline` sırasıdır;
koşul/döngü **yok**.
5. **`parse_status`** — `ok / partial / failed`. Kısmi ayrıştırma meşru bir durum.
6. **CLI** — `bizigo parser lint | test | try`. `try` en çok kullanılacak komut;
F2'deki UI editörü bunun üstüne oturacak.
7. **Test koşucusu** — YAML'ın gömülü `tests` bloğunu koşturur. **Testsiz parser**
** yayınlanamaz** — kalite için tek en ucuz kaldıraç.

### Dışında

Dispatcher (T06), `map` bloğunun OCSF/OTel türetmesi (T07 — bu ticket `map` bloğunu
ayrıştırır ve `core`'u doldurur), vendor parser'ları (T08).

## Kabul kriterleri

bizigo parser try tek satır log + YAML ile alanları basıyorbizigo parser test gömülü testleri koşturuyor, başarısızlıkta anlamlı fark gösteriyorbizigo parser lint ReDoS riskli pattern'i yakalıyorProperty test: rastgele pattern → derlenir veya anlamlı hata verirReDoS corpus'u ile timeout doğrulaması; hiçbir girdi süreci kilitlemiyorLogstash pattern setinin tamamı hatasız yükleniyor

## Notlar

- `grok.net` **kullanılmıyor**: v2'den itibaren PCRE.NET (native) kullanıyor; RID
başına ikili, AOT/trim belirsizliği ve en önemlisi ReDoS'a karşı `NonBacktracking`
eşdeğeri yok. Parser YAML'ı 50 kişilik kurumdan geliyor — kötü niyet gerekmiyor,
dikkatsiz tek pattern ingest'i durdurmaya yeter.
- Derleme ~300–400 satır. Referans için `grok.net` okunabilir.

## Uygulama notları (kapanış)

Uygulanan yer: `src/Bizigo.Parsing/` (Grok, Schema, Engine, Testing) + `src/Bizigo.Cli/`

- `catalog/patterns/` + `catalog/mappings/`. Birim testleri **122/122**, derleme 0 uyarı.

### Karara bağlanan noktalar

| Konu | Karar | Neden |
| --- | --- | --- |
| `on_failure` varsayılanı | **`fail`** | Dispatcher'ın "ilk `ok` kazanır" kuralı ancak eşleşmeyen parser'ın açıkça başarısız olmasıyla anlam kazanır. `continue` varsayılan olsaydı her parser her satırı "kısmen ayrıştırdım" diye sahiplenirdi (T06'yı doğrudan bozardı). |
| ReDoS bulgu şiddeti | Doğrusal motorda **bilgi**, geri izlemeli motorda **hata** | `NonBacktracking` ile derlenen pattern zaten doğrusal; orada `(a+)+` için hata vermek gerçek bulguları gürültüde boğardı. Linter'ın işe yaramasının şartı bu ayrım. |
| Eşleme tabloları | T05'e alındı (mekanizma), içerik T07'de | Aksi halde `map.ocsf.activity_id` yarım kalır: şema onu kabul eder ama motor çözemezdi. Tablolar `catalog/mappings/*.yaml`; **bilinmeyen tablo derleme zamanı hatası**. |
| Şablon çözülemezse | Atama **yapılmaz** | Boş string yazmak olayda "kaynak IP boş" gibi görünüp sorgu sonuçlarını sessizce kirletir. |
| Alan adı normalizasyonu | `[source][ip]` → `source.ip` | ECS pattern setinde alan adları köşeli parantezli. Grup adı ayrıca üretildiği için (`g0`, `g1`…) .NET grup adı kısıtı hiç devreye girmiyor. |
| Bilinmeyen YAML anahtarı | **Hata** + Levenshtein önerisi | `seperator` yazan kullanıcının parser'ının neden çalışmadığını saatlerce aratmamak için. |

### Upstream pattern setinde çıkan üç gerçek sorun

1. **`SHOREWALL` pattern'i `.?*` içeriyor** — Oniguruma tolere ediyor, .NET
"Nested quantifier" diye reddediyor. Pattern dosyasına dokunmadan derleyicide
`X?*` → `X*` çevirisi yapıldı (niyet açıkça `.*`).
2. **`MCOLLECTIVEAUDIT` iki dosyada tanımlı** (`mcollective`, `mcollective-patterns`),
gövdeler birebir aynı. Kural "birebir aynı tanım serbest, **çelişen** tanım hata"
olarak daraltıldı — sessizce üzerine yazmak hangi pattern'in koştuğunu takip
edilemez yapardı.
3. **`\h` ve `[[:alnum:]]`** — Oniguruma/POSIX yapıları. Derleyicideki tarayıcı
karakter sınıfı içinde/dışında olduğunu bilerek çeviriyor.

Sonuç: **legacy ve ecs-v1 setlerinin tamamı** derleniyor ve bu bir teste bağlandı
(`GrokPatternLibraryTests`). Upstream yükseltmesi bir pattern'i bozarsa CI'da görülür.

### Doğrulanan kabul kriterleri

| Kriter | Nasıl |
| --- | --- |
| `parser try` alanları basıyor | `CliSmokeTests` — metin ve `--json` çıktısı, Türkçe gövde dahil |
| `parser test` anlamlı fark gösteriyor | `CliSmokeTests` — `beklenen: 80 / gerçek: 53` |
| `parser lint` ReDoS yakalıyor | `CliSmokeTests` — `GROK001`, çıkış kodu 1 |
| Property test | 5 000 rastgele pattern → **yalnızca** `GrokCompilationException` veya başarı; başka istisna tipi yok |
| ReDoS corpus'u | 6 klasik desen; hiçbiri 2 sn'yi geçmiyor (doğrusal motorda eşleşmiyor, geri izlemelide zaman aşımı) |
| Logstash seti | İki set de tam derleniyor (~430 + ~430 pattern) |

### Sonraki ticket'lara devreden

- **T06:** `match.contains` literalleri şemada var ve doğrulanıyor; Aho-Corasick
 otomatı ve `specificity` sıralaması T06'da kullanılacak.
- **T07:** `catalog/mappings/ocsf_network_activity.yaml` başlangıç seti — tam OCSF
 kataloğu ve `core` → olay nesnesi dönüşümü T07'nin.
- **T12:** `ParserQuarantine` motorda hazır; sıcak yolda `ParseResult.TimedOut`'u
 buna bağlamak dispatcher'ın (T06) işi.
