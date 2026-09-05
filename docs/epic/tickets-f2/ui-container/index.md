---
title: "T49 — Dashboard container'ı: yığının tamamı tek komutla kalkıyor mu?"
kind: ticket
status: 1
---

# T49 — Dashboard compose'a giriyor

## 1 · Bugünkü hâl ölçüldü

`deploy/docker-compose.yml` on servis taşıyor: `clickhouse` · `redis` ·
`redis-session` · `postgres` · `rustfs` · `keycloak` · `otel-collector` ·
`sidecar` · `api` (profil arkasında) · `ssh-sim` (profil arkasında).

**Dashboard yok.**

API için container **var** ve gerekçesi README'de yazılı:

> **API'yi container'da koşturmak** ayrı bir soruya cevap veriyor — *yığının
> tamamı tek komutla kalkıyor mu* — ve varsayılanı değiştirmiyor.

Ama o soru bugün **hayır** diye cevaplanıyor: API kalkıyor, ekran kalkmıyor.

## 2 · Bunun sessiz olan tarafı

Uçtan uca harness ekranı **yerelden** koşturuyor (`npm run e2e`,
`playwright test` yerel sunucuya bakıyor). Yani BFF'in **container ağında**
çalışıp çalışmadığı bugün **hiç sınanmıyor**, ve sınanmadığı da hiçbir yerde
yazılı değil.

Bu, F1'den beri tekrarlanan sınıf: bir katman denenmediği hâlde çalıştığı
varsayılıyor, ve varsayımın yanlış olduğu ancak biri gerçekten denediğinde
görünüyor. Compose'un YAML'ı da tam böyle kırılmıştı — derleme temiz, testler
yeşil, ve yığın **hiç ayrıştırılamıyordu**.

## 3 · Container'a taşımanın zorunlu kılacağı şeyler — ve emsali elimizde

API container'a taşınırken **iki karar ölçülerek** bulundu (README):

| Karar | Sebep |
| --- | --- |
| `Auth:MetadataAddress` | Anahtarın *nereden indirileceği* ile *kime güvenildiği* ayrıldı — container içinde `localhost:8180` Keycloak değil **container'ın kendisi** |
| `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` | Metadata adresini ayırmak **yetmedi**: belge iniyordu ama içindeki `jwks_uri` yine `localhost:8180` gösteriyordu |

**BFF'in aynı sınıftan sorunları olacak ve bunlar öngörülebilir:**

- **API'yi servis adıyla bulmak.** BFF bugün `localhost` üzerinden konuşuyor;
container ağında `api:8080`. API'nin Keycloak'ta yaşadığının aynısı, bir
katman yukarıda.
- **Tarayıcı ile sunucu tarafının farklı adresler görmesi.** Next.js'in sunucu
bileşeni container ağından, tarayıcıya inen kod `localhost`'tan konuşuyor.
Tek bir taban URL varsayımı ikisinden birini kırar — ve **hangisinin
kırıldığı sessiz olur**: sunucu tarafı kırılırsa 500, tarayıcı tarafı
kırılırsa boş ekran.
- **`redis-session`.** BFF'in oturum katmanı zaten compose'da; bağlantı
dizgesi container'dan servis adıyla çözülmeli.
- **Keycloak yönlendirmesi.** Tarayıcı `localhost:8180`'e gidiyor, BFF
container'dan `keycloak:8080`'e. API'nin ayrımının aynısı.

Bunları **tahmin olarak yazıyorum, ölçülmediler.** Ticket sahibi ölçecek ve
hangisinin gerçekten çıktığını yazacak — çıkmayanı da.

## 4 · Kapsam

**İçinde**

- `ui` servisi, `profiles: ["api"]` ile **aynı profilde**. Gerekçe: ekranın
API'siz kalkması anlamsız, ve iki ayrı profil *"hangisini açacağım"* sorusunu
doğurur.
- Üretim derlemesi (`next build`), geliştirme sunucusu değil. Container'ın
cevapladığı soru *"temiz bir makinede kalkıyor mu"*, *"hızlı yeniden yükleniyor
mu"* değil.
- Uçtan uca harness'ın **container yığınına karşı da** koşabilmesi. Yerel
koşum **varsayılan kalıyor** — README'nin sıcak yeniden yükleme gerekçesi
geçerli ve değiştirilmiyor.

**Dışında**

- Üretim dağıtımı, imaj yayınlama, kayıt defteri. Bu ticket **geliştirme
yığınının bütünlüğü** hakkında.
- Varsayılanı değiştirmek. `docker compose up -d` bugünkü on servisi
kaldırmaya devam ediyor.

## 5 · Kabul kriterleri

1. `docker compose --profile api up -d --wait` **ekranı da** kaldırıyor ve
sağlık kontrolünden geçiyor.
2. Container'daki ekrandan **giriş yapılabiliyor** — Keycloak yönlendirmesi
tarayıcı ve sunucu taraflarının **ikisi için de** çözülüyor.
3. Uçtan uca harness container yığınına karşı koşuyor; hangi testlerin
koştuğu ve hangilerinin **koşmadığı** yazılı.
4. `docker compose config --quiet` temiz — bu kapı T01'den beri duruyor ve bir
kez dört merge boyunca kırmızı kaldı.
5. **Yerel döngü bozulmadı**: `npm run dev` ve mevcut `e2e` yolu aynı şekilde
çalışıyor.
6. Container'a taşımanın **gerektirdiği her ayar**, API'nin `MetadataAddress`
kaydı gibi, **gerekçesiyle** yazılı. Bir sonraki kişi *"bu neden burada"*
sorusunu koda bakarak cevaplayabilmeli.

## 6 · Ne ölçüldü, ne arandı

**İlk bakılacak yer:** `deploy/docker-compose.yml`'in `api` servisi —
`Auth:MetadataAddress` ve issuer ayrımı orada, ve BFF'in ihtiyacı olan
şeklin emsali o.

**Aradım ve elemedim:** compose'un on servisi (UI yok, doğrulandı) ·
README'nin container gerekçesi ve iki ölçülmüş kararı · `api` ve `simulators`
profilleri.

**Aramadım:** BFF'in bugün `localhost`'u nerede sabitlediğini —
`ui/src/lib/api/` altındaki taban URL çözümüne **bakmadım**. Ve Next.js'in
`output: standalone` yapılandırmasının bugün açık olup olmadığını; imaj
boyutu ve çalıştırma şekli ona bağlı.

## 6.1 · Uygulanırken ölçülenler

Yukarıdaki bölüm **ticket yazılırken** doğruydu. Uygulama turu onu iki yerden
düzeltti; bölüm silinmiyor, çünkü neyin tahmin edildiği ile neyin çıktığı
arasındaki fark bu ticket'ın en pahalı bilgisi.

### §3'ün dört tahmini — üçü çıkmadı

§3 dört sorun öngörüyor ve *"tahmin olarak yazıyorum, ölçülmediler"* diyor.
Ölçüldü:

| Tahmin | Çıktı mı | Ne gerekti |
| --- | --- | --- |
| API'yi servis adıyla bulmak | **Hayır** | `BIZIGO_API_URL` zaten var — compose girdisi |
| Tarayıcı/sunucu taban URL ayrımı | **Hayır** (API için) | Ayrım zaten yapısal |
| `redis-session` | **Hayır** | `BFF_SESSION_STORE` + `BFF_REDIS_URL` zaten var |
| Keycloak yönlendirmesi | **Evet** | Turun tek kod değişikliği |

İkinci satırın cevabı, §6'nın *"aramadım"* dediği yerde **yazılıymış**:
`ui/src/lib/api/client.ts:33` — *"Vekilin kökü. Mutlak API adresi burada
bilinçli olarak YOK."* Tarayıcı her isteği göreli `/api/bff/…`'ye yolluyor;
`apiBaseUrl`'i yalnızca `server.ts` okuyor. Kırılacak tek bir taban URL
varsayımı yok.

### Çıkan tahmin, emsalinden zor

BFF keşif belgesinden **iki ayrı yöne giden** uçlar okuyor ve tek adres ikisini
karşılayamıyor:

| Uç | Kim gidiyor | Container'da doğru adres |
| --- | --- | --- |
| `authorization_endpoint`, `end_session_endpoint` | **tarayıcı** | `localhost:8180` |
| `token_endpoint`, `jwks_uri` | Next sunucusu | `keycloak:8080` |

Çözüm `KEYCLOAK_METADATA_URL` + `discover()`'ın ön kanalı genel kökene
sabitlemesi. Gerekçesi `ui/src/lib/auth/oidc.ts` içinde (`onPublicOrigin`) ve
README'de.

### İki "aramadım" kalemi

- **`output: standalone` kapalıydı.** Açıldı; `.next/standalone/server.js`
gerçekten üretiliyor ve `ui/Dockerfile`'ın `CMD`'si ona dayanıyor.
- **`ui/public` yok.** Çoğu Next Dockerfile'ında duran
`COPY … /app/public ./public` satırı imaj derlemesini düşürürdü — ve bu ancak
imaj gerçekten derlendiğinde görünürdü.

### Bu turda ölçülmeyenler

Kabul kriterleri **1, 2 ve 3** koşturulmadı: yığını kaldırmak, container'daki
ekrandan giriş yapmak ve uçtan uca harness'ı container'a karşı koşturmak
Docker istiyor ve §2 onu koordinatöre veriyor. Yeşil gösterilmiyor; `status`
bu yüzden **1**.

`ui` servisinin sağlık kontrolü ölçütü `< 500` ve dayandığı varsayım —
oturumsuz isteğin kök sayfadan giriş akışına yönlenmesi (3xx) — **doğrulanmadı**.
Sağlıklıya dönmezse ilk bakılacak yer o satır, ikincisi `HOSTNAME=0.0.0.0`.
