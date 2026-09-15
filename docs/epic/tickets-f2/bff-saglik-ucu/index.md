---
title: "T62 — BFF hazırlık ucu: `--wait`'in yeşili neyi vaat ediyor?"
kind: ticket
status: 1
---

# T62 — BFF hazırlık ucu

**Bulgu T49'un içinden çıktı ve ticket'ın iddia etmediği bir şeydi:** container
sağlık kontrolü `ui` servisini **healthy** ilan ediyor, `docker compose up -d
--wait` **yeşil** dönüyor — yani yığın *"kalktı"* diyor — ve oturum deposu
tamamen kırık olabiliyor. Arıza **ilk giriş denemesinde** çıkıyor.

Bu, deponun beş kez ölçtüğü sınıfın yeni bir örneği: *bir kapının varlığı, neye
baktığını söylemiyor.*

## 1 · Sondanın görüş alanı — ölçüldü

T49'un sağlık kontrolü kök sayfayı yokluyordu, ölçütü `< 500`. Kod yolu:

| Adım | Yer | Ne oluyor |
| --- | --- | --- |
| Sonda çerez taşımıyor | `deploy/docker-compose.yml` (`ui` healthcheck) | `fetch('http://127.0.0.1:3000/')` |
| Oturum çözümü | `ui/src/lib/auth/session.ts:80` | `sessionId` yoksa **depoya hiç gitmeden** `undefined` |
| Kimlik | `ui/src/lib/auth/currentUser.ts` | `status: "anonymous"` |
| Kök sayfa | `ui/src/app/page.tsx:31` | `redirect("/api/auth/login?returnTo=%2F")` → **307** |

307 < 500 → **healthy**.

**Gördüğü:** Next süreci ayakta, HTTP dinliyor, kök sayfa render ediliyor,
`KEYCLOAK_CLIENT_SECRET` gibi zorunlu ortam değişkenleri okunabiliyor (yoksa
`readBffConfig` fırlatır ve sayfa 500 döner).

**Görmediği üç bağımlılık** — üçü de kırıkken sonda **yeşil** kalıyor:

| Bağımlılık | Kırık olduğunda kullanıcı ne görüyor | Sonda ne diyor |
| --- | --- | --- |
| **Oturum deposu** (`redis-session`) | Giriş akışı oturumu yazamıyor; vekil `503` dönüyor | healthy |
| **`Bizigo.Api`** | Ekran açılıyor, her veri isteği düşüyor | healthy |
| **Keycloak keşif belgesi** | Giriş düğmesi çalışmıyor; `discover()` fırlatıyor | healthy |

Üçüncüsü container'da ayrıca **T49'un kendi kararına** bağlı: keşif belgesi
`KEYCLOAK_METADATA_URL` üzerinden **arka kanaldan** iniyor. O adres yanlışsa
ekran kalkıyor ve giriş çalışmıyor — sondanın göremediği hâllerin en sinsisi,
çünkü yapılandırma hatası gibi değil "Keycloak bozuk" gibi görünüyor.

## 2 · Hazırlık mı canlılık mı — **hazırlık**, ve ikisi ayrı sorular

| Soru | Kim sorar | Cevabı ne yapar |
| --- | --- | --- |
| **Canlılık** — süreç ayakta mı | yeniden başlatıcı | süreci öldür ve yeniden başlat |
| **Hazırlık** — bağımlılıkları hazır mı | `--wait`, yük dengeleyici | trafiği bekletme / gönderme |

**Seçilen: hazırlık.** Tüketici `compose` sağlık kontrolü ve onun beslediği
`--wait`; o komutun verdiği söz *"yığın kullanılabilir"* ve bugün tutmayan söz o.

**Canlılık ucu yazılmadı ve gerekçesi tüketicisizlik** (§8: *tüketicisi olmayan
bir tip tahmindir*). Ölçüldü: `deploy/docker-compose.yml` içinde `ui` servisinin
**hiçbir `restart` politikası yok** — dosyadaki iki `restart` satırı başka
servislere ait — ve ortada orkestratör de yok. Yani *"beni yeniden başlat"*
cevabını okuyacak kimse yok. Bir gün Kubernetes'e girilirse `live` ucu o gün
yazılır ve o gün bir tüketicisi olur.

**Kiletlenme riski ölçüldü ve yok.** `ui` servisi `api`, `keycloak` ve
`redis-session` üçünü de `service_healthy` ile bekliyor, yani bu uç ilk kez
koştuğunda üçü **zaten hazır**; ters yönde `ui`'yi bekleyen bir servis de yok.
Dolayısıyla bağımlılıkları yoklamak kalkış sırasını kilitleyemiyor.

**Kalan bedel yazılı:** bağımlılıklardan biri çalışırken düşerse `ui`
`unhealthy` oluyor. Bugün bunu okuyan bir yeniden başlatma politikası olmadığı
için sonucu, **gerçeği yansıtan kırmızı bir sağlık kontrolü** — istenen şey.
Ama bir gün `restart: unless-stopped` eklenirse, Keycloak'ın bir dakikalık
kesintisi `ui` sürecini gereksizce yeniden başlatır; o gün `live`/`ready`
ayrımı gerçek olur.

## 3 · Sınır: `memory` varsayılan ve orada Redis YOK

Uç *"Redis kırık"* **diyemez**. `BFF_SESSION_STORE` varsayılanı `memory` ve
orada Redis diye bir şey yok; yerel geliştirmenin tamamı o kipte koşuyor.

Söylenebilecek tek doğru cümle: **yapılandırılmış** depoya erişiliyor mu. Rapor
bu yüzden deponun **adını** taşıyor:

```json
{ "ready": false,
  "checks": [ { "name": "session_store", "state": "unreachable", "kind": "redis" },
              { "name": "api", "state": "ready" },
              { "name": "identity_provider", "state": "ready" } ] }
```

Ayrım kaçırılsaydı uç yerel geliştirmede **sürekli kırmızı** yanar ve ilk gün
kapatılırdı — bu depoda yanlış pozitifin bilinen bedeli.

Mekanizma `SessionStore.probe()`: arayüzde **zorunlu**, isteğe bağlı değil.
Kalıp `RedisClient.close`'un aynısı — isteğe bağlı olsaydı yeni bir depo
uygulaması onu yazmayı unutabilir ve uç o depo için **sessizce yeşil** kalırdı,
yani bu ucun kapattığı hatanın birebir aynısı doğardı.

`get()` neden yetmiyor: sağlık sondasının elinde geçerli bir oturum kimliği yok
ve `get(rastgele)` **ulaşılabilir** bir depoda da `undefined` dönüyor. *"Bulunamadı"*
ile *"ulaşılamadı"* ayrımı (`redis-store.ts`'in taşıyıcı ayrımı) sonda tarafında
kaybolurdu.

## 4 · Yük topoloji taşımıyor — güvenlik kararı

Uç **kimlik doğrulaması istemiyor** ve isteyemez: onu çağıran şey container'ın
sağlık kontrolü. Ama `ui` servisi 3000'i **dışarıya** veriyor, yani bu yük
kuruma dışarıdan da görünebilir.

O yüzden yükte **adres yok, ana makine adı yok, hata metni yok, sürüm yok** —
yalnızca sayılabilir durumlar. Kalıp telemetri süzgecinin aynısı
(`ui/src/lib/telemetry`: sayılabilir durumlar gidiyor, serbest metin gitmiyor).
Yapılandırma okunamazsa **hangi değişkenin** eksik olduğu da yazılmıyor; gerçek
hata sunucu günlüğüne düşüyor.

Bekçisi var ve kırmızı yanabildiği ölçüldü: `ui/tests/health-ready.test.ts` →
*"yükte adres, ana makine adı ya da hata metni geçmiyor"*, bağımlılıkların
istisna metinlerine kasten adres/port/hata kodu koyarak.

## 5 · Kabul kriterleri

1. Üç bağımlılığın **her biri** ayrı ayrı kırıkken uç `503` dönüyor ve
**yalnızca** kırık olanı işaretliyor — ✅ ölçüldü (10 test, konteyner yok).
2. Yapılandırılmış deponun **adı** raporda — ✅ ölçüldü.
3. Yük topoloji taşımıyor — ✅ ölçüldü.
4. Container sağlık kontrolü bu ucu yokluyor ve ölçütü `r.ok` — ✅ yazıldı,
`docker compose config --quiet` temiz. **Koşturulmadı.**
5. `redis-session` durdurulduğunda `--wait` bugün **yeşil** dönüyor, aynı
koşulda yeni uç **kırmızı** yanıyor — ⬜ **koşturulmadı**, Docker gerektiriyor
(§2). Kontrol çiftinin iki yarısı da o koşumda ölçülmeli: eski davranışın yeşil
olduğu **ve** yeni davranışın kırmızı olduğu.

## 6 · Ne ölçüldü, ne ölçülmedi

**Ölçüldü (konteyner yok):** sondanın kör olduğu üç bağımlılık ve her birinin
ayrı ayrı kırmızı yakması · yapılandırılmış deponun adı · yükün sızdırmadığı ·
soğuk açılış beklemesinin yoklamada da geçerli olduğu · `probe()`'un iki depo
uygulamasında da davranışı · rotanın durum kodunu rapora bağladığı · üretimin
yoklamasının gerçekten yapılandırılmış depoya sorduğu.

### Kırmızı ölçümü bir kusuru KENDİ bekçimde buldu

`tools/t62-kirmizi-olcumu.py`, altı kusur. Beşi beklendiği gibi kırmızı yandı ve
"yeşil kalmalı" kontrollerinin dördü de tuttu. **Altıncısı bir bulgu üretti:**

> `compose eski sondaya dönüyor` → `Arayuz_saglik_kontrolu_hazirlik_ucunu_yokluyor`:
> **YEŞİL KALDI**

Sebep: bekçi `ui` servisinin bloğunun **tamamını** okuyordu ve hemen sağlık
kontrolünün üstündeki **gerekçe yorumu** `/api/health/ready` dizgisini zaten
içeriyordu. Yani bekçi *"uç yoklanıyor mu"* değil *"ucun adı dosyada geçiyor
mu"* diye soruyordu.

Sınıf tanıdık — **bir adı metinde bulmak, o şeye dokunmak değil** — ama buradaki
hâli özellikle sinsi: bekçiyi körleştiren şey, onu **açıklayan metnin
kendisiydi**. Düzeltme yorum satırlarını eliyor (`UiProbeCommand`), ve ikinci
koşumda kusur kırmızı yandı.

İkinci ölçüt (`r.ok`) aynı kusurda **kırmızı yanmıştı**, ve bu bir tesadüf:
kusur `r.status < 500` dizgisini komuta **eklediği** için `DoesNotContain`
düşüyordu. Yani iki ölçütten biri kör, diğeri sağlamdı ve ikisi aynı testte
duruyordu — tek bir "yeşil" ikisini birden temsil ediyordu.

**Ölçülmedi:**

- **Kriter 5'in koşumu.** `redis-session` durdurup `--wait`'i izlemek Docker
istiyor; koordinatörün tarafı. Bu ticket'ın taşıyıcı iddiası (*"eski sonda
yeşil kalıyor"*) bugün **kod okumasıyla** duruyor, koşumla değil.
- **Zaman aşımı bütçesi gerçek gecikmeyle sınanmadı.** Yoklamalar paralel ve her
biri 2 sn ile sınırlı, sağlık kontrolünün `timeout`'u 5 sn. Yavaş ama yaşayan
bir bağımlılığın hangi tarafta göründüğü ölçülmedi.
- **`start_period: 20s` yeterli mi.** Uç artık üç ağ çağrısı yapıyor; ilk
yoklama soğuk açılışta 2 sn'ye kadar bekleyebiliyor. Değer değiştirilmedi,
çünkü değiştirmek için ölçüm gerekiyor.
- **Aramadım:** `ui` dışındaki servislerin sağlık kontrolleri aynı sınıftan bir
körlük taşıyor mu. `api`'nin kontrolü kök sayfayı yokluyor ve ClickHouse'a
erişimi sınamıyor — bakmadım, ölçmedim.

## Bağımlılıklar

T49 (container ve sağlık kontrolünün kendisi), B7 (oturum deposu arayüzü).
Kesişme: `deploy/docker-compose.yml` — yalnızca `ui` servisinin `healthcheck`
bloğuna dokunuldu.
