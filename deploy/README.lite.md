# LogAnalyzer Lite — yapılandırma ve Coolify geçiş kitabı

Lite, `src/LogAnalyzer.Lite` altındaki bağımsız olay kaydedicisi (net10, tek
konteyner). Üretimde `loglar.<alan>` üzerinden Traefik BasicAuth
(`removeHeader: true`) arkasında yayında; IntegrationGateway sink'i olayları iç
Docker ağından `http://loganalyzer-lite:5060/api/v1/ingest`'e yazıyor ve
Gateway `/loganalyzer/{**rest}` yolunu Lite'a vekilliyor.

| Dosya | Ne için |
| --- | --- |
| `deploy/Dockerfile.lite` | İmaj. `sdk/aspnet:10.0-alpine`, root olmayan `app` kullanıcısı (UID 1654), `/app/data` 0700, `HEALTHCHECK`, `org.opencontainers.image.revision` |
| `deploy/docker-compose.coolify.lite.yml` | **Üretim.** Coolify "Docker Compose" kaynağı |
| `deploy/docker-compose.lite.yml` | **Yalnızca yerel.** `127.0.0.1:5060`'a bağlı; sunucuda çalıştırılmaz |

Yerelde konteynersiz çalıştırma — varsayılan veri dizini `/app/data` olduğu
için yerel bir dizin verin (`.gitignore`'da):

```bash
LITE_DATA_DIR=./.lite-data dotnet run --project src/LogAnalyzer.Lite
```

## Ortam değişkenleri

| Değişken | Varsayılan | Anlamı |
| --- | --- | --- |
| `LITE_INGEST_TOKEN` | — | Tanımlıysa `/api/v1/ingest` `X-LogAnalyzer-Ingest-Token` başlığını ister (sabit zamanlı karşılaştırma, aksi hâlde 401). Tanımsızsa ingest kabul edilir ve açılışta **uyarı** loglanır — yalnızca geçiş dönemi için. |
| `LITE_ALLOWED_ORIGINS` | — | Virgülle ayrılmış kökenler (`https://alan[:port]`). CORS yalnızca bunlara açık; yönetim eylemlerinde de aynı köken sayılır — ama tarayıcı isteği `Sec-Fetch-Site: cross-site` diye işaretlerse (farklı kayıtlı alan adı) yine 403. `*` ve yollu girdiler uyarıyla yok sayılır. |
| `LITE_FRAME_ANCESTORS` | — | Boşlukla ayrılmış kaynaklar; CSP `frame-ancestors 'self' …`. Boşsa yalnızca aynı köken çerçeveleyebilir (+ `X-Frame-Options: SAMEORIGIN`). |
| `LITE_ENABLE_DEMO` | `false` | Yalnızca `true` iken `/api/v1/demo/simulate-incident` var; aksi hâlde 404. Üretimde **açılmaz** (depoya sahte olay yazar). |
| `LITE_DATA_DIR` | `/app/data` | Olay spool'u ve alarm ayarı. Dizin yazılamıyorsa uygulama **açılmaz**. |
| `GIT_SHA` | imajdan | `/health` → `commit`. Coolify'da derleme argümanı `SOURCE_COMMIT`'ten gelir. |

**Yönetim eylemleri** (`/api/v1/logs/purge`, `POST /api/v1/alerts/config`,
`/api/v1/alerts/test`, açıksa demo) `X-Requested-With: loganalyzer-lite`
başlığı ister ve çapraz siteden gelen (`Sec-Fetch-Site: cross-site` ya da
yabancı `Origin`) istekleri 403 ile reddeder. Bu bir CSRF kapısı; kimlik
doğrulama hâlâ Traefik BasicAuth'ta.

**Webhook'lar** yalnızca `https://hooks.slack.com/services/…` ve
`https://discord.com|discordapp.com/api/webhooks/{id}/{token}` olabilir.
`GET /api/v1/alerts/config` adresleri maskeli döndürür
(`slackConfigured`/`discordConfigured` bayraklarıyla); boş ya da maskeli değer
kayıtlı sırrı korur, `clearSlackWebhook`/`clearDiscordWebhook` siler. Test ucu
yalnızca `not_configured | timeout | unreachable | rejected` döndürür.

## IntegrationGateway tarafında gereken değişiklikler

Bu depo Gateway'i değiştirmiyor; aşağıdakiler Gateway'de yapılmalı:

1. **Sink belirteci.** `/api/v1/ingest` çağrısı `X-LogAnalyzer-Ingest-Token`
   başlığını, Lite'taki `LITE_INGEST_TOKEN` ile aynı değerle göndermeli.
2. **`/loganalyzer` vekili.** Bugün yalnızca `Content-Type`'ı iletiyor. Yönetim
   eylemlerinin vekil üzerinden çalışması için en az `X-Requested-With`
   iletilmeli. `Origin`/`Sec-Fetch-Site` de iletilirse (önerilen) Lite'ın
   isteği aynı köken sayabilmesi için ya `X-Forwarded-Host` (tarayıcının
   gördüğü ana makine) iletilmeli ya da Gateway'in genel kökeni
   `LITE_ALLOWED_ORIGINS`'e eklenmeli.
3. **Çerçeveleme.** Gateway `loglar.<alan>`'ı doğrudan iframe'e alıyorsa
   Gateway kökeni `LITE_FRAME_ANCESTORS`'e eklenmeli. `/loganalyzer` vekili
   üzerinden çerçeveleme aynı köken olduğu için ek ayar istemez.

**Belirteç sırası:** önce Gateway başlığı göndermeye başlar (belirteç
tanımsızken Lite başlığı yok sayar), sonra Lite'ta `LITE_INGEST_TOKEN`
tanımlanır. Ters sıra olayları 401 ile düşürür. Doğrulama: Lite loglarında
`Ingest request rejected` uyarısı olmamalı ve açılıştaki
`LITE_INGEST_TOKEN is not set` uyarısı kaybolmalı.

## Geçiş kitabı: elle kurulmuş konteynerden Coolify'a

Amaç: mevcut birimdeki olayları ve alarm ayarını **kaybetmeden** Coolify
kaynağına geçmek. Hiçbir adım orijinal birimi silmez ya da taşımaz.
`tests/LogAnalyzer.Lite.Tests/RunbookContractTests` aşağıdaki iki kuralın
komut sırasını sınıyor.

**Veri yalnızca DURMUŞ birimden okunur.** Eski sürüm spool dosyasını her
olayda yerinde yeniden yazıyor; dosyayı önce kesip sonra yazıyor, yani yazım
atomik değil. Çalışan birimden alınan kopya iki şeyi bozar. Kopya ile
durdurma arasında gelen olaylar ve ayar değişiklikleri yeni birime geçmez.
Kopya yarım yazılmış bir dosyaya denk gelirse yeni sürüm spool'u kenara alıp
**boş** başlar. Bu yüzden eski konteyner önce durdurulur. Taban çizgisi, yedek
ve kopya ondan sonra alınır.

**Coolify'da domain ATANMAZ.** `loglar.<alan>` route'u ve BasicAuth
(`removeHeader: true`) bu depodaki compose dosyalarında tanımlı değil; eski
compose'da da hiç etiket yoktu. Route Traefik'in dosya sağlayıcısında duruyor
ve `loganalyzer-lite` adına gidiyor (1. adım bunu doğrular). Yeni compose aynı
takma adı taşıdığı için bu route ve BasicAuth yeni konteynere olduğu gibi
hizmet eder. Coolify'da servise domain atamak, aynı `Host` kuralıyla
BasicAuth'suz **ikinci** bir router üretir. O zaman hangi router'ın seçileceği
Traefik'in öncelik sırasına kalır ve arayüz ile yönetim eylemleri (purge,
webhook ayarı) kimliksiz açılabilir. Geçiş sırasında `LITE_INGEST_TOKEN` de
henüz tanımsız olabilir. BasicAuth'u compose etiketlerine taşımak ayrı bir
karardır ve bu kitabın parçası değildir. O karar verilirse etiket eklemek ve
dosya route'unu kaldırmak birlikte yapılır, kimliksiz 401 denetimi de domain
dışarı açılmadan önce yapılır.

**Kesinti penceresi.** Pencere 3. adımdaki durdurmayla başlar ve yeni
konteyner sağlıklı olana kadar sürer; Coolify'ın derleme süresi de buna
dahildir (birkaç dakika). Bu pencerede Gateway sink'inin gönderdiği olaylar
Lite'a **ulaşmaz** ve Lite onları sonradan geri alamaz. Gateway yeniden
denemiyorsa bu olaylar kaybolur. Düşük trafikli bir saat seçin. 3. ve 9.
adımdaki saatleri not edin ve pencereyi Gateway loglarındaki sink
hatalarıyla karşılaştırın.

> Eski sürümün `GET /api/v1/alerts/config` ucu ve `alerts-config.json`
> dosyası webhook adreslerini düz metin tutuyor. Aşağıdaki komutlar yalnızca
> **var/yok** bilgisini yazdırıyor. Adresleri terminale, loga ya da sohbete
> dökmeyin.

**0. Gerçek birimi bul.** Konteyner elle `docker compose -f deploy/docker-compose.lite.yml`
ile kurulduysa birimin adı büyük ihtimalle `deploy_loganalyzer-data`'dır,
`loganalyzer-data` değil.

```bash
docker inspect loganalyzer-lite \
  --format '{{range .Mounts}}{{.Type}} {{.Name}} -> {{.Destination}}{{"\n"}}{{end}}'
VOL_SRC=<yukarıdaki /app/data biriminin adı>
```

**1. Bugünkü route'u doğrula.** Eski konteyner çalışmaya devam eder ve hiçbir
veri okunmaz. Coolify'da dosya route'ları Servers → Proxy → Dynamic
Configurations altında, sunucuda `/data/coolify/proxy/dynamic/` dizininde durur.
Komutlar yalnızca anahtarları, kuralları ve hedefi yazdırır; kullanıcı ve
parola satırları yazdırılmaz.

```bash
docker inspect loganalyzer-lite \
  --format '{{range $k, $v := .Config.Labels}}{{$k}}{{"\n"}}{{end}}' | grep '^traefik\.' || echo 'traefik etiketi yok'
grep -rl 'loglar\.' /data/coolify/proxy/dynamic/
grep -nE 'rule:|middlewares:|basicAuth:|removeHeader:|url:' /data/coolify/proxy/dynamic/<yukarıdaki dosya>
```

Devam etmek için üç koşul var: kural ``Host(`loglar.<alan>`)`` olmalı, router'a
`removeHeader: true` ile bir BasicAuth ara katmanı bağlı olmalı ve hedef
`http://loganalyzer-lite:5060` olmalı. Hedef başka bir şeyse (IP adresi,
`container_name`'e bağlı başka bir ad ya da başka bir port) ya da route bu
dizinde değilse **durun**. Yeni konteynere o route'tan erişilemez ya da
BasicAuth başka bir yerde tanımlıdır; ikisi de bir karar ister.

Dışarıdan ve kimlik bilgisi OLMADAN deneyin:

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://loglar.<alan>/               # 401
curl -s -o /dev/null -w '%{http_code}\n' https://loglar.<alan>/api/v1/stats   # 401
```

**2. Coolify kaynağını hazırla ama Deploy ETME.** Kaynak türü Docker Compose,
dosya `deploy/docker-compose.coolify.lite.yml`. Kaynakta şunlar ayarlanır:

- "Include Source Commit in Build" açık.
- Ortam değişkenleri Coolify'da tanımlı.
- **Domains alanı boş.**
- Auto Deploy, geçiş bitene kadar kapalı. Eski konteyner çalışırken başlayan
  bir kopya aynı takma adı paylaşır. `VOL_SRC` zaten `loganalyzer-data` ise
  aynı birimi de paylaşır.

**3. Eski konteyneri durdur — silme.** Eski konteyner yeniden adlandırılır;
böylece adı ve `loganalyzer-lite` takma adı yeni konteynerle çakışmaz.
Yeniden başlatma ilkesi kapatılır; böylece sunucu yeniden başladığında eski
konteyner geri gelmez:

```bash
date -u +%FT%TZ   # kesinti başlangıcı
docker stop loganalyzer-lite
docker rename loganalyzer-lite loganalyzer-lite-pre-rollout
docker update --restart=no loganalyzer-lite-pre-rollout
```

**4. Taban çizgisi — durmuş birimden, salt okunur:**

```bash
docker run --rm -v "$VOL_SRC":/from:ro alpine cat /from/incidents-flight-recorder.json \
  | python3 -c 'import json,sys; print({"incidents": len(json.load(sys.stdin))})'
docker run --rm -v "$VOL_SRC":/from:ro alpine cat /from/alerts-config.json \
  | python3 -c 'import json,sys; c=json.load(sys.stdin); print({"enabled": c.get("enabled"), "slack": bool(c.get("slackWebhookUrl")), "discord": bool(c.get("discordWebhookUrl")), "minLevel": c.get("minLevel")})'
```

`incidents` sayısını (aşağıda `N`) ve bayrakları not edin. `N`, yeni sürümün
açılışta yükleyeceği sayının kendisidir. Durdurmadan önce `/api/v1/stats`'tan
alınan bir sayı kaybı göstermez: ingest sürerken o sayı artar ve 1000'lik
halka tamponu dolunca sabit kalır.

- **`json.load` hata verirse dosya yarım yazılmıştır: durun.** Yeni sürüm bu
  dosyayı kenara alıp boş başlar. Eski konteyneri yeniden başlatmak da çözüm
  değildir: eski sürüm okuyamadığı spool'un üstüne yeni bir dosya yazar.
- `alerts-config.json` için `cat` "No such file" derse alarm hiç
  yapılandırılmamıştır ve bu normaldir. Aynı hata
  `incidents-flight-recorder.json` için gelirse birim yanlıştır: durun.

**5. Yedek al.** Bu yedek yalnızca kopyalar ve kaynağı durmuş birimden alır:

```bash
STAMP=$(date +%Y%m%d)
docker volume create "loganalyzer-data-pre-rollout-$STAMP"
docker run --rm -v "$VOL_SRC":/from:ro -v "loganalyzer-data-pre-rollout-$STAMP":/to alpine \
  sh -c 'cp -a /from/. /to/ && ls -la /to'
```

**6. `loganalyzer-data` adlı external birimi hazırla.**

- `VOL_SRC` zaten `loganalyzer-data` ise olduğu gibi kullanılır.
- Değilse yeni bir birim yaratılır, **boş olduğu doğrulanır** ve kopya bu
  birime alınır. Birim zaten varsa ve boş değilse **durun**: hangi verinin
  doğru olduğuna karar vermek gerekir.

```bash
docker volume create loganalyzer-data
docker run --rm -v loganalyzer-data:/to alpine sh -c 'test -z "$(ls -A /to)"' \
  && docker run --rm -v "$VOL_SRC":/from:ro -v loganalyzer-data:/to alpine cp -a /from/. /to/
```

**7. Sahiplik.** Yeni imaj root olarak çalışmıyor (UID 1654). Eski imaj
dosyaları root sahipliğinde ve 0644 kipinde, dizini de 0777 kipinde
bırakıyordu:

```bash
docker run --rm -v loganalyzer-data:/data alpine \
  sh -c 'chown -R 1654:1654 /data && chmod 700 /data && find /data -type f -exec chmod 600 {} +'
```

**8. Deploy.** 2. adımda hazırlanan kaynağı Coolify'da Deploy edin. Domains
alanı hâlâ boş kalır.

**9. Doğrulama.**

```bash
NEW=$(docker ps --filter "label=com.docker.compose.service=loganalyzer-lite" --format '{{.Names}}' | head -n1)
date -u +%FT%TZ                                              # kesinti sonu
docker exec "$NEW" wget -qO- http://127.0.0.1:5060/health     # commit = dağıtılan SHA
docker exec "$NEW" id -u                                      # 1654
docker logs "$NEW" 2>&1 | grep -E 'incidents from the incident spool|No incident spool|could not be parsed|not an allowed'
docker exec "$NEW" wget -qO- http://127.0.0.1:5060/api/v1/alerts/config \
  | python3 -c 'import json,sys; c=json.load(sys.stdin); print({"enabled": c.get("enabled"), "slack": c.get("slackConfigured"), "discord": c.get("discordConfigured"), "minLevel": c.get("minLevel")})'
```

- Log satırı `Loaded N incidents from the incident spool.` olmalı; `N`, 4.
  adımdaki sayıyla **aynı** olmalı. Yeni sürüm boş depoya örnek olay eklemez
  ve spool'u ingest başlamadan önce yükler.
- `No incident spool found` ya da `could not be parsed; moved aside` satırı
  görünüyorsa veri gelmemiştir. Coolify kaynağını durdurun ve 10. adıma
  geçin.
- Bayraklar 4. adımdakilerle aynı olmalı. `not an allowed … webhook` satırı,
  kayıtlı adresin izin listesine uymadığını söyler. O kanalın bayrağı `false`
  döner ve adres arayüzden yeniden girilmelidir.

Route'u denetleyin. Önce yeni konteyneri, sonra dışarıdan ve kimlik bilgisi
olmadan route'u deneyin:

```bash
docker inspect "$NEW" \
  --format '{{range $k, $v := .Config.Labels}}{{$k}}={{$v}}{{"\n"}}{{end}}' | grep -E '^traefik\.http\.routers\.[^=]+\.rule=' || echo 'router kuralı yok'
curl -s -o /dev/null -w '%{http_code}\n' https://loglar.<alan>/               # 401
curl -s -o /dev/null -w '%{http_code}\n' https://loglar.<alan>/api/v1/stats   # 401
```

İlk komut `router kuralı yok` yazmalıdır. Bir ``Host(`loglar.…`)`` kuralı
görünüyorsa Coolify bir domain atamış demektir. Domain'i kaldırıp yeniden
dağıtın. Sonra tarayıcıda kimlik bilgisiyle `https://loglar.<alan>/` açılmalı.

Konteyneri yeniden başlatın. Spool yeniden başlatmadan sağ çıkmalı:

```bash
docker restart "$NEW"
docker logs "$NEW" 2>&1 | grep 'incidents from the incident spool' | tail -n1   # en az N
```

Son olarak Gateway konteynerinden `http://loganalyzer-lite:5060/health`
çağrılır; yanıt yeni konteynerin commit'ini döndürmelidir.

**10. Geri dönüş.** Coolify kaynağını durdurun, sonra:

```bash
docker rename loganalyzer-lite-pre-rollout loganalyzer-lite
docker update --restart=unless-stopped loganalyzer-lite
docker start loganalyzer-lite
```

`VOL_SRC` `loganalyzer-data`'dan farklıysa orijinal birime hiç dokunulmadı.
Aynıysa 7. adımdaki sahiplik değişikliği eski konteyneri etkilemez, çünkü
eski konteyner root olarak çalışır. Veri gerekirse
`loganalyzer-data-pre-rollout-$STAMP` biriminden geri kopyalanır.

`loganalyzer-lite-pre-rollout` konteyneri ve yedek birim, yeni kaynak bir süre
kararlı çalıştıktan sonra **ayrı bir kararla** kaldırılır. Bu, kitabın
parçası değildir.
