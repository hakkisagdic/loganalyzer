# bizigo-loganalyzer

Plugin tabanlı, çok formatlı ve çok dilli log analiz platformu. Ağ/altyapı cihazı
logları birincil alan; agentic katmanla proaktif araştırma ve kök neden analizi.

**Durum:** F1 (boru hattı) — T01 iskelet, T02 depolama/kapsam, T03 ingest boru hattı,
T04 ham arşiv, T05 parser motoru ve T06 dispatcher tamamlandı; T07'den devam ediliyor.

Planlama belgeleri Traycer epic'inde:
`mimari-kararlar` · `f1-teknik-plan` · `rca-raporu-ozelligi` · `tickets/`

> ⚠️ **Geliştirme aşamasında.** `deploy/.env.example` ve `appsettings.json` içindeki
> kimlik bilgileri (`bizigo/bizigo`, `admin/admin`) yalnızca yerel compose içindir.
> Üretimde kullanılmaz.

## Hızlı başlangıç

```bash
# 1. Geliştirme ortamı
cd deploy && cp .env.example .env && docker compose up -d --wait && cd ..

# 2. Derle ve test et
dotnet build
dotnet test tests/Bizigo.UnitTests            # container gerektirmez
dotnet test tests/Bizigo.IntegrationTests     # Testcontainers ile ayrı container'lar

# 3. API'yi çalıştır (göçleri açılışta uygular)
dotnet run --project src/Bizigo.Api
```

| Servis | Adres | Not |
| --- | --- | --- |
| ClickHouse | http://localhost:8123 | `bizigo` / `bizigo` |
| PostgreSQL | localhost:5432 | `bizigo` / `bizigo` — Keycloak da bu örneği kullanır |
| RustFS (S3) | http://localhost:9000 | konsol :9001, `bizigoadmin` / `bizigoadmin` |
| Keycloak | http://localhost:8180 | `admin` / `admin`, realm `bizigo` |
| OTel Collector | syslog TCP :5140, UDP :5141, OTLP :4318 | |
| Sidecar | http://localhost:8099 | Drain3 + pySigma (T12) |

## Mimarinin özeti

Yazmadıklarımız: **collector** (OTel Collector + Fluent Bit), **depolama**
(ClickHouse), **detection kuralları** (SigmaHQ), **ortak şema** (OCSF + OTel semconv),
**IdP** (Keycloak), **object storage** (RustFS).

Yazdıklarımız: plugin host, YAML parser motoru, normalizasyon, sorgu API'si,
agent katmanı, RCA kanıt toplayıcı.

İki genişleme ekseni plugin (parser + senaryo), üçüncüsü kanıt sağlayıcıları.

## Bilinmesi gerekenler

**Hedef çatı: `net10.0` (LTS).** ⚠️ **SDK yolu:** `dotnet` PATH'te
`/usr/local/share/dotnet` çözülüyor ve orada yalnızca SDK 8 ve 9 var. arm64 SDK
**10.0.302** `~/.dotnet` altında. İki seçenek:

```bash
# Kalıcı (önerilen): ~/.dotnet PATH'te önce gelsin
export PATH="$HOME/.dotnet:$PATH"

# Ya da her komutta tam yol
~/.dotnet/dotnet build
```

`global.json` 10.0.302'yi istiyor; yanlış `dotnet` ile çalıştırılırsa net bir hata
verir, sessizce SDK 9'a düşmez.

**Türkçe kültür tuzağı.** `tr-TR`'de `ToLower()` `I` harfini `ı` yapar ve
`INTERFACE` gibi kelimeler aramada sessizce eşleşmez. `.editorconfig`'de
CA1304/1305/1307/1310/1311/1862 **hata** seviyesinde — kültür duyarlı bir çağrı
derlemeyi kırar. `ToLowerInvariant()` / `StringComparison.Ordinal` kullanın.

**Ham veri her şeyden önce gelir.** Ack, ham batch yerel WAL'a yazılıp fsync
edildikten sonra verilir. RustFS 1.0-rc olduğu için dayanıklılık sınırı bilinçli
olarak WAL'da: object storage veri kaybederse en kötü senaryo "eski arşivin bir
kısmı", "yeni veri" değil. Manifest (`raw_manifest`) nesne kaybını görünür kılar.

**Collector'da `encoding: iso-8859-1`.** syslog receiver'ın varsayılanı `utf-8` ve
geçersiz baytları **U+FFFD ile değiştiriyor** — `protocol: none` tek başına ham
sadakati korumuyor. `nop` denendi ve **çalışmıyor**: UDP'de varsayılan
`line_end_pattern` yüzünden hata veriyor, TCP'de ise syslog çerçevelemesini yok
edip tüm akışı tek kayda çeviriyor (çökme yok — sessizce bozuk veri).
`iso-8859-1` bayt ↔ kod noktası eşlemesini birebir ve tersinir yapar; satır bölme
korunur ve baytlar bizde `Latin1.GetBytes` ile aynen geri alınır. Tel kodlaması
`bizigo.wire_encoding` özniteliğiyle açıkça bildirilir. Kodlama tespiti bizim
tarafımızda (BOM → bildirilen → UTF-8 doğrulaması → kaynağın yedek kod sayfası →
latin1), sonuç NFC normalize edilir.

**WAL yükü ile arşiv satırı aynı formattır.** Tek NDJSON codec'i
(`RawRecordCodec`): yükleyici dönüştürmez, kopyalar. `owner_group`/`source_id`
WAL aşamasında boş yazılır — alanın varlığı formatın parçası, değeri değil.

**ClickHouse şeması elle yazılmış SQL.** `db/clickhouse/NNNN_ad.sql`, sırayla
uygulanır, `schema_migrations` izler. **Uygulanmış bir dosya değiştirilirse göç hata
verir.** Değişiklik yeni dosyayla yapılır. Gerekçe: ClickHouse DDL'i (ORDER BY,
PARTITION BY, skip index, CODEC) ilişkisel göç araçlarına sığmıyor ve şemanın
kendisi bir karar belgesi.

**Kontrol düzlemi Postgres, veri düzlemi ClickHouse.** Değişken operasyonel durum
(envanter, katalog, manifest, audit) ClickHouse'a yazılmaz.

**Dispatcher'ın kademe sırası performans için değil doğruluk için.** Envanter bağı
(`source_id → parser_id`) hem en hızlı hem en güvenilir yol: cihazın ne gönderdiğini
tahmin etmek yerine biliyoruz. Literal ön filtre (tek Aho-Corasick otomatı, satır
başına tek tarama) yalnızca envanteri eksik kaynaklar için güvenlik ağı — üretimde
`bound_ratio`'nun düşmesi bir arıza belirtisidir, normal çalışma değil. Hiçbir satır
reddedilmez: eşleşmeyen kaynak `_unassigned`'a, eşleşmeyen satır `failed`'a düşer ve
ikisi de ham arşivde durur.

**Parser kataloğu sıcak yeniden yükleniyor ve değişim atomik.** Anlık görüntü satırın
başında alınır, yani yeniden yükleme tam o sırada olsa bile satır tek bir tutarlı
katalogla işlenir. Tüm dosyalar bozuksa katalog **değiştirilmez** — hatalı bir
dağıtım çalışan boru hattını parser'sız bırakamaz.

**Ham arşiv RustFS'in veri kaybetmesini varsayarak kuruldu.** Sıra: yükle → geri
oku → sha256 karşılaştır → manifest'e `verified_at` yaz → segmenti 48 saat sonra
sil. **Doğrulanmamış segment asla silinmez.** Periyodik scrub örneklenmiş
nesneleri indirip manifest'e karşı doğrular; kayıp nesne replay gününde değil
olduğu gün görünür. Depoya yalnızca S3 API üzerinden konuşulur — RustFS'e özel
tek çağrı yok, kaçış planının bedeli bir config satırı.

**Grok kütüphanesi ve eşleme tabloları veridir, kod değil.**
`catalog/patterns/` Logstash setinin birebir kopyası; **elle düzenlenmez**,
upstream'den yeniden kopyalanır. Oniguruma/POSIX söz dizimi farkları derleyicide
çevrilir (`\h`, `[[:alnum:]]`, `X?*`). Aynı şekilde `catalog/mappings/` altındaki
eşleme tabloları YAML'dır; bilinmeyen tablo **derleme zamanında** hata verir.

**Grok için hazır kütüphane kullanılmıyor.** Parser YAML'ı kurum içinden geliyor ve
dikkatsiz tek pattern ingest'i durdurabilir. Derleme önce
`RegexOptions.NonBacktracking` ile denenir — girdi uzunluğunda doğrusal, felç
imkânsız. Lookaround/geri referans varsa geri izlemeli motora düşülür; orada 50 ms
`matchTimeout` ve karantina devreye girer. `parser lint` bu ayrımı bilir: doğrusal
motorda koşan bir "iç içe niceleyici" bulgusu **bilgi**, geri izlemeli motorda
**hata**dır.

**Testsiz parser yayınlanamaz.** `tests` bloğu şema düzeyinde zorunlu; CI
`catalog/parsers` altındaki her parser'ın gömülü testlerini koşturur.

## Proje düzeni

```
src/
  Bizigo.Contracts/            ortak tipler, plugin şemaları
  Bizigo.Ingest/               OTLP uç, WAL, kodlama, boru hattı
  Bizigo.Parsing/              YAML şema, grok derleyici, dispatcher
  Bizigo.Normalization/        core → OCSF/OTel eşleme
  Bizigo.Storage.ClickHouse/   şema, göç, bulk writer — ClickHouse.Driver'a erişen tek yer
  Bizigo.Storage.Raw/          object storage (S3 API), manifest, replay okuyucu
  Bizigo.ControlPlane/         EF Core, envanter, katalog, audit
  Bizigo.Query/                IScopedQuery — kapsam zorlamasının tek kapısı
  Bizigo.Api/                  ASP.NET Core, OIDC, uçlar
  Bizigo.Cli/
sidecar/                       Python: drain3 + pysigma
catalog/patterns/              Logstash grok setleri — VERİ, elle düzenlenmez
catalog/mappings/              eşleme tabloları (ocsf_network_activity vb.)
catalog/parsers/               YAML parser'lar + altın örnek dosyaları
db/clickhouse/                 sıralı SQL göçleri
deploy/                        docker-compose, otel, keycloak realm
```

## Sık kullanılan komutlar

```bash
dotnet build                                            # uyarılar hata
dotnet test tests/Bizigo.UnitTests                      # hızlı, container yok
dotnet ef migrations add <Ad> \
  --project src/Bizigo.ControlPlane \
  --startup-project src/Bizigo.Api --output-dir Migrations
dotnet run --project src/Bizigo.Cli -- schema migrate db/clickhouse
cd deploy && docker compose logs -f <servis>
```

### Parser CLI

```bash
BIN=src/Bizigo.Cli/bin/Debug/net10.0/bizigo        # ya da: dotnet run --project src/Bizigo.Cli --

$BIN parser lint catalog/parsers                    # şema + ReDoS taraması
$BIN parser test catalog/parsers                    # gömülü `tests` bloklarını koştur
$BIN parser try p.yaml --input '<189>devname=FG100 srcip=10.0.0.5 action=deny'
$BIN parser try p.yaml --input-file ornekler.log --json
cat ornekler.log | $BIN parser try p.yaml           # stdin de çalışır
```

`--patterns` / `--mappings` (ya da `BIZIGO_PATTERNS` / `BIZIGO_MAPPINGS`) katalog
dizinlerini değiştirir. Varsayılanlar çalışma dizininden yukarı doğru aranır, yani
CLI repo içindeki herhangi bir alt dizinden çağrılabilir.

⚠️ `bizigo` ikilisi doğrudan çalıştırılırken `DOTNET_ROOT` gerekir — apphost PATH'teki
`dotnet`'i (SDK 8/9) bulup net10.0 çalışma zamanını göremez:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
```

## Lisans

[MIT](LICENSE). Yeniden dağıtılan üçüncü taraf malzeme
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) içinde: `catalog/patterns/`
altındaki grok setleri
[logstash-patterns-core](https://github.com/logstash-plugins/logstash-patterns-core)
v4.3.4'ten olduğu gibi alınmıştır ve **Apache-2.0** lisanslıdır.
