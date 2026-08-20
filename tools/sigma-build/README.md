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
| Kapı 2 — `EXPLAIN` (kendi CI işinde) | ⏳ |
| Manifest, takaslı yazım, sürüklenme kapısı | ⏳ |
| Gerçek derleme | ⏳ T31'i bekliyor |

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

## Koşturmadığım test

`tests/Bizigo.IntegrationTests/SigmaViewColumnsTests.cs` türetilmiş kümeyi canlı
ClickHouse'un `system.columns` çıktısıyla karşılaştırıyor. **Yazıldı,
koşturulmadı** (§2: ajanlar Docker'a dokunmaz).

Koşturulduğunda kanıtlayacağı şey, birim testlerinin kanıtlayamadığı: bu modülün
SQL metninden anladığı ile **ClickHouse'un aynı metinden anladığı** aynı şey.
