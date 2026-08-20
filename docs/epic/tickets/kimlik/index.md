---
title: "T09 — Kimlik: Keycloak, OIDC, servis hesapları"
kind: ticket
status: 2
---

# T09 — Kimlik ve yetkilendirme

**Bağımlılık:** T02 · **Sonraki:** T10
**Yöneten belgeler:** [F1 §10.1, §10.1.1, §10.1.2](../../f1-teknik-plan/index.md) ·
[K18, K26](../../mimari-kararlar/index.md)

## Amaç

Kim olduğunu ve **neyi görebileceğini** belirlemek. T02'nin kurduğu `IScopedQuery`
kapısına gerçek kimlik bağlanıyor.

## Kapsam

### İçinde

1. **Keycloak realm-as-code** — `deploy/keycloak/realm-bizigo.json`, `--import-realm`
ile yüklenir. İçinde: realm, roller (`reader/analyst/author/admin`), örnek gruplar,
UI client'ı, collector servis hesabı, ve **claim mapper'ları**.
Bu dosya olmadan grup claim eşlemesi herkesin makinesinde farklı davranır.
2. **Claim sözleşmesi** ([F1 §10.1.1](../../f1-teknik-plan/index.md)) — ürünün
IdP'den beklediği tek şey: `sub`, `preferred_username`, `roles`, `groups`.
Bu sözleşmenin **testi olmalı**: sahte token ile dört claim'in de doğru
çözüldüğü doğrulanır.
3. **API kimlik doğrulama** — `AddJwtBearer()`, Keycloak imzalı erişim token'ı.
Ürün kendi kullanıcı tablosunu tutmaz.
4. **BFF** — React/Next için `AddOpenIdConnect` (authorization code + PKCE) + cookie
  - YARP ters vekil. Token tarayıcıda saklanmaz.
5. **Grup → `owner_group` çözümü** — `idp_group_mapping` üzerinden.
**Keycloak tuzağı:** Group Membership mapper tam yolu başında eğik çizgiyle basar
(`/network/core`). Tam yol açık kalır, eşleme tablosu tam yolu saklar, giriş
`TrimStart('/')` ile normalize edilir.
6. **Collector servis hesabı** — `client_credentials`, collector tarafında
`oauth2clientauthextension`. Rolü **yalnızca `ingest`** — sorgulama yetkisi yok.
`/v1/logs` bu ticket'ta kapatılır (T03'te açıktı).
7. **Audit log** — her sorgu: kim, hangi kapsam, hangi filtre, kaç satır.

### Dışında

F4 senaryo kimlikleri (kalıp burada kuruluyor, kullanımı F4), kurumun gerçek IdP'sine
(Entra ID) geçiş — claim sözleşmesi sayesinde kodu etkilemiyor.

## Kabul kriterleri

docker compose up sonrası Keycloak realm'i hazır geliyor, elle ayar yokUI'dan OIDC ile giriş yapılıyor; token tarayıcı depolamasında yokroles claim'i uygulama rollerine doğru çözülüyor/network/core biçimli grup claim'i owner_group'a doğru eşleniyorKimliksiz istek /v1/logs'a 401 alıyorHer sorgu audit_log'a düşüyor

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| Realm-as-code | `deploy/keycloak/realm-bizigo.json` — roller, gruplar, iki client, claim mapper'ları, geliştirme kullanıcıları |
| Claim → kapsam | `src/Bizigo.ControlPlane/AccessScopeResolver.cs` (`GroupMapping` saf tip) |
| Kimlik doğrulama | `src/Bizigo.Api/AuthenticationSetup.cs` — JWT Bearer + Cookie/OIDC |
| BFF uçları | `src/Bizigo.Api/AuthEndpoints.cs` — `/auth/login`, `/auth/logout`, `/auth/me` |
| Collector kimliği | `deploy/otel/collector.yaml` `oauth2client` + compose ortam değişkenleri |

**Üç tasarım noktası:**

1. **Varsayılan şema Bearer.** Kimliksiz bir API isteği 401 alıyor, giriş sayfasına
yönlendirilmiyor. Tarayıcı akışı yalnızca `/auth/login` çağrıldığında devreye
giriyor. Tersi olsaydı collector 302 alırdı.
2. **`GroupMapping` veritabanından ayrıldı.** Çevrim mantığı K17'nin tam ortasında
ve bir EF sağlayıcısı kurmadan sınanabilmesi gerekiyordu; 12 test bu yüzden
bağımlılıksız koşuyor.
3. **Kimlik kapalıyken bile kapsam kapalı.** `Auth:Enabled=false` yalnızca
kimlik doğrulamayı atlıyor; `AccessScope` yine `Denied` başlıyor. "Kimlik yoksa
her şeyi gör" varsayılanı bu üründe yapılabilecek en pahalı hata olurdu.

**Kapsam dışı bırakılan:** YARP ters vekil. UI henüz yok, vekilleyecek bir şey de
yok. Kimlik tarafı doğru kurulduğu için UI geldiğinde o adım yalnızca yönlendirme
işi olacak — T10 ya da UI ticket'ında.

**Doğrulama:** derleme 0 uyarı, birim testleri **313/313** (12'si yeni claim
sözleşmesi testi). Gerçek Keycloak'a karşı uçtan uca giriş akışı **denenmedi** —
compose yığınının ayağa kalkması CI'da doğrulanıyor ama tarayıcı akışı elle
sınanmalı.

## Notlar

- Keycloak `start-dev` **kullanılmaz** — bellek içi H2, her yeniden başlatmada veri
gider. `start --optimized` + Postgres.
- Claim sözleşmesinin dar tutulması bilinçli: Keycloak → Entra ID geçişi IdP
tarafında mapper ayarı + eşleme tablosu satırı demek, **kodda değişiklik yok**.
- Collector kimliği sızarsa veri **yazılabilir, okunamaz**. Rol ayrımının sebebi bu.
