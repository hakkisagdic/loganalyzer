# bizigo-loganalyzer

Plugin tabanlı, çok formatlı ve çok dilli log analiz platformu. Ağ/altyapı cihazı
logları birincil alan; agentic katmanla proaktif araştırma ve kök neden analizi.

**Durum:** F1 (boru hattı) kapandı — T01 iskelet, T02 depolama/kapsam, T03 ingest
boru hattı, T04 ham arşiv, T05 parser motoru, T06 dispatcher, T07 normalizasyon,
T08 vendor kataloğu, T09 kimlik, T10 API uçları, T11 replay ve T12 sidecar.

**F2 (görünürlük) kapandı** — T13 Next.js iskeleti ve BFF, T14 OpenAPI tip
üretimi, T15 log arama, T16 olay detayı ve ham görünüm, T17 kaynak envanteri,
T18–T19 parser yayın akışı ve editör, T20 katalog, T21–T23 alarm motoru,
bildirim kanalları ve yönetim ekranı, T24–T26 değişiklik beslemesinin üç
kaynağı, T27 doğrulama, T28 UI/UX denetimi.

Kapanış belgesi: **`docs/epic/f2-kapanis/`** — ölçülen kısıtlar, yanlış çıkan
altı iddia, kapanmayan kalemler gerekçeleriyle, bekçilerin durumu ve F3'e
devredilen sorular. F3'e başlarken okunacak belge odur.

Planlama belgeleri Traycer epic'inde:
`mimari-kararlar` · `f1-teknik-plan` · `rca-raporu-ozelligi` · `tickets/`

> ⚠️ **Geliştirme aşamasında.** `deploy/.env.example` ve `appsettings.json` içindeki
> kimlik bilgileri (`bizigo/bizigo`, `admin/admin`) yalnızca yerel compose içindir.
> Üretimde kullanılmaz.

## Hızlı başlangıç

```bash
# 0. Git kancaları (klon başına BİR KEZ)
git config core.hooksPath .githooks

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

**0. adım ne yapıyor.** `.githooks/pre-push`, `main`'e push etmeden önce bir
önceki CI koşumuna bakıyor ve kırmızıysa push'u durduruyor. Sebebi ölçülmüş bir
olay: compose dosyası dört merge boyunca ayrıştırılamıyordu, kapı her koşumda
kırmızı yanıyordu ve kimse bakmadığı için üstüne üç merge daha kondu (B18/B19).
Bilerek geçmek için `git push --no-verify`.

`gh` kurulu değilse kanca **gürültülü biçimde açık kalıyor** — kapatsaydı
`--no-verify` alışkanlığa döner ve kanca tamamen ölürdü. O boşluğu GitHub
tarafındaki `.github/workflows/ci-red.yml` kapatıyor: main kırmızı kaldığında
açık bir konu (issue) yazıyor ve hiçbir yerel kuruluma bağlı değil.

`Bizigo.Api` gibi arayüz de **compose'un dışında**, doğrudan makinede koşuyor:
sıcak yeniden yükleme geliştirme döngüsünü hızlandırıyor ve API zaten aynı
şekilde çalışıyor.

**API'yi container'da koşturmak** ayrı bir soruya cevap veriyor — *yığının
tamamı tek komutla kalkıyor mu* — ve varsayılanı değiştirmiyor:

```bash
cd deploy && docker compose --profile api up -d --wait
```

Profil arkasında olması bilinçli: yukarıdaki sıcak yeniden yükleme döngüsü
geliştirmenin varsayılanı ve onu yavaşlatmanın bedeli herkese çıkardı.

İki karar container'lı kurulumun **zorunlu kıldığı** şeyler ve ikisi de
ölçülerek bulundu:

- **`Auth:MetadataAddress`** — anahtarın *nereden indirileceği* ile *kime
güvenildiği* ayrıldı. Issuer hâlâ `http://localhost:8180/realms/bizigo` (token'da
yazan o ve tarayıcı oraya gidiyor), ama container içinde `localhost:8180`
Keycloak değil container'ın kendisi.
- **`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`** — metadata adresini ayırmak tek başına
yetmedi. Belge iniyordu ama **içindeki `jwks_uri` yine `localhost:8180`**
diyordu, yani imzalama anahtarları indirilemiyor ve API her token'ı reddediyordu.
Ekranda görünen hâli *"API kimliği tanımıyor"* — bir kimlik hatası gibi duran,
aslında ağ topolojisi olan bir arıza. Ölçülen hâli:

  | Nereden | `issuer` | `jwks_uri` |
  | --- | --- | --- |
  | Ağ içinden | `localhost:8180` | `keycloak:8080` |
  | Makineden | `localhost:8180` | `localhost:8180` |

  Yani makinedeki akış değişmiyor; değişen yalnızca ağ içinden sorulduğunda
  verilen backchannel adresi.

**`.dockerignore` bir hız ayarı değil doğruluk koşulu.** Makinedeki `obj/`
derleme bağlamına girdiğinde container'ın kendi `restore` çıktısının üstüne
yazıyor ve `publish` *"Package AWSSDK.S3 … was not found"* diyerek düşüyor —
paketten söz eden ama sebebi paket olmayan bir hata.

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

**Saklama tek politika, üç tablo:** `change_webhook_deliveries`,
`change_connector_runs` ve `change_config_snapshots` 90 gün sonra siliniyor
(`Changes:Connectors:Retention`). İki istisna var ve ikisi de bilinçli:
`change_events` politikanın **dışında** — RCA'nın F3'te arayacağı geçmiş hiç
silinmiyor; ve her connector'ın **en yeni** anlık görüntüsü kesimin gerisinde
kalsa bile korunuyor, yoksa hiç değişmeyen bir cihazın taban çizgisi silinir ve
bir sonraki çekim config'in tamamını sahte bir değişiklik olarak raporlar.

**Cihaz config farkı (T26).** `Bizigo.Devices` ürünün cihazlara **bağlanan**
tek derlemesi; SSH bağımlılığı yalnızca orada ve arayüzün yüzeyinde yazma diye
bir şey yok — bu ürün config okuyor, değiştirmiyor. FortiGate, Cisco ASA ve
MikroTik: üçü de SSH konuşuyor, komutları vendor başına yazıldı (`show`,
`more system:running-config`, `/export terse` — hepsi sayfalama kapalı ve
satır kaydırması olmayan biçimler, çünkü yarım ya da farklı bölünmüş bir çıktı
silinmiş yüzlerce satır gibi görünür).

**Gürültü elenmezse tablo işe yaramaz.** Cihazlar her çekimde değişen satırlar
basıyor: FortiGate config dosyası sürümünü, ASA `Cryptochecksum`'ı, MikroTik
export başlığına o anın tarihini. `ConfigNormalizer` bunları eliyor ve
**gizli değerleri silmiyor, maskeliyor**: `set psksecret ENC …` →
`set psksecret ENC <gizli:a3f21c08>`. Silmek dönen bir anahtarı görünmez
yapardı; oysa rotasyon gerçek bir değişiklik. Özet değişince fark yakalanıyor,
değer hiçbir yere yazılmıyor.

**Fark bölüm başına çoklu-küme farkı, LCS değil.** Ağ config'i bildirimsel:
aynı bölümdeki iki ayarın sırası anlam taşımıyor ve cihazlar yeniden yazımda
sırayı değiştirebiliyor. LCS her yeniden yazımda yüzlerce sahte değişiklik
üretirdi, üstelik maliyeti iki tarafın çarpımı kadar. Bu yöntem girdi
uzunluğunda doğrusal. Bedeli açık: bölüm **içinde** yer değiştiren satır fark
üretmiyor — bildirimsel bir config'te zaten bir değişiklik değil.

**Saklanan anlık görüntü ham config değil.** Normalize + maskelenmiş metin,
üstüne şifreli. Bu ürün config yedeklemiyor: RCA'nın ihtiyacı "ne değişti",
config'in kopyası değil — ve kopya tutulduğu an saklama, erişim ve sızıntı
sorumluluğu da doğar. `change_events`'e yazılan kayıt da bölüm **adlarını**
taşıyor, satır içeriklerini değil.

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
  src/app/olaylar/             log arama (T15) ve olay detayı + ham baytlar (T16)
  src/app/kaynaklar/           kaynak envanteri, son görülme, CSV yükleme (T17)
  src/lib/auth/                keşif, PKCE, oturum deposu, yenileme
  src/lib/api/                 üretilen tipler + tarayıcı/sunucu istemcileri
  src/lib/events/              arama ölçütleri, kısa sorgu kuralı, hex/kodlama
  src/lib/sources/             envanter + etkinlik birleştirmesi
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

### Uçtan uca — çalışan ürünün ekran görüntüleri

Arayüzün iki ayrı görüntü paketi var ve **aynı şeyi kanıtlamıyorlar**:

| Paket | Ne koşturuyor | Ne kanıtlıyor |
| --- | --- | --- |
| `npm test` → `tests/screenshots/` | Bileşenleri doğrudan çiziyor, sunucu yok | Görünüm: çok dilli gövde, rozet kontrastı, 500 satırlık tablonun yerleşimi |
| `npm run e2e` → `tests/e2e/` | Gerçek Keycloak + gerçek API + gerçek veri | Zincir: yönlendirme, OIDC akışı, kapsam filtresi, ClickHouse sorgusu |

```bash
# Ön koşul: yığın ayakta. Yığın yoksa koşum KIRMIZI yanıyor, atlamıyor.
cd deploy && cp .env.example .env
docker compose up -d --wait clickhouse postgres rustfs keycloak sidecar

cd ../ui
npm run e2e             # başsız — hazırlık + koşum
npm run e2e:headed      # tarayıcı önde, adımlar izlenebilir hızda
npx playwright test     # yalnız koşum (hazırlık zaten yapılmışsa)
```

`npm run e2e:prepare` dört iş yapıyor: yığın sağlıklı mı, API/CLI derlemesi,
`bizigo seed golden`, `next build`. **Playwright'ın `globalSetup`'ına
konulamıyor** — Playwright `webServer`'ı ondan önce başlatıyor ve sunucu henüz
var olmayan bir ikiliyi çalıştırmaya çalışıyor.

**Giriş `analyst.core` ile, `admin` ile değil.** `admin` kapsam filtresinden
muaf (`AccessScope.System`); o yolla çekilen bir görüntü kapsamın uygulandığını
göstermezdi. Analistin `/network/core` claim'i `idp_group_mapping` üzerinden
`golden`'a çevriliyor, yani görüntüdeki her satır aynı zamanda K17'nin kanıtı.

Çıktı `docs/ekran-goruntuleri/uctan-uca/`. Üç port sabit ve üçü de zorunlu:
**3000** (realm'in `redirectUris`'i), **5080** (`BIZIGO_API_URL`), **8180**
(Keycloak'ın `KC_HOSTNAME`'i issuer'ı oraya sabitliyor).


**Kimlik akışı Next'te (K31).** Tarayıcıya yalnızca oturum çerezi gidiyor; erişim
ve yenileme token'ları sunucudaki oturum deposunda duruyor ve API'ye sunucudan
sunucuya `Authorization: Bearer` ile taşınıyor. `Bizigo.Api` saf kaynak
sunucusu: cookie ve OIDC işleyicisi taşımıyor.

**Oturum deposu seçilebilir** (B7). Varsayılan `memory`, çok kopyalı kurulum
için `redis`:

```bash
BFF_SESSION_STORE=memory                 # tek süreç — geliştirmenin varsayılanı
BFF_SESSION_STORE=redis                  # çok kopya
BFF_REDIS_URL=redis://localhost:6379     # `redis` seçildiyse ZORUNLU
```

Bellek içi hâl bilerek duruyor: Redis zorunlu olsaydı yerel ortam tek komutla
ayağa kalkmazdı. Ama sınırı da yerinde — Next sunucusu yeniden başlarsa herkes
yeniden giriş yapıyor ve **ikinci bir kopya açıldığında oturumlar paylaşılmıyor**.

Üç şey depo değişse de değişmiyor:

- **Çerez hâlâ opak bir anahtar.** İçinde token yok, çözülebilecek bir şey yok.
`ui/tests/redis-session.test.ts` aynı sızıntı iddialarını Redis deposuyla da
koşturuyor.
- **TTL tek kaynaktan.** Redis `EXPIRE`'ı kaydın kendi ömründen türüyor, o da
`BFF_SESSION_TTL_SECONDS`'ten. İkinci bir ömür değeri, oturumun ya erken
ölmesi ya Redis'te sızıntı olarak kalması demekti.
- **Ulaşılamayan depo, oturumsuz kullanıcı DEĞİL.** Redis düşerse vekil `503`
dönüyor, `401` değil: `401` istemciye "yeniden giriş yap" dedirtir, giriş de
aynı depoya yazmayı dener ve düşer — kullanıcı hiçbir hata görmeden sonsuz
döngüye girer. `currentUser()`'ın üç durumlu olmasının sebebi de buydu.

Servis adı **`redis-session`** (`deploy/docker-compose.yml`); kalıcılığı
**kapalı**, çünkü oturum verisi geçici ve diske yazmak token'ları diske yazmak
olurdu.

Compose'da **iki Redis örneği** var ve bu bilinçli: `redis` sidecar'ın Drain3
ağacını tutuyor ve kalıcılığı **açık** olmak zorunda — durum kaybolursa
`template_id`'ler sessizce kayar. RDB anlık görüntüsü örneğin tamamını yazdığı
için (veritabanı seçmiyor) tek örnekte iki ihtiyaç sağlanamıyor.

### Ölçüm verisi — `bizigo seed golden` (T39)

F3'ün iki ölçümü de **gerçek** veri istiyor: Sigma kapsam ölçümü
(`prototypes/t30-sigma/measure.py`) vendor başına sayıyor, baseline penceresi
ölçümü (`BaselineWindowMeasurement`) tabanı 1 saatten 30 güne süpürüyor. İkisinin
de ön kontrolü sentetik kıyaslama verisini **reddediyor** ve doğru yapıyor.

```bash
export DOTNET_ROOT="$HOME/.dotnet"
BIN=src/Bizigo.Cli/bin/Debug/net10.0/bizigo

# Docker'sız: üret, zaman damgası bekçisini koştur, raporla.
$BIN seed golden --dry-run

# Yükle. Bağlantı --clickhouse ya da BIZIGO_CLICKHOUSE'tan geliyor.
$BIN seed golden --clickhouse 'Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo'

# Yeniden yükleme: yalnızca kendi kapsam grubunu siler, tablodaki diğer veriye
# DOKUNMAZ. Bayraksız koşum, grup doluysa hata verip durur.
$BIN seed golden --replace
```

**Doğrudan `INSERT` yok.** Satırlar `EncodingDetector` → `EventComposer`
(dispatch + imza + şablon) → `EventNormalizer` → `EventWriter` yolundan geçiyor,
yani `signature_hash`, `template_id`, `attrs` (OCSF/OTel görünümlerinin girdisi),
`time_source` ve `parse_status` üretimdekiyle **aynı** üretiliyor. Elle yazılan
bir satır bunların hepsinde ayrışabilir ve ayrıştığı hiçbir yerde görünmez.

**Zaman damgaları yeniden yazılıyor, gövde değil.** Örnek dosyalar 2015–2024
tarihleri taşıyor; olduğu gibi yüklenirse baseline ölçümü hiçbir şey göremez.
`SampleTimeRewriter` yalnızca damga tokenını hedef ana taşıyor — aynı olayı
yarın basan cihaz da aynı şeyi yapar. Damga biçimi bilgisi böylece **ikinci kez**
yazılmış oluyor (birincisi parser YAML'ının `date` adımı); ayrışma sessiz kalmasın
diye yükleyici her olayda normalize edilmiş `ts`'nin ektiği ana eşit olduğunu
**doğruluyor** ve tutmazsa durup satırı basıyor.

**Yayılım kararı ölçülen sayıyı belirliyor**, o yüzden `GoldenSamplePlan` içinde
yazılı: sıklık yasası Zipf (`--zipf`, varsayılan 2.0), sıra **imza** üzerinden ve
vendor'lar arasında sırayla dağıtılmış, varış zamanları düzgün (uniform) — günlük
ritim bilerek modellenmedi, çünkü ritim 45 dakikalık olay penceresinin
yoğunluğunu yükleyicinin koşturulduğu saate bağlardı.

**Baseline ölçümü artık kendi verisini tohumluyor.** `BaselineWindowMeasurement`
compose yığınına bakmıyor: her eğri için izole bir ClickHouse veritabanı kurup
yukarıdaki tiplerin **aynısıyla** tohumluyor. Dışarıdan bağlanmak ölçümü birinin
elle kurduğu bir duruma bağlardı; **tekrarlanabilirlik, sayının bağlayıcı
olmasının şartı.**

> ⚠️ Baseline eğrisinin **dirseği bu fixture'ın özelliğidir**, üretimin değil:
> yaklaşık `1/λ_min` civarında oluşuyor, yani seçilen `--zipf` ve
> `--events`/`--span-days` oranının sonucu. Bu yüzden ölçüm **iki farklı
> `--zipf` ile birden** koşuyor ve `BaselineFixtureVerdict.Compare`'in **imzası**
> iki eğri istiyor — tavsiye olarak yazılsaydı bir kez koşturulur ve unutulurdu.
> Dirsekler ayrışırsa "seçilebilir taban" **doğmuyor** (`Baseline` alanı `null`),
> yani rapor basacak bir sayı bulamıyor.
>
> **Ölçüldü (ClickHouse'suz ön görü, `BaselineFixturePreviewTests`):** `zipf=2.0`
> dirseği **7 gün**, `zipf=1.4` dirseği **1 gün**. Yani bugünkü fixture'dan
> üretim için bağlayıcı bir taban uzunluğu **çıkmıyor**; ölçümün kanıtladığı şey
> mekanizmanın çalıştığı. Bağlayıcı sayı gerçek müşteri verisiyle tekrarlanmalı.
>
> İkinci sınır: 87 örnek satır ~81 imza taşıyor, yani taban yeterince uzadığında
> oran **sıfıra** iniyor — üretimde olmayacak bir hâl. Yenilik üretmeye devam
> eden bir örneklem gerekiyor.

> ⚠️ **Maskeleme sözlüğünde ay *adı* için maske yok.** `NUMBER` günü ve saati
> yutuyor ama `May`/`Oct` imzada kalıyor, yani syslog biçimli vendor'larda
> (Cisco ASA, MikroTik, nginx) aynı şablon **her ay yeni bir `signature_hash`**
> alıyor. Ölçüldü: 87 örnek satırın 38'i bu davranışı gösteriyor; 5 günlük
> yayılımda 81 ayrı imza, 30 günlükte 92, 90 günlükte 102. Yükleyici bunu her
> koşumda basıyor. F3'ün "ilk-görülen imza" sinyali için gerçek bir kalem.

### Alan kapsamı — `bizigo fields coverage` (T39)

Bir Sigma kuralı hiçbir satır bulmuyorsa sebebi üç bambaşka şey olabilir ve
tabloda üçü de aynı görünür: **boş kolon**. Bu araç üçünü ayırıyor.

```bash
# Katalog yarısı — ClickHouse gerekmiyor.
$BIN fields coverage

# Her iki yarı: katalog ne üretebiliyor ↔ events_ocsf'te ne yazılı.
$BIN fields coverage --clickhouse 'Host=localhost;...' --owner-group golden
```

| Kutu | Soru | Örnek |
| --- | --- | --- |
| **1** | Dosyada var, hiçbir alana inmemiş | ASA'nın `Reset-I`'si — parser hiç görmemiş |
| **2** | İnmiş ama OCSF adına değil `unmapped`'e | RouterOS zincir adı → `fw_chain` |
| **3a** | Kolon hiçbir vendor'da dolmuyor | eşleme hiç yazılmamış olabilir |
| **3b** | Kolon **bu** vendor'da boş, başkasında dolu | `activity_name`: FortiGate dolu, RouterOS boş |

3b ayrımı olmadan küresel bir "eksik alan" listesi `activity_name`'i ifade
edemiyor ve iki farklı durum için de yanlış iş yaptırıyor.

**Araç eşanlamlı tablosu taşımıyor:** hangi `unmapped` anahtarının hangi OCSF
kolonuna karşılık geldiğini iddia etmiyor — o tabloyu yazmak, ölçümün cevabını
ölçümün girdisine taşımak olurdu. Kutular yan yana basılıyor, eşleştirmeyi
okuyan yapıyor. Tek istisna **biçim** farkı: `proto_token=UDP` ile
`connection_info_protocol_name=udp` birebir tespit edilebiliyor ve
`[biçim: …]` diye işaretleniyor — o durumda cevap "kayıp" değil "dönüştürülmüş".

> ⚠️ **Kutu 1'de ayraç ve söz dizimi de var.** "Yakalanmamış" bilgi demek değil;
> liste taranarak *veriye benzeyen* parçalar aranmalı. Araç bu ayrımı yapmıyor,
> çünkü yapabilmesi için neyin veri olduğunu bilmesi gerekirdi — sorunun kendisi bu.
>
> Kutu 1 bir kez **sessizce boş çıktı** ve düzeltildi: `attrs['message']` satırın
> tamamı ve kapsama sayılınca hiçbir aralık boşta kalmıyordu, yani "parser her
> şeyi yakalamış" görünüyordu. Artık **içinde başka bir yakalanmış değer geçen**
> alanlar üst hâl sayılıyor ve kapsama girmiyor.

### Değer uzayı — `bizigo fields values` (T39)

Üç kutu "bilgi alan oldu mu" diye soruyor; bu ölçüm **alan hangi değerleri
taşıyabiliyor** diye. Bir eşleme tablosu, beslediği kolonun değer uzayını
daraltıyor: `status` kolonu `http_status_outcome.yaml`'dan besleniyor ve orada
hiçbir zaman bir HTTP kodu durmuyor, yalnızca `success`/`failure` duruyor.

```bash
$BIN fields values                                  # değer uzayları
python3 prototypes/t30-sigma/explain_misses.py --json /tmp/misses.json
$BIN fields values --rules /tmp/misses.json         # kurallarla birleştir
```

**Veriye hiç bakmıyor** ve bakmaması asıl özelliği: örneklemde bir değerin
bulunmaması *"bugün yok"*, şemanın onu üretememesi *"hiçbir zaman olmayacak"*.

Kuralları da **okumuyor** — `explain_misses.py --json` zaten
`alan|operatör = değer` üçlülerini çıkarıyor, alan adı çevirisi de
`bizigo_pipeline.py`'nin `FIELD_MAP`'inden okunuyor. İki ayrıştırıcı, iki aracın
aynı kuralı farklı kolona bağladığı gün demekti.

| Sınıf | Anlamı |
| --- | --- |
| **ERİŞİLEMEZ** | Kapalı uzay o değeri üretemiyor — örneklem düzelse de eşleşmez |
| **PARSER BOŞLUĞU** | Vendor'da açık ama bazı parser'lar kolonu hiç doldurmuyor |
| **METİN EKSENİ YANILIYOR** | Ham satırda yok ama kolonda **var** (eşleme tablosu çeviriyor) |
| ham metin | `raw_data ILIKE …` — tasarım tercihi, ama indeks kullanılmıyor |
| `unmapped` erişimi | `unmapped['…']` — alan olarak adreslenmiş, yine indekssiz |
| uzay açık | Şema bir şey demiyor |

Ölçülen (24 kural, 33 dizge): ERİŞİLEMEZ **0** · parser boşluğu **4** ·
metin ekseni yanılıyor **1** · ham metne vuran kural **4/24**.

> ⚠️ **`absent` kutusu bir üst sınırdır.** Bir eşleme tablosu cihazın sözcüğünü
> normalleştiriyorsa (`failed → failure`), kuralın aradığı değer ham satırda hiç
> geçmez ve metin ekseninde "desen yok" görünür — oysa kolonda gerçekten vardır.
> Ölçüldü: 10 `absent` kuralın **1'i** bu yüzden orada. Kapsam oranının paydası
> `absent` düşülerek kurulduğu için bu doğrudan paydayı oynatıyor.

Ayrıntı: `docs/epic/t39-alan-kapsami/`.

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
