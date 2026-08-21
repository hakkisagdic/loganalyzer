# PostHog — kendi altyapımızda

Ürün analitiği. Bizigo tarafındaki entegrasyon `ui/src/lib/telemetry/` altında;
bu dosya **sunucu tarafını** anlatıyor.

## Neden self-host

Bizigo müşterinin log'unu okuyor. Ürünü değerlendiren güvenlik ekibinin ilk
sorduğu şey "bu ekran dışarıya ne gönderiyor" oluyor ve "tarayıcı
posthog.com'a gidiyor" cevabı satın almayı durduran cevap. İki kademeli
savunmamız var:

1. **Tarayıcı PostHog'a hiç konuşmuyor.** Bütün trafik Next sunucusundaki
   `/api/telemetry` vekilinden geçiyor (`ui/src/app/api/telemetry/[...path]/route.ts`).
   Müşteri ağı yalnızca ürünün kendi adresini görüyor. Bu, PostHog Cloud
   kullanırken bile geçerli.
2. **Self-host** — olayların hiç dışarı çıkmaması. Bu dosya bunu anlatıyor.

`TELEMETRY_HOST` dışında hiçbir şey değişmiyor: aynı kod ikisiyle de çalışıyor.

## Donanım — buraya dikkat

PostHog'un kendi dokümanı **4 vCPU / 16 GB RAM / 30+ GB disk** istiyor, ve bu
PostHog'un *tek başına* ihtiyacı. Yığın kendi ClickHouse, Kafka, Postgres,
Redis ve MinIO'sunu getiriyor.

> **Geliştirme makinesinde çalıştırmayın.** Bu depo 16 GB'lık bir makinede
> geliştiriliyor ve üstünde zaten sekiz konteyner var (`deploy/docker-compose.yml`).
> PostHog'u oraya eklemek makineyi swap'e sürükler — `CLAUDE.md` §3'ün
> yasakladığı şey tam olarak bu. PostHog **ayrı bir host** ister.
>
> Ağır işten önce: `~/.claude/scripts/machine-resources.sh check`

## Kurulum

PostHog **sürümlenmiş imaj yayımlamıyor**; self-host HEAD'den akıyor. Bu yüzden
compose dosyasını depoya kopyalamıyoruz — kopya ilk gün bayatlar ve kimse
bayatladığını fark etmez. Bunun yerine **sabitlenmiş bir commit'ten** çekiyoruz:

```bash
./deploy/posthog/fetch-compose.sh
```

Betik `POSTHOG_REF` içindeki commit'i indiriyor ve `deploy/posthog/.gitignored/`
altına yazıyor. Sürümü yükseltmek = `POSTHOG_REF`'i değiştirmek, yani **bir
commit'te görünen** bir hareket.

Sonra:

```bash
cp deploy/posthog/.env.example deploy/posthog/.env   # düzenleyin
docker compose --env-file deploy/posthog/.env -f deploy/posthog/.gitignored/docker-compose.hobby.yml up -d
```

Resmî hızlı yol (sabitleme yok, HEAD'i alır — üretim için önerilmez):

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/posthog/posthog/HEAD/bin/deploy-hobby)"
```

## Bizigo'yu buna bağlamak

`ui/.env.local`:

```bash
TELEMETRY_ENABLED=true
TELEMETRY_PROJECT_KEY=phc_...          # PostHog → Project settings
TELEMETRY_HOST=http://posthog.ic.local:8000
TELEMETRY_ASSET_HOST=http://posthog.ic.local:8000   # self-host'ta host ile aynı
TELEMETRY_UI_HOST=http://posthog.ic.local:8000
TELEMETRY_ENVIRONMENT=prod
```

`TELEMETRY_HOST` **sunucudan sunucuya** adres — tarayıcının çözebilmesi
gerekmiyor, Next sunucusunun çözebilmesi yeterli. Konteyner ağındaysa
`http://posthog:8000` de olur.

## Açmayın: oturum kaydı (session replay)

PostHog'un en gösterişli özelliği ve bu üründe **kullanılamaz**. Ekranda duran
şey müşterinin log satırları; oturum kaydı onları videoya alıp PostHog'a
kopyalar. Ürün iki yerden kapatıyor:

| Kapı | Yer | Ne yapıyor |
| --- | --- | --- |
| İstemci | `TelemetryProvider.tsx` → `disable_session_recording: true` | Kaydediciyi hiç başlatmıyor |
| Vekil | `route.ts` → `ALLOWED_UPSTREAM_PATHS` | `/s` ucunu 403'le reddediyor |

İki kapı bilinçli: birincisi bir sürüm yükseltmesinde varsayılanını
değiştirebilecek bir *seçenek*, ikincisi değiştiremeyecek bir *liste*.
İkincisinin kırmızı yanabildiği ölçüldü (`ui/tests/telemetry-proxy.test.ts`).

Aynı gerekçeyle kapalı: otomatik yakalama, ısı haritası, ölü/öfke tıkı,
istisna yakalama, anketler.

## Ne gidiyor, ne gitmiyor

**Gidiyor:** `ui/src/lib/telemetry/events.ts` kataloğundaki olaylar ve
**yalnızca** her olayın yanında yazılı alanlar — sayaçlar, süreler,
sınıflandırılmış hata tipleri, kalıba indirilmiş yollar (`/kaynaklar/:id`).

**Gitmiyor:** arama ölçütünün metni, sorgu dizeleri, log satırları, hata
mesajlarının kendisi, ham URL'ler, istemci IP'si, e-posta, ham Keycloak `sub`.

Süzgeç beyaz liste; katalogda olmayan alan sessizce düşüyor ve düştüğünün
testi var (`ui/tests/telemetry-scrub.test.ts`).

**IP'nin bedeli:** vekil `X-Forwarded-For` taşımıyor, dolayısıyla PostHog
bütün olayları uygulama sunucusunun adresinden görüyor. Coğrafi kırılım
kayboluyor. Bilinçli takas — müşteri ağının adresi bizim analitiğimizde işi yok.

## Kimlik

Varsayılan **anonim**: posthog-js'in rastgele tarayıcı kimliği kullanılıyor,
yani "kaç tarayıcı" cevaplanıyor, "hangi kullanıcı" cevaplanmıyor.

`TELEMETRY_IDENTIFY_USERS=true` açarsanız `TELEMETRY_IDENTITY_SALT` **zorunlu**
olur ve giden şey ham `sub` değil `HMAC-SHA256(sub, tuz)` (`identity.ts`).
Tuzsuz açmak sessizce anonime düşmüyor — vekil 503 ve eksik değişkenin adını
dönüyor.

Tuz üretimi:

```bash
openssl rand -hex 32
```

## Yedekleme

PostHog'un durumu Postgres (yapılandırma, pano, kullanıcılar) ve ClickHouse
(olaylar) içinde. Hobby yığınında ikisi de konteyner hacminde — yani
`docker compose down -v` **her şeyi siliyor**. Üretimde ikisi de dışarı
alınmalı.

## Lisans

Self-host bileşenleri MIT. Ücretli plan özellikleri yalnızca Cloud'da; PostHog
self-host için destek vermiyor ve CVE yayımlamıyor.
