# Sigma derleme hattı (T32)

Sigma kurallarını **derleme zamanında** ClickHouse SQL'ine çevirip repoda
versiyonlayan hat. Sıcak yolda Python yok; üretilen SQL depoda duruyor.

Tasarımın tamamı ve kararların gerekçeleri:
[`docs/epic/t32-derleme-tasarimi/index.md`](../../docs/epic/t32-derleme-tasarimi/index.md).

## Bugün ne var

| Parça | Durum |
| --- | --- |
| `view_columns.py` — görünüm kolon kümesini göçlerden türetir | ✅ |
| `detections/schema/view-columns.json` — türetilmiş küme, versiyonlu | ✅ |
| `gate.py` — Kapı 1: kural SQL'i var olmayan kolona gidiyor mu | ✅ |
| `manifest.py` — manifest, takaslı yazım, sürüklenme kapısı | ✅ |
| `compile.py` — tek komut (`--write` / `--check` / `--summary`) | ✅ |
| `explain_gate.py` — Kapı 2: `EXPLAIN`, kendi CI işinde | ✅ **koşturuldu** — ilk koşum kapının kendi kusurunu buldu |
| `ruleset.py` — kural seti çivisi, ağsız doğrulama | ✅ |
| Kural setinin **yükseltme** yolu (ağ) | ⏳ kapsam kararını bekliyor |
| Gerçek derleme | ⏳ T31'i bekliyor |

## Kural seti depoda duruyor, indirilmiyor

Kurallar `catalog/sigma/rules/` altında kopyalı, çivi `catalog/sigma/ruleset.json`.
CI **ağa çıkmıyor**; tek yaptığı kopyanın çiviye uyduğunu doğrulamak.

```bash
python -m sigma_build.ruleset            # çivinin durumu
python -m sigma_build.ruleset --verify   # CI kapısı, ağsız
```

Üç gerekçe:

1. **Ağ, kapının gerekçesi olamaz.** `ci.yml`'ın başında yazılı: `setup-dotnet`
   `codeload.github.com`'dan iniyordu ve GitHub sınırlandırınca iş kurulumda
   ölüyordu — ilgisiz bir hata mesajıyla, tek oturumda üç kez.
2. **Ticket'ın kendi gerekçesi.** "Proje terk edilse bile mevcut kurallar
   çalışır" ancak kaynak kurallar da depodaysa tam: aksi hâlde SQL kalır ama
   **yeniden üretilemez**, yani sürüklenme kapısı da koşamaz.
3. **Kapsam bir liste olmalı, bir filtre değil.** Yukarı akışa karşı çalışan bir
   filtre, yukarı akış kural eklediğinde korpusu sessizce değiştirir.

Doğrulama üç sürüklenme yönünü **ayrı** raporluyor, çünkü üçünün cevabı farklı:
eksik dosya (kopyalama yarım), fazla dosya (çiviye girmemiş kural — derlenir ama
nereden geldiği kayıtsız), değişmiş içerik (elle düzenlenmiş).

Bugün çivi boş: `commit: null`, sıfır kural. Bu "kural yok" değil **"hangi
sürümden alacağımıza karar verilmedi"** — T30'un kapsam ölçümünü bekliyor.
Yükseltme yolu bilerek yazılmadı; var olmayan bir komutu mesajlarda anmak, bu
turda bulunan hatanın (`unmapped_expression()` yazılmış, hiç çağrılmamış) aynısı
olurdu.

## Üç olay birbirinden ayrılıyor

`python -m sigma_build.compile --summary` iki koşum arasındaki farkı `HEAD`'deki
manifest'e göre veriyor:

| Alan | Ne söylüyor |
| --- | --- |
| `ruleset_changed` + `source_changed` | Kural seti yükseltildi, şu kuralların kaynağı oynadı |
| `pipeline_changed` | Eşleme değişti — bütün çıktılar yenilendi |
| `output_changed_without_source_change` | **Kaynağı oynamadan anlamı oynayan kurallar** |

Üçüncüsü asıl bekçi: bir kural seti yükseltmesinde boş olması beklenir, dolu
çıkarsa yükseltmeyle birlikte başka bir şey daha değişmiştir ve iki değişiklik
tek diff'te saklanmıştır.

## Kapı 2 kendini sınıyor

`detections/sigma/` bugün boş, yani Kapı 2 sıfır sorgu sorup sessizce yeşil
kalırdı — ve "sessizce yeşil bekçi" bu deponun adını koyduğu sınıf. CI işi bu
yüzden önce `--self-test` koşturuyor: sonucu **bilinen** üç sorgu, ikisi
reddedilmeli, biri kabul edilmeli.

```bash
python -m sigma_build.explain_gate --self-test --clickhouse-url http://localhost:8123
python -m sigma_build.explain_gate --clickhouse-url http://localhost:8123
```

Kırmızı yanabildiği **her koşumda** ölçülüyor, bir kez değil.

### İlk koşum kapının kendi kusurunu buldu

Sınav ilk kez koşturulduğunda iki reddin de **kabul** geldiğini bildirdi. İlk
okuyuşta "tipler tuttu, iddia çürüdü" gibi görünüyor. Değildi:

| Sorgu | `EXPLAIN SYNTAX` | `EXPLAIN` |
| --- | --- | --- |
| `connection_info_protocol_name=6` | kabul | **red — Code 386 `NO_COMMON_TYPE`** |
| `src_endpoint_ip ILIKE '203.0.113.%'` | kabul | **red — Code 43 `ILLEGAL_TYPE_OF_ARGUMENT`** |
| sağlam sorgu | kabul | kabul |

`EXPLAIN SYNTAX` **tip denetimi yapmıyor** — yalnızca AST'yi yeniden yazıyor.
Yani kapı, kural seti geldiğinde 24 kuralın hepsini geçirecek ve ikisi üretimde
patlayacaktı; `KIND_TYPE_MISMATCH` kolu o yoldan asla tetiklenemezdi. Kapı 2'nin
kapatmak için var olduğu sınıfın kendisi, kapının içinde.

Aynı koşum `0001_events.sql`'den okunan tipleri de doğruladı (`toTypeName` →
`LowCardinality(String)`, `IPv6`) ve sınıflandırmada ikinci bir boşluk açtı:
`NO_COMMON_TYPE`'ın metni hiçbir desene uymuyordu, yani tip uyuşmazlığı
`remedy: unknown` diye etiketleniyordu — **kapanabilir bir iş kalemi "kapanır mı
bilmiyoruz" kutusunda.** Desen eklendi, testi ölçülen metinle yazıldı.

Varsayılan artık `EXPLAIN`. Biçim seçimi bir daha tahminle yapılmasın diye
`--probe-forms` adayları yan yana ölçüyor ve CI her koşumda basıyor:

```bash
python -m sigma_build.explain_gate --probe-forms --clickhouse-url http://localhost:8123
```

`EXPLAIN SYNTAX` aday listesinde **duruyor** — "denedik, olmadı" bilgisini
silmek, bir sonraki kişinin aynı seçimi aynı gerekçeyle yapmasına kapı açardı.

Hata sınıflandırması güvenli tarafa bozuluyor: desenler ClickHouse sürümüyle
bayatlayabilir, ve bayatladıkları gün tanınmayan hata yine bir engel üretiyor —
kayıp `kind` çözünürlüğünde, kuralın kapıdan geçmesinde değil. Bağlantı hatası
ise engel değil **istisna**: ortam bozukken "bütün kurallar kırık" yazdırmak
ölçüm aracının kendi sessiz yanlışı olurdu.

## `gated` tek sayı değil

| Sayı | Anlamı |
| --- | --- |
| `gated_closeable` | Azalması **beklenen** — biri kapatabilir |
| `gated_upstream` | Yukarı akış/backend değişmeden kapanmaz |

Tek sayı olsaydı "liste boşaldı mı" sorusunun cevabı asla evet olamazdı
(§8'in `Pending`/`Exempt` ayrımı). `upstream` hiçbir sınıflandırıcı tarafından
kendiliğinden atanmıyor; muafiyet gibi bilinçli bir hareket. `unknown`
kapanabilirler tarafında duruyor — "kapanamaz" ile "kapanır mı bilmiyoruz" aynı
şey değil ve bilinmeyeni muafiyete yazmak işi gizlerdi.

Bir kural ancak engellerinin **hepsi** kapanabiliyorsa kapanabilir sayılıyor.

## Kapı bugünden koşuyor, hat bitmeden

`detections/sigma/manifest.json` şu an sıfır kural taşıyor ve
`run.pipeline_version` `null`. Bu **bugünkü doğru durum**: sıfır Sigma kuralı
derleniyor, ve sebebi "hiç kural yok" değil "henüz derlemiyoruz" — manifest
ikisini karıştırmıyor.

Kapı yine de CI'da. Sebep bu turda ölçüldü: `bizigo_pipeline.py`
`UNMAPPED_FIELDS`'ı tanımlamış ama hiçbir dönüşüme vermemişti,
`unmapped_expression()` yazılmış ama hiç çağrılmamıştı. **Hazırlanmış ama
bağlanmamış**, ve bağlanmamış olması hiçbir yerde belirti üretmiyordu — 24
kuralın 8'i sessizce koşmuyordu. Kapıyı "hat bitince bağlarız" diye bekletmek
aynı deseni bir kez daha kurardı.

T31 geldiğinde değişen tek yer `collect_outcomes()`; kapı ve yazım dokunulmadan
kalıyor, ve kuralların belirmesi bir git diff'i olarak görünüyor.

## Kapı 1 — ne yakalıyor, ne yakalamıyor

Kolon **varlığına** bakıyor. Örneklemdeki 24 gerçek sorguda ölçüldü:
**8 kural takılıyor, 16'sı geçiyor.**

| Kural | Engel |
| --- | --- |
| `fortigate_blocked_category` | `url` |
| `fortigate_dns_tunnel` | `dns_query_name` |
| `nginx_admin_path` | `url` |
| `nginx_dns_rebind` | `query` |
| `nginx_large_upload` | `http_method`, `url` |
| `nginx_scanner_agent` | `user_agent` |
| `nginx_sqli_probe` | `url` |
| `routeros_dns_request` | `dns_query_name` |

**Yakalayamadığı:** tip uyuşmazlığı — kolon var, karşılaştırma geçersiz.
Örneklemde iki tane var ve ikisi de Kapı 1'i **geçiyor**, geçmeleri de doğru:

- `connection_info_protocol_name=6` — kolon `LowCardinality(String)`
- `src_endpoint_ip ILIKE '203.0.113.%'` — kolon `IPv6`, `ILIKE` String istiyor

Kapı 1'in bunları yakalaması ancak kolon tiplerini de modellemesiyle olurdu,
yani ClickHouse'un yarısını yeniden yazmakla. Yakalayan yer Kapı 2. İki kapının
neden ayrı olduğu bu; biri diğerinin yerine konursa yakalanmayan sınıf sessiz
kalır.

`8 + 2 = 10`, ve `compiled=24 / runs=14`. Örneklem tam olarak açıklandı.

## Ad çıkarımı gürültülü tarafa yanılıyor

SQL'den kolon adı çıkarmak tam bir ayrıştırıcı olmadan kesin değil. Bilinmeyen
bir ClickHouse anahtar sözcüğü kolon sanılabilir — ve bu **kasıtlı olarak**
kabul edilen taraf:

| Yanılma | Sonuç |
| --- | --- |
| Anahtar sözcük kolon sanıldı | Kural reddedilir, rapora düşer — **gürültülü**, listeye bir sözcük eklenir |
| Kolon referansı görülmedi | Kural kapıdan **geçer**, çalışma zamanında kırılır — **sessiz** |

Yanılma yönünün kendisi bir testle çivili (`test_bilinmeyen_sozcuk_gurultulu_tarafa_yaniliyor`).

## Neden ayrı bir dizin, `sidecar/` değil

`sidecar/app/` **koşan bir servis**; burası bir **yapı aracı**. Aynı imaja
girmiyor, aynı ömrü yaşamıyor.

Ama pySigma sürümleri ortak: `requirements.txt` doğrudan
`sidecar/requirements.txt`'e işaret ediyor. Gerekçe, sidecar'ın
`/v1/sigma/compile` ucunun UI'da "bu kuralı derle ve önizle" akışını
beslemesi — iki taraf ayrı sürüm sabitlerse ekranın gösterdiği SQL ile
build-time üretilen SQL ayrışır ve **ayrıştıklarında hiçbir şey kırmızı
yanmaz.**

## Kolon kümesi neden türetiliyor

Kapı 1, bir Sigma kuralının ürettiği SQL'in görünümde olmayan bir kolona gidip
gitmediğine bakıyor. Karşılaştıracağı liste elle yazılsaydı `CLAUDE.md` §7'deki
`Produces<T>` deliğinin aynısı doğardı. Sürüklenme burada **asimetrik**:

| Sürüklenme | Sonuç |
| --- | --- |
| Görünüme kolon eklendi, liste güncellenmedi | Kural yanlışlıkla reddedilir — **gürültülü**, fark edilir |
| Görünümden kolon çıktı, liste güncellenmedi | Kural kapıdan **geçer**, çalışma zamanında kırılır — **sessiz** |

Küme `db/clickhouse/*.sql` göçlerinden **sırayla** uygulanarak çıkarılıyor. Sıra
teorik bir kaygı değil: `0004`, `events_otel`'i `DROP` + `CREATE` ile yeniden
yaratıyor. Sıra `ClickHouseMigrator.cs`'ninkiyle aynı (ordinal, dosya adına
göre) — başka türlüsü canlıdan farklı bir gerçeklik modellemek olurdu.

`CREATE VIEW IF NOT EXISTS` bir yeniden tanım **değil**: görünüm zaten varsa
ClickHouse ifadeyi atlar, çıkarıcı da atlıyor. `0003` tam olarak bu biçimi
kullanıyor.

## Kullanım

```bash
cd tools/sigma-build

# Kolonları oku
python -m sigma_build.view_columns                       # hepsi
python -m sigma_build.view_columns --view events_ocsf --json

# Türetilmiş dosyayı yeniden üret / kapıyı koştur
python -m sigma_build.view_columns --write
python -m sigma_build.view_columns --check               # CI kapısı
```

`--check` depodaki dosyaya **dokunmuyor** — üzerine yazsaydı, düşen bir kapıdan
sonra aynı komutun ikinci koşumu sebepsiz yere geçerdi
(`ui/scripts/generate-api-types.sh` aynı gerekçeyi taşıyor).

## Testler

```bash
python3.13 -m venv .venv
.venv/bin/pip install 'pytest==9.1.1'
.venv/bin/python -m pytest
```

`view_columns.py` **hiçbir bağımlılık istemiyor** — saf metin işi. Kapı 1'in
ClickHouse'suz ve pySigma'sız koşabilmesinin sebebi bu.

CI'da `sigma-build` işi bu testleri ve `--check` kapısını koşturuyor.

Testler pySigma **istemiyor**: `tests/fixtures/generated-sql-sample.json`
prototipin 24 kuralının gerçek backend çıktısını donmuş olarak taşıyor.
Prototip "atılabilir" işaretli ve testler kurulum durumuna bağlanmamalı.
T31'in kalıcı pipeline'ı geldiğinde örneklem yenilenir.

### Ölçülmüş: kapılar kırmızı yanabiliyor

On mutasyon uygulandı, her biri en az bir testi düşürdü, sonra geri alındı.

`view_columns.py`:

| Mutasyon | Düşen test |
| --- | --- |
| `IF NOT EXISTS` atlaması kaldırıldı | 1 |
| Göç sırası ters çevrildi | 5 |
| Parantez derinliği sayılmadı (naif virgül bölmesi) | 12 |
| Ad kuralı zorlaması kaldırıldı | 1 |
| Adsız ifade sessizce kabul edildi | 1 |

`gate.py`:

| Mutasyon | Düşen test |
| --- | --- |
| Metin sabitleri elenmedi | 12 |
| Fonksiyon adları elenmedi | 1 |
| Tablo adı elenmedi | 16 |
| Anahtar sözcükler elenmedi | 15 |
| Kapı her şeyi geçirdi | 6 |

`manifest.py`:

| Mutasyon | Düşen test |
| --- | --- |
| Takas geri alma kaldırıldı | 1 |
| Pipeline olayı gizlendi (`output_changed_without_source_change`) | 1 |
| `gated` kural da dosya üretti | 3 |
| Kapı hedefin üstüne yazdı | 6 |
| Manifest sırası girdiye bağlandı | 1 |
| Manifest'e derleme tarihi eklendi | 1 |

`ruleset.py`:

| Mutasyon | Düşen test |
| --- | --- |
| Çivide olmayan kural görmezden gelindi | 2 |
| İçerik özeti karşılaştırılmadı | 2 |

Canlı kapı da ölçüldü: `detections/sigma/` içine bayat bir dosya bırakıldığında
ve manifest elle bozulduğunda `--check` çıkış kodu 1 veriyor, geri alınınca 0.

## Derleme tarihi neden hiçbir yerde yok — ticket'tan bilinçli sapma

Ticket "kural kimliği, kaynak sürümü ve **derleme tarihi** ile birlikte" diyor.
Üçü birden olamıyor:

- Kriter A: *aynı girdi, aynı SQL.*
- Kriter B: *çıktı depodakiyle aynı değilse CI düşer.*
- Derleme tarihi: her koşumda değişir.

Tarihi kural dosyalarına koymak kapıyı 269 dosyada öldürüyor; manifest'e koymak
**manifest'in kendi kapısını** öldürüyor, çünkü manifest de karşılaştırılan
çıktının parçası. Kalan tek yol kapının o alanı görmezden gelmesi — yani kapıyı
yumuşatmak.

Tarih atıldı çünkü **git zaten tutuyor ve daha güvenilir tutuyor**:
`git log detections/sigma/manifest.json`. Kaybedilen bilgi yok; kaybedilen tek
şey aynı bilginin ikinci, sürüklenebilir kopyası. Girdinin parçası olan sürümler
(`ruleset_commit`, `pipeline_version`, `pipeline_sha`) duruyor.

## Koşturmadığım test

`tests/Bizigo.IntegrationTests/SigmaViewColumnsTests.cs` türetilmiş kümeyi canlı
ClickHouse'un `system.columns` çıktısıyla karşılaştırıyor. **Yazıldı,
koşturulmadı** (§2: ajanlar Docker'a dokunmaz).

Koşturulduğunda kanıtlayacağı şey, birim testlerinin kanıtlayamadığı: bu modülün
SQL metninden anladığı ile **ClickHouse'un aynı metinden anladığı** aynı şey.
