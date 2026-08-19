# bizigo-loganalyzer

Plugin tabanlı, çok formatlı ve çok dilli log analiz platformu. Ağ/altyapı cihazı
logları birincil alan; agentic katmanla proaktif araştırma ve kök neden analizi.

**Durum:** F1 (boru hattı) kapandı — T01 iskelet, T02 depolama/kapsam, T03 ingest
boru hattı, T04 ham arşiv, T05 parser motoru, T06 dispatcher, T07 normalizasyon,
T08 vendor kataloğu, T09 kimlik, T10 API uçları, T11 replay ve T12 sidecar.

**F2 (görünürlük) sürüyor:** T13 Next.js iskeleti ve BFF, T14 OpenAPI tip
üretimi, T18 parser yayın akışı.

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

# 4. Arayüzü çalıştır (ayrı terminal)
cd ui && cp .env.example .env.local && npm install && npm run dev
```

`Bizigo.Api` gibi arayüz de **compose'un dışında**, doğrudan makinede koşuyor:
sıcak yeniden yükleme geliştirme döngüsünü hızlandırıyor ve API zaten aynı
şekilde çalışıyor.

| Servis | Adres | Not |
| --- | --- | --- |
| ClickHouse | http://localhost:8123 | `bizigo` / `bizigo` |
| PostgreSQL | localhost:5432 | `bizigo` / `bizigo` — Keycloak da bu örneği kullanır |
| RustFS (S3) | http://localhost:9000 | konsol :9001, `bizigoadmin` / `bizigoadmin` |
| Keycloak | http://localhost:8180 | `admin` / `admin`, realm `bizigo` |
| OTel Collector | syslog TCP :5140, UDP :5141, OTLP :4318 | |
| Sidecar | http://localhost:8099 | Drain3 + pySigma (T12) |
| Arayüz (Next.js) | http://localhost:3000 | BFF burada; tarayıcı API'ye doğrudan konuşmuyor |

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

**Yazılan tek gerçek `core`; OCSF ve OTel türetiliyor.** İki şemayı da materyalize
etmek depolamayı ~2 katına, mapping bakımını iki katına çıkarırdı. Türetme
ClickHouse görünümlerinde (`events_ocsf`, `events_otel`) — API katmanında değil,
çünkü F3'te Sigma kuralları ClickHouse SQL'ine derleniyor ve OCSF alan adlarına
vuruyor; SQL konuşan her araç aynı şekli görmeli. Kolona yazılan tek OCSF alanı
`ocsf_class_uid` ve `ocsf_activity_id`; gerisi `attrs` içinde `ocsf.`/`otel.`
önekiyle durur, yani yeni bir alan eklemek şema göçü değil YAML değişikliğidir.

**`raw_ref` bayt konumu değil arşiv ön eki taşır.** Ingest boru hattı ile arşiv
yükleyici bilinçli olarak bağımsız çalıştığı için olay satırı yazılırken nesne
henüz yoktur ve offset bilinemez. Ön ek (`raw/{owner_group}/{yyyy}/{MM}/{dd}/{HH}/{source_class}/`)
yazma anında hesaplanabilir ve manifest sorgusunun anahtarıyla örtüşür; tek gerçek
kaynak arşivin kendisi kalır. Bedeli, tek kaydı okumak için nesnenin açılması.

**Kimlik Keycloak'ta, kapsam bizde.** Ürünün IdP'den beklediği tek şey dört claim
(`sub`, `preferred_username`, `roles`, `groups`) — dar tutulması bilinçli: Entra
ID'ye geçiş IdP tarafında mapper ayarı demek, kodda değişiklik değil. Grup claim'i
doğrudan `owner_group` sayılmıyor; `idp_group_mapping` üzerinden çevriliyor, yoksa
bir ekibin kapsamını değiştirmek için IdP'ye dokunmak gerekirdi. Keycloak grup
adlarını tam yol ve baştan eğik çizgiyle basıyor (`/network/core`) — `full.path`
açık bırakıldı çünkü kapatmak iç içe gruplarda ad çakışması üretirdi.

**Collector `/v1/logs`'a anonim vurmuyor.** Keycloak servis hesabı,
`client_credentials`, rolü **yalnızca `ingest`** — kimlik sızarsa veri yazılabilir,
okunamaz. Rol ayrımının tek sebebi bu.

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

**`bizigo-v1` kaplaması varsayılan olarak devrede.** Logstash'in `IPV4` ve `TIME`
tanımları lookaround taşıyor ve ağ logunda IP geçmeyen pattern neredeyse yok —
sonuç olarak kataloğun 21 ifadesi doğrusal motorda derlenemeyip geri izlemeye
düşüyordu, yani "önce `NonBacktracking` dene" kademesi pratikte hiç çalışmıyordu.
`catalog/patterns/bizigo-v1/` bir **kaplama**, tam set değil: `legacy` üstüne
biniyor ve yalnızca adlandırılmış pattern'leri değiştiriyor, böylece `legacy` ile
`ecs-v1` upstream'in birebir kopyası kalıp `cp -R` ile yükseltilebiliyor.
Sınırlar `\b` ile kuruldu — yerine geçtiği lookaround'dan **daha katı**, yani
kaçırabilir ama uyduramaz. GROK003 uyarısı 21'den 2'ye indi.

**Grok için hazır kütüphane kullanılmıyor.** Parser YAML'ı kurum içinden geliyor ve
dikkatsiz tek pattern ingest'i durdurabilir. Derleme önce
`RegexOptions.NonBacktracking` ile denenir — girdi uzunluğunda doğrusal, felç
imkânsız. Lookaround/geri referans varsa geri izlemeli motora düşülür; orada 50 ms
`matchTimeout` ve karantina devreye girer. `parser lint` bu ayrımı bilir: doğrusal
motorda koşan bir "iç içe niceleyici" bulgusu **bilgi**, geri izlemeli motorda
**hata**dır.

**Testsiz parser yayınlanamaz.** `tests` bloğu şema düzeyinde zorunlu; CI
`catalog/parsers` altındaki her parser'ın gömülü testlerini koşturur.

**Değişiklik webhook'unun kimliği bir imza, token değil** (T24). CI sistemleri
OIDC akışı yürütmüyor; `POST /v1/changes/webhooks/{id}` bu yüzden anonim ama
imza doğrulanmadan hiçbir kayıt oluşmuyor. Kapsam token'dan değil **ucun
yapılandırmasından** geliyor: her uç tek bir `owner_group`'a bağlı ve
`IScopedQuery.WriteChangeAsync` yalnızca ona yazmasına izin veriyor. Sistem
kapsamı (sınırsız) bilerek kullanılmıyor — sızan tek bir anahtar her ekibin
zaman çizelgesine olay düşürebilirdi.

```jsonc
"Changes": {
  "Webhooks": {
    "MaxBodyBytes": 1048576,
    "Endpoints": [
      {
        "Id": "gh-network",              // POST /v1/changes/webhooks/gh-network
        "Provider": "github",            // github | jenkins | gitlab | generic
        "OwnerGroup": "network/core",    // bu ucun yazabildiği TEK grup
        "Secret": "${WEBHOOK_SECRET}",   // zorunlu; anahtarsız uç açılışta patlar
        "TargetKind": "Config",
        "DefaultChangeKind": "deploy",
        // Yalnızca "generic" için — bilinmeyen sağlayıcı JSON yollarıyla eşlenir.
        "Mapping": {
          "TargetId": "$.data.name",
          "ChangeKind": "$.event",
          "Actor": "$.username",
          "Timestamp": "$.timestamp",
          "DeliveryId": "$.request_id",
          "Details": { "site": "$.data.site.name" }
        }
      }
    ]
  }
}
```

| Sağlayıcı | İmza başlığı | Kaydedilen bildirim |
| --- | --- | --- |
| GitHub | `X-Hub-Signature-256` (HMAC-SHA256) | `workflow_run` (yalnızca `completed`), `deployment_status` (nihai durum), `push` |
| GitLab | `X-Gitlab-Token` (düz jeton — sağlayıcı HMAC vermiyor) | `pipeline` ve `deployment` (yalnızca `success`/`failed`) |
| Jenkins | `X-Bizigo-Signature` (HMAC-SHA256) | Notification Plugin, yalnızca `COMPLETED` fazı |
| generic | `X-Bizigo-Signature` (HMAC-SHA256) | Yapılandırılan yollar |

**Filtreler kasıtlı:** her sağlayıcı aynı işi birden çok kez bildiriyor
(`in_progress`, `STARTED`, `running`). Hepsini kaydetmek `change_events`'i RCA
kanıtı olmaktan çıkarıp CI gürültüsüne çevirirdi. Eşlenmeyen bildirim **202**
alıyor, 4xx değil — GitHub 4xx gören webhook'u kırmızı işaretliyor ve kimse
gerçek olayların gelmediğini fark etmiyor.

**İdempotans Postgres'te**, ClickHouse'ta değil: `change_events` düz bir
`MergeTree` ve tekillik garantisi vermiyor. `change_webhook_deliveries` tablosu
`{uç}:{teslimat kimliği}` anahtarını benzersizlik kısıtıyla tutuyor; kimlik yoksa
gövdenin sha256'sı kullanılıyor. Talep **önce** yazılıyor, değişiklik olayı
sonra — ters sırada iki eşzamanlı teslimatın ikisi de satır düşürürdü.

**Webhook uçları artık ekrandan tanımlanıyor** (T25, K34). `change_connectors`
tablosu kaynağın tipini, hedefini, zamanlamasını, sahip grubunu ve **şifreli**
kimlik bilgisini tutuyor; alıcı bir isteği karşılarken önce bu tabloya, sonra
`appsettings.json`'daki uçlara bakıyor. **Veritabanı kazanıyor** — tersi olsaydı
ekrandan yapılan bir değişiklik unutulmuş bir yapılandırma satırı yüzünden
sessizce etkisiz kalırdı.

**Gizli bilgi anahtarı ürün geneli tek:** `Security__SecretKey` (base64, 32 bayt,
AES-256-GCM). T22 bunu `Alerting:SecretKey` olarak kurmuştu; T25 connector
kimlik bilgileri için aynı şeye ihtiyaç duyunca ortaklaştırıldı
(`Bizigo.Contracts.Security`). İki anahtar, iki rotasyon hikâyesi ve altı ay
sonra birinin döndürülüp diğerinin unutulması demekti. **Anahtar yoksa gizli
bilgi kaydedilmiyor** — düz metne düşmek, "şifreli saklanıyor" iddiasını
sessizce yalanlardı.

**Hata mesajının temizliği runner'ın değil servisin işi.** Bir toplayıcıdan ya
da bir istisnadan gelen her metin, veritabanına yazılmadan ve kullanıcıya
gösterilmeden önce `SecretRedactor`'dan geçiyor. Gerekçe ölçüldü: sızıntının en
sık gerçekleştiği yer bağlantı hatasının mesajı ve orada gizli bilgi çoğu zaman
kimsenin yazmadığı bir yerden — kütüphanenin istisna metninden — geliyor.

**Toplayıcısı olmayan connector tipi etkinleştirilemiyor.** Cihaz config
toplayıcısı T26'da; o gelene kadar `DeviceConfig` connector'ı kaydedilebiliyor
ama açılamıyor. Alternatif — zamanlayıcının her turda "bu tip için toplayıcı
yok" diye hata yazması — çalışma geçmişini gerçek arızalarla sahtelerinin
karıştığı bir yığına çevirirdi.

**Saklama tek politika, iki tablo:** `change_webhook_deliveries` ve
`change_connector_runs` 90 gün sonra siliniyor (`Changes:Connectors:Retention`).
`change_events` bunun **dışında** — RCA'nın F3'te arayacağı geçmiş hiç
silinmiyor.

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
  Bizigo.Alerting/             eşik · oran · sessizlik + Slack/Teams/e-posta/webhook
  Bizigo.Api/                  ASP.NET Core, JWT bearer, uçlar — cookie/OIDC YOK (K31)
  Bizigo.Cli/
ui/                            Next.js: arayüz + BFF (OIDC, oturum, API vekili)
  src/app/api/auth/            giriş ve çıkış
  src/app/signin-oidc/         OIDC dönüş ucu — yol realm dosyasında sabit
  src/app/api/bff/[...path]/   Bizigo.Api'ye açılan tek kapı
  src/lib/auth/                keşif, PKCE, oturum deposu, yenileme
  src/lib/api/                 üretilen tipler + tiplenmiş istemci
  src/app/tokens.css           tasarım jetonları — ekranlar ham değer yazmıyor
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

### Arayüz

```bash
cd ui
npm run dev            # http://localhost:3000
npm test               # BFF testleri — token tarayıcıya ulaşmıyor kanıtı
npm run typecheck
npm run api:generate   # OpenAPI belgesi + TypeScript tipleri (T14)
npm run api:check      # CI kapısı: üretilenler depodakiyle aynı mı
```

**Kimlik akışı Next'te (K31).** Tarayıcıya yalnızca oturum çerezi gidiyor; erişim
ve yenileme token'ları Next sunucusunun belleğindeki oturum deposunda duruyor ve
API'ye sunucudan sunucuya `Authorization: Bearer` ile taşınıyor. `Bizigo.Api`
saf kaynak sunucusu: cookie ve OIDC işleyicisi taşımıyor.

⚠️ Oturum deposu **bellek içi**: Next sunucusu yeniden başlarsa herkes yeniden
giriş yapıyor ve birden çok kopya çalıştırılırsa oturumlar kopyalar arasında
paylaşılmıyor. `SessionStore` arayüzü paylaşılan bir depo (Redis) eklenebilsin
diye ayrıldı; dağıtım çok kopyaya çıkmadan önce doldurulmalı.

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
