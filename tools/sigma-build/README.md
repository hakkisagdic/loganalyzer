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
| Kapı 1 — kural SQL'i var olmayan kolona gidiyor mu | ⏳ sırada |
| Kapı 2 — `EXPLAIN` (kendi CI işinde) | ⏳ |
| Manifest, takaslı yazım, sürüklenme kapısı | ⏳ |
| Gerçek derleme | ⏳ T31'i bekliyor |

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

### Ölçülmüş: kapı kırmızı yanabiliyor

Beş mutasyon uygulandı, her biri en az bir testi düşürdü, sonra geri alındı:

| Mutasyon | Düşen test |
| --- | --- |
| `IF NOT EXISTS` atlaması kaldırıldı | 1 |
| Göç sırası ters çevrildi | 5 |
| Parantez derinliği sayılmadı (naif virgül bölmesi) | 12 |
| Ad kuralı zorlaması kaldırıldı | 1 |
| Adsız ifade sessizce kabul edildi | 1 |

## Koşturmadığım test

`tests/Bizigo.IntegrationTests/SigmaViewColumnsTests.cs` türetilmiş kümeyi canlı
ClickHouse'un `system.columns` çıktısıyla karşılaştırıyor. **Yazıldı,
koşturulmadı** (§2: ajanlar Docker'a dokunmaz).

Koşturulduğunda kanıtlayacağı şey, birim testlerinin kanıtlayamadığı: bu modülün
SQL metninden anladığı ile **ClickHouse'un aynı metinden anladığı** aynı şey.
