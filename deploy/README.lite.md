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

> Eski sürümün `GET /api/v1/alerts/config` ucu webhook adreslerini düz metin
> döndürüyor. Aşağıdaki komutlar yalnızca **var/yok** bilgisini yazdırıyor;
> adresleri terminale, loga ya da sohbete dökmeyin.

**0. Gerçek birimi bul.** Elle `docker compose -f deploy/docker-compose.lite.yml`
ile kurulduysa birim adı büyük ihtimalle `deploy_loganalyzer-data`'dır,
`loganalyzer-data` değil.

```bash
docker inspect loganalyzer-lite \
  --format '{{range .Mounts}}{{.Type}} {{.Name}} -> {{.Destination}}{{"\n"}}{{end}}'
VOL_SRC=<yukarıdaki /app/data biriminin adı>
```

**1. Taban çizgisi** (sırsız):

```bash
docker exec loganalyzer-lite wget -qO- http://127.0.0.1:5060/api/v1/stats
docker exec loganalyzer-lite wget -qO- http://127.0.0.1:5060/api/v1/alerts/config \
  | python3 -c 'import json,sys; c=json.load(sys.stdin); print({"enabled": c.get("enabled"), "slack": bool(c.get("slackWebhookUrl")), "discord": bool(c.get("discordWebhookUrl")), "minLevel": c.get("minLevel")})'
```

`activeIncidents` ve bayrakları not edin.

**2. Yalnızca kopyalayan yedek** (orijinal salt okunur bağlanıyor):

```bash
STAMP=$(date +%Y%m%d)
docker volume create "loganalyzer-data-pre-rollout-$STAMP"
docker run --rm -v "$VOL_SRC":/from:ro -v "loganalyzer-data-pre-rollout-$STAMP":/to alpine \
  sh -c 'cp -a /from/. /to/ && ls -la /to'
```

**3. `loganalyzer-data` adlı external birimi hazırla.**

- `VOL_SRC` zaten `loganalyzer-data` ise olduğu gibi kullanılır.
- Değilse yeni birim yaratılır ve **boş olduğu doğrulanarak** içine kopyalanır.
  Birim zaten varsa ve boş değilse **durun** — hangi verinin doğru olduğu bir
  karar.

```bash
docker volume create loganalyzer-data
docker run --rm -v loganalyzer-data:/to alpine sh -c 'test -z "$(ls -A /to)"' \
  && docker run --rm -v "$VOL_SRC":/from:ro -v loganalyzer-data:/to alpine cp -a /from/. /to/
```

**4. Sahiplik.** Yeni imaj root olarak koşmuyor (UID 1654). Eski imaj dosyaları
root/0644 ve dizini 0777 bırakıyordu:

```bash
docker run --rm -v loganalyzer-data:/data alpine \
  sh -c 'chown -R 1654:1654 /data && chmod 700 /data && find /data -type f -exec chmod 600 {} +'
```

**5. Eski konteyneri durdur — silme.** Ad ve `loganalyzer-lite` takma adı
yenisiyle çakışmasın diye yeniden adlandırılıyor; yeniden başlatmada geri
gelmesin diye yeniden başlatma ilkesi kapatılıyor:

```bash
docker stop loganalyzer-lite
docker rename loganalyzer-lite loganalyzer-lite-pre-rollout
docker update --restart=no loganalyzer-lite-pre-rollout
```

**6. Coolify kaynağı.** Docker Compose kaynağı, dosya
`deploy/docker-compose.coolify.lite.yml`; "Include Source Commit in Build"
açık; ortam değişkenleri Coolify'da tanımlanır; `loglar.<alan>` alanı
`loganalyzer-lite` servisinin 5060 portuna bağlanır ve **BasicAuth ara
katmanı (`removeHeader: true`) yeniden bağlanır**. Kimlik bilgisi olmadan
`https://loglar.<alan>/` 401 vermiyorsa geçiş bitmemiştir.

**7. Doğrulama.**

```bash
NEW=$(docker ps --filter "label=com.docker.compose.service=loganalyzer-lite" --format '{{.Names}}' | head -n1)
docker exec "$NEW" wget -qO- http://127.0.0.1:5060/health   # commit = dağıtılan SHA
docker exec "$NEW" id -u                                     # 1654
docker exec "$NEW" wget -qO- http://127.0.0.1:5060/api/v1/stats         # activeIncidents = taban
docker exec "$NEW" wget -qO- http://127.0.0.1:5060/api/v1/alerts/config # *Configured = taban
docker restart "$NEW"   # sonra üç satırı tekrarla: sayı ve bayraklar yeniden başlatmadan sağ çıkmalı
```

Gateway konteynerinden `http://loganalyzer-lite:5060/health` yeni konteynerin
commit'ini döndürmeli.

**8. Geri dönüş.** Coolify kaynağını durdurun, sonra:

```bash
docker rename loganalyzer-lite-pre-rollout loganalyzer-lite
docker update --restart=unless-stopped loganalyzer-lite
docker start loganalyzer-lite
```

`VOL_SRC` `loganalyzer-data`'dan farklıysa orijinal birime hiç dokunulmadı.
Aynıysa 4. adımın sahiplik değişikliği eski (root) konteyneri etkilemez; veri
gerekirse `loganalyzer-data-pre-rollout-$STAMP`'tan geri kopyalanır.

`loganalyzer-lite-pre-rollout` konteyneri ve yedek birim, yeni kaynak bir süre
kararlı çalıştıktan sonra **ayrı bir kararla** kaldırılır; bu kitabın parçası
değil.
