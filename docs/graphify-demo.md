# Graphify — demo: bu araç bu depoda ne yapıyor

`docs/graphify.md` aracın **ne olduğunu**, `docs/graphify-kullanim.md` **ne
zaman kullanılacağını** anlatıyor. Bu belge ikisini de tekrarlamıyor. Burada
tek bir şey var: **altı gerçek soru, altı gerçek koşum, ve grafın yanıldığı
bir örnek.**

Demo satmıyor, tarif ediyor. Kazandığı yerleri de kaybettiği yerleri de aynı
biçimde gösteriyor.

## Ölçüm koşulları

| | |
| --- | --- |
| Depo HEAD | `c7066e6` |
| Grafın derlendiği commit | `cca19da5` — HEAD'in atası **değil**, ayrık bir dal |
| Aradaki mesafe | `git rev-list --count cca19da5..HEAD` → **36** |
| graphify | `0.9.48` |
| Makine | yüklü (swap %87, bellek %49 boş) — bkz. son bölüm |

Aşağıdaki her çıktı bu koşullarda **gerçekten koşturuldu**. Kırpılan yerler
`(kırpıldı)` ile işaretli. `graphify update`, `cluster-only`, `label` gibi
grafı yeniden üreten hiçbir komut koşturulmadı (ağır iş yasağı).

Bütün komutlar stderr'e `warning: skill is from graphify 0.8.19, package is
0.9.48.` basıyor; aşağıda `2>/dev/null` ile susturuldu. **Bunun bir yan
etkisi var, §4'te ölçüldü.**

---

## 1 · Grafın içinde ne var

`graph.json`, 12 MB, tek dosya. İçindekiler `python3` ile sayıldı:

| | |
| --- | --- |
| Düğüm | **8.313** |
| Kenar (`links`) | **19.000** |
| Hiperkenar | 0 |
| Yönlü mü | **hayır** (`directed: false`) |
| Topluluk | **420** (en büyüğü 99 düğüm) |
| Ortalama derece | 4,57 · medyan **3** · maksimum **150** |
| Derecesi ≥ 50 olan düğüm | **11** |
| İzole düğüm (derece 0) | **2** (`sidecar/tests/conftest.py`, `sigma-build/tests/conftest.py`) |
| Ucu kırık kenar | 0 |

Düğümler ne cinsten:

| `file_type` | Adet | Ne olduğu |
| --- | --- | --- |
| `code` | 6.724 | Sınıf, metot, dosya, ve **kaynağı olmayan dış semboller** |
| `document` | 1.252 | 113 markdown dosyasının başlıkları |
| `rationale` | 298 | Yalnızca Python docstring'lerinden — aşağıya bak |
| `concept` | 39 | Çoğu ayrıştırma artığı — aşağıya bak |

**8.313 düğümün 1.661'inin kaynak dosyası yok.** Bunlar `Dictionary`, `Func`,
`Path`, `DateTimeOffset`, `JsonElement` gibi dış tipler: grafta düğüm olarak
duruyorlar ama depoda karşılıkları yok. Yani düğümlerin **%20'si depo dışı**.

Kenar cinsleri:

```
references 6206 | calls 4213 | contains 3511 | method 2406 | imports 1644
imports_from 455 | rationale_for 298 | implements 117 | inherits 71 | uses 30
dynamic_import 20 | re_exports 9 | extends 8 | indirect_call 7 | defines 5
```

Olgu ile tahmin ayrımı:

```
EXTRACTED 18.137   (%95,5)
INFERRED     863   (%4,5) — ortalama güven 0,807
```

`INFERRED` kenarlar **yalnızca üç ilişkide** çıkıyor: `calls` 826, `uses` 30,
`indirect_call` 7. Yani "A, B'yi çağırıyor" cümlesi grafın en zayıf cümlesi;
`imports`/`contains`/`inherits` gibi yapısal kenarların **hepsi** AST'den.
Bu ayrım §2.1'de somut olarak karşımıza çıkıyor.

Düğümler dizinlere göre:

```
src 1906 | tests 1683 | (kaynaksız) 1661 | docs 1039 | ui 980
tools 449 | sidecar 224 | prototypes 180 | .claude 62 | catalog 37 | deploy 23
```

### Grafta ne YOK — sayarak

Depoda 693 ayrıştırılabilir dosya var (`.cs/.md/.ts/.tsx/.py/.csproj`,
`graphify-out/` hariç), grafta 682 dosya izi var. Kesişimi çıkarınca:

```
HEAD'de var, grafta YOK : 24 dosya
grafta var, HEAD'de YOK :  2 dosya
```

Uzantı bazında kapsama (manifest'te izlenen ↔ grafta düğümü olan):

```
.cs   356 -> 356      .md 113 -> 113      .ts  76 -> 76      .tsx 71 -> 71
.py    37 ->  37      .json 24 ->   4     .yml 27 ->  0      .yaml 22 ->  0
.sql    0 ->   0      <- depoda 28 .sql dosyası var, manifest'te bile yok
```

Son satır önemli: veritabanı şeması — `db/clickhouse/0001_events.sql` ve 27
kardeşi — **manifest'e hiç girmemiş.** `db/` altından grafta yalnızca
`README.md` var.

### `docs/` bir ada

1.039 doküman düğümü var. Bu düğümlerden **doküman ağacının dışına çıkan
kenar sayısı: 2.** (Biri `README.md`'ye, biri `tools/`'a.)

Yani graf dokümanları okuyor, indeksliyor, topluluklara bölüyor — ama hiçbirini
anlattığı koda bağlamıyor. §2.5 bunun pratikte ne demek olduğunu gösteriyor.

### `rationale` yalnızca Python'da

298 `rationale` düğümünün kaynak dağılımı:

```
tools 148 | prototypes 78 | sidecar 72 | src 0 | tests 0 | ui 0
```

Üçü de Python. Bu depoda 356 C# dosyası var ve hepsi XML doc yorumlarıyla
dolu; grafta **tek bir C# gerekçe düğümü yok**. "Bu neden böyle yapılmış"
sorusunu graf yalnızca `tools/`, `prototypes/`, `sidecar/` için cevaplayabilir.

### `concept` bir kavram katmanı değil

39 `concept` düğümünün tamamı:

```
Microsoft.NET.Sdk 16, Microsoft.NET.Sdk.Web, jose, next, react, react-dom,
typescript, vitest, redis, posthog-js, posthog-node, openapi-typescript,
@playwright/test, @types/node, @types/react, @types/react-dom,
dom, dom.iterable, ES2022, next-env.d.ts, node_modules,
'**/*.ts', '**/*.tsx', '.next/types/**/*.ts'
```

17'si csproj'un `Sdk` özniteliği, gerisi npm bağımlılık adları ve **tsconfig
glob desenleri**. Ad "concept" ama içerik ayrıştırma artığı; buna anlam yükleme.

---

## 2 · Altı soru, altı koşum

Sorular bu depoda gerçekten sorulmuş cinsten. Her biri için hem graf hem grep
koşturuldu ve **ikisinin sayısı yan yana** duruyor.

### 2.1 · "İkinci kopya yazmadan önce: `SecretProtector`'ı kim kullanıyor?"

`CLAUDE.md` §9 ortak yüzeyi kopyalamayı yasaklıyor ve `SecretProtector`'ı
adıyla sayıyor. Dokunmadan önce tüketici listesi lazım.

```
$ graphify explain "SecretProtector" 2>/dev/null
Node: SecretProtector
  ID:        src_bizigo_contracts_security_secretprotector_bizigo_contracts_security_secretprotector
  Source:    src/Bizigo.Contracts/Security/SecretProtector.cs L32
  Type:      code
  Community: NotificationSecretTests
  Degree:    10

Connections (10):
  <-- ChangeConnectorService [references] [EXTRACTED] src/Bizigo.Api/Connectors/ChangeConnectorService.cs:L53
  <-- ChangeConnectorTests [references] [EXTRACTED] tests/Bizigo.UnitTests/ChangeConnectorTests.cs:L35
  <-- NotificationDispatcher [references] [EXTRACTED] src/Bizigo.Alerting/Notifications/NotificationDispatcher.cs:L43
  <-- DeviceConfigRunner [references] [EXTRACTED] src/Bizigo.Api/Connectors/DeviceConfigRunner.cs:L81
  <-- NotificationChannelService [references] [EXTRACTED] src/Bizigo.Alerting/NotificationChannelService.cs:L55
  --> .Unprotect() [method] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L117
  --> .Protect() [method] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L83
  <-- SecretProtector.cs [contains] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L32
  --> byte [references] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L37
  --> int [references] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L35
exit=0
```

Beş tüketici. **Ama bu cevap eksik** — sınıf düğümü metotlarının çağrılarını
görmüyor. Metot düğümünü ayrıca sormak gerekiyor:

```
$ graphify explain ".Protect()" 2>/dev/null
Node: .Protect()
  Degree:    9
Connections (9):
  <-- .SaveAsync() [calls] [INFERRED] src/Bizigo.Api/Connectors/ChangeConnectorService.cs:L179
  <-- .SaveAsync() [calls] [INFERRED] src/Bizigo.Alerting/NotificationChannelService.cs:L122
  <-- .RunAsync() [calls] [INFERRED] src/Bizigo.Api/Connectors/DeviceConfigRunner.cs:L172
  <-- SecretProtector [method] [EXTRACTED] src/Bizigo.Contracts/Security/SecretProtector.cs:L83
  <-- .Gonderici_turu_ne_veritabanina_ne_loga_gizli_bilgi_yaziyor() [calls] [INFERRED] tests/Bizigo.UnitTests/NotificationSecretTests.cs:L254
  <-- .Kurcalanmis_sifreli_metin_reddediliyor() [calls] [INFERRED] tests/Bizigo.UnitTests/NotificationSecretTests.cs:L69
  ... (kırpıldı, 3 satır daha — hepsi INFERRED)
exit=0
```

Dikkat: metot düğümünün **gelen kenarlarının 8'i de `INFERRED`.** Yani "kim
`Protect` çağırıyor" sorusunun cevabı grafın tahmin katmanı. §1'deki 0,807
ortalama güven tam olarak burada okunuyor.

**Graf ne dedi, grep ne dedi:**

| | Sonuç |
| --- | --- |
| Graf, üç düğümün birleşimi (`SecretProtector` + `.Protect()` + `.Unprotect()`) | **7 dosya** |
| `git grep -l SecretProtector -- src tests` | **12 dosya** |
| `git grep -n SecretProtector` (tüm depo) | 17 dosya / 43 satır — 5'i `graphify-out/` ve `docs/` gürültüsü |

Grafın **hiçbir düğümünde görünmeyen 5 dosya** ve satırları:

```
src/Bizigo.Alerting/AlertingServiceCollectionExtensions.cs:42:        services.TryAddSingleton(_ => new SecretProtector(
src/Bizigo.Api/Connectors/ChangeConnectorServiceCollectionExtensions.cs:32:        services.TryAddSingleton(_ => new SecretProtector(
tests/Bizigo.UnitTests/NotificationDispatcherTests.cs:46:        new SecretProtector(Key),
src/Bizigo.Contracts/Security/SecretProtectionOptions.cs:26:/// ... <see cref="SecretProtector"/> anahtarı
src/Bizigo.ControlPlane/ChangeConnectorEntities.cs:44:/// ... <c>Bizigo.Contracts.Security.SecretProtector</c> ile — T22'nin ...
```

Desen net: **3'ü `new SecretProtector(...)` nesne üretimi, 2'si doküman
yorumu.** Graf sınıfı *bildirilmiş tip* konumunda (parametre, alan) ve
*metodu çağrılınca* görüyor; `new` ile üretilince görmüyor.

Kaçırdığı ikisi DI kaydı — yani **sınıfın canlıda nasıl kurulduğu**. Anahtarı
oradan alıyor. Bir gizli-bilgi sınıfının kurulum noktasını göremeyen bir liste,
güvenlik incelemesi için yeterli değil.

> **Karar:** graf tüketici listesini **başlatır**, kapatmaz. `new X(` ve
> `ServiceCollectionExtensions` için grep şart.

### 2.2 · "`EventWriter.cs`'e dokunuyorum — ne kırılır?"

```
$ graphify affected src/Bizigo.Storage.ClickHouse/EventWriter.cs 2>/dev/null
Affected nodes for EventWriter.cs
Relations: calls, indirect_call, references, imports, imports_from, dynamic_import,
           re_exports, inherits, extends, implements, uses, mixes_in, embeds, requires
Depth: 2
- ScopedQuery [references] src/Bizigo.Query/ScopedQuery.cs:L54
- ReplayEngine [references] src/Bizigo.Replay/ReplayEngine.cs:L37
- ClickHouseEventSink [references] src/Bizigo.Storage.ClickHouse/ClickHouseEventSink.cs:L36
- ChangeOutOfScopeCountTests [references] tests/Bizigo.IntegrationTests/ChangeOutOfScopeCountTests.cs:L24
- CorrelationQueryTests [references] tests/Bizigo.IntegrationTests/CorrelationQueryTests.cs:L30
... (kırpıldı, toplam 102 satır)
exit=0
```

| | Sonuç |
| --- | --- |
| `affected` | **102 düğüm**, 41 ayrı dosyaya dağılmış (düğümlerin 78'i `tests/`, 24'ü `src/`) |
| `git grep -l EventWriter -- src tests ui` | **27 dosya** |
| Ortak | 23 dosya |
| **Yalnızca grafta** | **18 dosya** ← grafın kattığı değer |
| **Yalnızca grep'te** | **4 dosya** ← grafın kör noktası |

Grafın tek başına bulduğu 18 dosya iki adım ötedeki dolaylı bağımlılar:
`src/Bizigo.Api/Program.cs`, `src/Bizigo.Api/ReplayEndpoints.cs`,
`src/Bizigo.Replay/ReplayDiff.cs`, `tests/.../DevStackFixture.cs`… Bunları
grep'le bulmak elle BFS yapmak demekti. **Bu, grafın kazandığı yer.**

Grep'in bulup grafın bulamadığı 4 satır — dördü de ayrı bir sebeple:

```
src/Bizigo.Storage.ClickHouse/EventFieldKinds.cs:95:  [.. EventWriter.EventColumns.Where(...)]   <- statik üye erişimi
tests/Bizigo.IntegrationTests/EventRetentionChainTests.cs:106:  new EventWriter(_context),        <- nesne üretimi (§2.1'in aynısı)
tests/Bizigo.UnitTests/EventRetentionTests.cs:60:  private sealed class KabulEdenYazici : IEventWriter   <- grep gürültüsü (farklı sembol)
ui/tests/e2e/prepare.ts:153:  * (<c>... → EventWriter</c>)                                      <- yorum içi, üstelik dil sınırı ötesi
```

Dürüst sayım: **2 gerçek kaçırma** (statik üye + `new`), **2 grep yanlış
pozitifi**. `IEventWriter` ayrı bir semboldür ve grafın onu ayırması doğru
davranıştır.

Ama son satıra bakın: `affected` çıktısında **hiç `ui/` düğümü yok** — 78 test
+ 24 src, o kadar. C# ile TypeScript arası kenar sayısı grafta sıfır.

### 2.3 · "Grok derleyicisi ile ClickHouse yazıcısı arasında bağ var mı?"

```
$ graphify path "GrokCompiler" "EventWriter" 2>/dev/null
No directed path found between 'GrokCompiler' and 'EventWriter'.
Re-run with --undirected to search ignoring edge direction.
exit=0
```

Graf `directed: false`, `path` varsayılan yönlü. İlk cevap yanlış negatif:

```
$ graphify path "GrokCompiler" "EventWriter" --undirected 2>/dev/null
Shortest path (5 hops):
  GrokCompiler <--references [EXTRACTED]-- ParserCompiler
               <--references [EXTRACTED]-- PublishedParserLoader
               --references [EXTRACTED]--> ControlPlaneDbContext
               <--references [EXTRACTED]-- ScopedQuery
               --references [EXTRACTED]--> EventWriter
exit=0
```

**Grep karşılığı yok.** Bu soruyu grep'le cevaplamak, beş adımı elle
kovalamak demek — her adımda bir `git grep`, aradaki düğümü doğru tahmin
etmek şartıyla. Grafın grep'i açık farkla yendiği tek soru sınıfı bu.

Cevabın anlamına dikkat: yol beş hop ve ortasından `ControlPlaneDbContext`
geçiyor — bu bir *mimari bağ* değil, ikisinin de aynı hub'a değdiğinin
kanıtı. `path` "bağ var mı" sorusunu cevaplar, "bağ anlamlı mı" sorusunu
cevaplamaz.

### 2.4 · "Olay saklama süresi (TTL) nerede uygulanıyor?"

```
$ graphify query "olay saklama suresi TTL nerede uygulaniyor" 2>/dev/null
Graph: graphify-out/graph.json (8313 nodes) | Traversal: BFS depth=2 |
Start: ['olaylar/page.tsx', 'olaylar/loading.tsx', 'olay-detayi/index.md',
        '3 · "Derlendi ama koşmuyor" nerede yakalanır',
        '.Saklama_temizligi_taban_cizgisini_silmiyor()', ...] | 441 nodes found

[!] TRUNCATED: showing 61 of 441 nodes (~2000-token budget). ...

NODE olaylar/page.tsx [src=ui/src/app/olaylar/page.tsx loc=L1 ...]
NODE olaylar/loading.tsx [src=ui/src/app/olaylar/loading.tsx loc=L1 ...]
NODE olay-detayi/index.md [src=docs/epic/tickets-f2/olay-detayi/index.md ...]
NODE 3 · "Derlendi ama koşmuyor" nerede yakalanır [src=docs/epic/t32-derleme-... ]
... (kırpıldı)
exit=0
```

Gösterilen 61 düğümün dizin dağılımı:

```
ui 37 | tests 10 | src 7 | docs 5 | (kaynaksız) 2
```

**Cevap yanlış yerde.** `query` bir LLM sorgusu değil, soru metnindeki
dizgelerden başlayan BFS: Türkçe "olay" kelimesi `ui/src/app/olaylar/` rotasına
çarpmış ve arama oradan yayılmış. Gösterilen 61 düğümün 37'si UI sayfası.

Doğru cevap grep'te, iki satırda:

```
$ git grep -n "TTL " -- src db
db/clickhouse/0001_events.sql:79:TTL toDateTime(ts) + INTERVAL 90 DAY
src/Bizigo.Storage.ClickHouse/ClickHouseEventSink.cs:23:  /// (<c>db/clickhouse/0001_events.sql</c>: <c>TTL toDateTime(ts) + INTERVAL 90 DAY</c>).
```

`ClickHouseEventSink` — yani saklama politikasının gerçek sahibi — `query`
çıktısının **gösterilen kısmında hiç geçmiyor**. Ve asıl tanım
`0001_events.sql`'de; §1'de sayıldığı gibi grafta **hiçbir `.sql` dosyası
yok**, manifest'e bile girmemişler.

> **Karar:** `query` bir keşif aracı, cevap aracı değil. Başlangıç düğümleri
> (`Start: [...]`) sorunla ilgisizse gerisini okumaya değmez — soruyu kodda
> geçen İngilizce sembol adlarına yaklaştır ya da grep'e geç.

### 2.5 · "`AccessScope`'u hangi doküman anlatıyor?"

`AccessScope` grafın en merkezî düğümü (derece 150). Onu açıklayan tasarım
notunu bulmak isteyelim.

```
$ graphify explain "src_bizigo_contracts_accessscope_bizigo_contracts_accessscope" 2>/dev/null
Node: AccessScope
  Degree:    150
Connections (150):
  --> .ForGroups() [method] [EXTRACTED] src/Bizigo.Contracts/AccessScope.cs:L53
  ... (kırpıldı, 20 satır gösteriliyor + "and 130 more")
exit=0
```

150 komşunun tamamını `graph.json`'dan saydım:

```
komşu üst dizin : src 99 | tests 50 | (kaynaksız) 1
komşu uzantı    : .cs 149 | (yok) 1
markdown komşu  : 0
```

Grep aynı soruya saniyenin onda biriyle cevap veriyor:

```
$ git grep -l "AccessScope" -- docs
docs/epic/rca-raporu-ozelligi/index.md
docs/epic/t02-kararlar/index.md
docs/epic/t04-kararlar/index.md
docs/epic/t09-kararlar/index.md
docs/epic/tickets/kimlik/index.md
docs/graphify.md
```

Altı doküman `AccessScope`'u anlatıyor; graf **sıfırını** biliyor. §1'de
ölçüldüğü gibi 1.039 doküman düğümünün dışarıya toplam **2** kenarı var.

Bu bir hata değil, bir **tasarım sınırı**: markdown içindeki bir sınıf adı
AST'de bir sembol referansı değil, düz metin. Ama sonucu pratik: **"kararı
nerede tartıştık" sorusu grafta hiçbir zaman cevaplanmayacak.**

### 2.6 · "S02'nin syslog yayıcısını kim çağırıyor?"

Bu soru §3'ün konusu. Ayrı bölüm hak ediyor, çünkü graf burada "bilmiyorum"
demiyor — **yanlış cevap veriyor.**

---

## 3 · Grafın yanıldığı yer

### 3.1 · Yokluğu bildiren hâl

`SyslogEmitter`, `85343ff` commit'iyle gelen `sim/Bizigo.Simulators/`
projesinin göbeğinde. Graf `cca19da5`'te derlendi ve o an `sim/` ağacı boştu.

```
$ graphify explain "SyslogEmitter" 2>/dev/null
No node matching 'SyslogEmitter' found.
exit=0

$ git grep -l "SyslogEmitter"
docs/epic/fs-simulatorler/index.md
sim/Bizigo.Simulators/Program.cs
sim/Bizigo.Simulators/SyslogEmitter.cs
tests/Bizigo.UnitTests/EventRetentionTests.cs
```

Bu hâli görmek kolay: cevap boş, şüphelenirsin. **Asıl tehlikeli olan öteki
hâl.**

### 3.2 · Yokluğu bildirmeyen hâl — dolu ama yanlış cevap

Aynı konuyu bir ajanın soracağı gibi soralım:

```
$ graphify query "SyslogEmitter syslog emitter simulator" 2>/dev/null
Graph: graphify-out/graph.json (8313 nodes) | Traversal: BFS depth=2 |
Start: ['.RenderSyslog()', '.TryParseSyslog()',
        '.Gercek_syslog_satiri_ayristirilir()',
        '.Syslog_zaman_damgasi_gelecege_dusmez()'] | 102 nodes found

[!] TRUNCATED: showing 57 of 102 nodes (~2000-token budget). ...

NODE .RenderSyslog() [src=src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs loc=L183 community=.Rewrite]
NODE .TryParseSyslog() [src=src/Bizigo.Parsing/Engine/StepExecutors.cs loc=L507 community=ParseContext]
NODE .Gercek_syslog_satiri_ayristirilir() [src=tests/Bizigo.UnitTests/GrokPatternLibraryTests.cs loc=L59 ...]
NODE SampleTimeRewriter [src=src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs loc=L42 community=.Rewrite]
... (kırpıldı, 57 düğüm)
exit=0
```

102 düğümlük, kendinden emin, biçimi kusursuz bir cevap. İçinde:

**Bir:** aranan sınıf yok. `sim/Bizigo.Simulators/SyslogEmitter.cs` çıktının
hiçbir yerinde geçmiyor — çünkü graf o dosyayı hiç görmedi.

**İki:** ilk satırdaki dosya **artık depoda yok.**

```
$ ls src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs
ls: src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs: No such file or directory
exit=1

$ ls src/Bizigo.Parsing/Samples/SampleTimeRewriter.cs
-rw-r--r--  8.8K   src/Bizigo.Parsing/Samples/SampleTimeRewriter.cs

$ git log --oneline --diff-filter=D -- src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs
7c74ced Count the rows ClickHouse says it wrote but silently erased (S02a)
```

Yani graf, **var olmayan bir dosya yolunu gerçek bir bulgu gibi** listenin
başına koydu. Hata yok, uyarı yok, sayaç yok — `CLAUDE.md` §7'nin tarif ettiği
sessiz yanlış davranışın tam kendisi. Bu satırı bir rapora kopyalayan kişi
başkasını olmayan bir dosyaya yollar.

### 3.3 · Kör noktanın büyüklüğü

Tek dosya değil. HEAD ile grafın dosya kümeleri karşılaştırıldığında:

```
HEAD'de var, grafta YOK : 24 dosya
  sim   6   (SyslogEmitter dahil, projenin tamamı)
  docs  9
  tests 4   (EventRetentionTests, SimulatorProfileTests, ...)
  ui    3   (playwright.config.ts, tests/e2e/*)
  src   1   (SampleTimeRewriter'ın yeni yeri)
  catalog 1

grafta var, HEAD'de YOK : 2
  src/Bizigo.Cli/Seeding/SampleTimeRewriter.cs   <- taşındı
  src                                            <- artık karşılığı olmayan düğüm
```

36 commit, 24 görünmeyen dosya, 2 hayalet yol. Sayı küçük görünüyor ama
dağılımı kötü — hangi commit'in eklediğini saydım:

```
$ git log --oneline --diff-filter=A -1 -- <dosya>
sim/Bizigo.Simulators/* (6)              85343ff  S02
EventRetentionChainTests.cs              7c74ced  S02a
EventRetentionTests.cs                   7c74ced  S02a
SimulatorFixtureChainTests.cs            0de3beb  S01
SimulatorProfileTests.cs                 5af095c  S01
ui/playwright.config.ts, tests/e2e/*     12c91c0  T27
```

**Grafın göremediği 24 dosyanın 8'i son iki ticket'ın (S02, S02a) ürettiği kod
ve testler**, 2'si daha S01'in simülatör işinin. Yani kör nokta rastgele
dağılmamış: tam da şu an sorulacak soruların konusuna oturmuş.

### 3.4 · Kural

> Grafın "bulunamadı" cevabı bir bulgu değil.
> Grafın **dolu cevabı da** tek başına bir bulgu değil.
>
> İkisi de `git grep` ile teyit edilene kadar hipotez.

Ucuz teyit, her oturumun başında:

```bash
python3 -c "import json;print(json.load(open('graphify-out/graph.json'))['built_at_commit'])"
git rev-list --count <o_commit>..HEAD
```

---

## 4 · Çıkış kodları — kendim ölçtüm

`graphify`'ın "bulamadım" hâlleri **birbirinin aynısı değil**. Üçü de aynı
oturumda ölçüldü:

| Komut | Mesaj nereye | Çıkış kodu |
| --- | --- | --- |
| `graphify path "SyslogEmitter" "EventWriter" --undirected` | **stderr** | **1** |
| `graphify explain "HicBoyleBirSinifYok"` | stdout | **0** |
| `graphify affected "sim/Bizigo.Simulators/SyslogEmitter.cs"` | stdout | **0** |

Ölçüm:

```
$ graphify path "SyslogEmitter" "EventWriter" --undirected >/tmp/o.txt 2>/tmp/e.txt
exit=1
stdout: (boş)
stderr: No node matching 'SyslogEmitter' found.

$ graphify explain "HicBoyleBirSinifYok" 2>/dev/null
No node matching 'HicBoyleBirSinifYok' found.
exit=0

$ graphify affected "sim/Bizigo.Simulators/SyslogEmitter.cs" 2>/dev/null
No unique node match for sim/Bizigo.Simulators/SyslogEmitter.cs
exit=0
```

Buradan çıkan **somut tuzak** — ve bu belgenin en başındaki alışkanlıkla
doğrudan çatışıyor:

```
$ graphify path "SyslogEmitter" "EventWriter" --undirected 2>/dev/null
$ echo $?
1
```

`2>/dev/null` sürüm uyarısını susturmak için konuluyor; `path` için **tek
tanısal satırı** da susturuyor. Ekranda hiçbir şey kalmıyor. Betikte
`$?` bakılmıyorsa "yol yok" ile "düğüm yok" ayırt edilemez hâle geliyor.

`explain`/`affected` tersi: konuşuyorlar ama **exit=0** dönüyorlar; bir CI
adımı bunu başarı sayar.

> **Pratik kural:** `path` için stderr'i açık bırak ya da `$?` oku;
> `explain`/`affected` için çıkış kodunu değil **çıktı metnini** kontrol et.

---

## 5 · Bu demo neyi koşturmadı

`CLAUDE.md` §2 ve §6 gereği açıkça yazıyorum:

- **`graphify update` / `cluster-only` / `label` koşturulmadı.** Makine yüklü
  (swap %87). Grafı tazelemek §3'teki 24 dosyalık kör noktayı kapatırdı; bu
  demo onu kapatmayı değil **ölçmeyi** amaçlıyor.
- **Hiçbir Docker/entegrasyon testi koşturulmadı.** §2.2'deki `affected`
  listesinde 78 test dosyası var; hiçbiri koşturulmadı, yalnızca adları
  sayıldı.
- **Süreler benchmark değil.** Yüklü makinede, tek koşum, ısınma yok:

  ```
  graphify explain SecretProtector      0,43 s
  graphify affected .../EventWriter.cs  0,16 s
  graphify query "olay saklama suresi"  0,37 s
  graphify god-nodes                    0,17 s
  git grep -l SecretProtector           0,05 s
  ```

  `docs/graphify-kullanim.md` aynı `affected` için 0,873 s ölçmüştü. Aradaki
  fark aracı değil **makinenin gürültüsünü** ölçüyor (§6). Bağlayıcı sayı
  isteyen sessiz makinede tekrar ölçsün. Buradan çıkarılacak tek şey büyüklük
  mertebesi: **her iki araç da yüz milisaniyeler düzeyinde, hangisini
  koşturacağını maliyete bakarak seçmeye gerek yok — ikisini birden koştur.**

---

## 6 · Demonun tek cümlelik özeti

Altı sorunun karnesi:

| Soru | Kazanan | Neden |
| --- | --- | --- |
| §2.1 `SecretProtector`'ı kim kullanıyor | grep (12 vs 7 dosya) | `new X(...)` ve DI kaydı grafta yok |
| §2.2 `EventWriter.cs`'e dokunursam ne kırılır | **graf** (18 dosya fazladan) | geçişli kapanış; grep tek adım görür |
| §2.3 Grok ↔ ClickHouse bağı var mı | **graf** (5 hop) | grep'in cevabı yok |
| §2.4 TTL nerede uygulanıyor | grep (2 satır) | doğru cevap `.sql`'de, graf `.sql` görmüyor |
| §2.5 `AccessScope`'u hangi doküman anlatıyor | grep (6 dosya vs 0) | `docs/` → kod kenarı toplam 2 |
| §3 `SyslogEmitter`'ı kim çağırıyor | **ikisi de değil** | graf dolu ve yanlış cevap verdi |

Graf, **ilişki** sorularında grep'in yapamayacağını yapıyor; **varlık** ve
**tazelik** sorularında grep'in yerini almıyor ve almaya kalktığında sessizce
yanılıyor. Bu demo ikisini de aynı komutlarla gösterdi.
