---
title: BFF deseni — token tarayıcıya hiç ulaşmıyor
category: concepts
tags: [arayuz, guvenlik, mimari, kavram, bizigo]
aliases: [K31, backend-for-frontend, oturum sunucuda]
relationships:
  - target: "[[concepts/f2-kapsam-tek-kapi]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[references/f2-kapanis]]"
    type: derived_from
  - target: "[[skills/f2-ekran-tutarliligi]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/nextjs-iskelet-bff/index.md
  - docs/epic/tickets-f2/openapi-tip-uretimi/index.md
  - docs/epic/tickets-f2/log-arama-ekrani/index.md
  - docs/epic/tickets-f2/f2-dogrulamasi/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/f2-dogrulamasi/index.md=87ed12f0cd0e docs/epic/tickets-f2/log-arama-ekrani/index.md=e4425034ae89 docs/epic/tickets-f2/nextjs-iskelet-bff/index.md=c49270b469fb docs/epic/tickets-f2/openapi-tip-uretimi/index.md=56ec69775b3f"
summary: K31 oturumu Next.js sunucusuna taşıdı; erişim token'ı tarayıcıya hiç geçmiyor ve bunun tek kanıtı yanıtın her baytını tarayan bekçi. Kararın gerekçesi canlı Keycloak'ta kaynağında doğrulandı.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:45:00Z
updated: 2026-08-24T17:45:00Z
---

# BFF deseni — token tarayıcıya hiç ulaşmıyor

F2'nin K31 kararı: **BFF Next.js sunucusunda**. OIDC akışı ve oturum çerezi
Next'te duruyor, API'ye sunucudan sunucuya gidiliyor, ve `Bizigo.Api` **saf
kaynak sunucusu** olarak kalıyor — yalnızca JWT bearer. Bedeli açıkça yazılı:
Node dağıtıma giriyor (`docs/epic/f2-teknik-plan/index.md`, Kararlar tablosu).

## Kararın doğrudan sonucu: API sadeleşiyor

F1'den kalan cookie ve OIDC işleyicisi API'de duruyordu. K31'den sonra bunlar
**API'den çıkmalı**, çünkü iki yerde oturum yönetimi demek *kullanıcının hangi
yoldan girdiğine göre farklı davranan bir kapsam* demek. API'de kalan: JWT
doğrulaması ve `/auth/me` (BFF'in kimlik sorgusu).

Tek istisna korunuyor: **JWT tarafındaki `MapInboundClaims = false`**. O olmadan
roller görünmez oluyor — F1'de ölçülmüş. `ClaimMappingTests` sadeleşiyor ama bu
satır kalıyor (`docs/epic/tickets-f2/nextjs-iskelet-bff/index.md`, kabul
kriterleri).

## Kararın kaynağında doğrulanması

Kapanış belgesi K31'i tercih olmaktan çıkarıp **ölçülmüş bir zorunluluk**
hâline getirdi. Realm dosyası `clientScopes` verdiği için Keycloak yerleşik
scope'ları hiç oluşturmuyor:

| İstenen scope | Cevap |
| --- | --- |
| `openid` | geçiyor |
| `openid profile` | `invalid_scope` |
| `openid profile email` | `invalid_scope` |

F1'den kalma OIDC işleyicisi **üçüncü satırı istiyordu**. Tarayıcı akışı sevk
edilseydi ilk kullanıcının ilk girişinde patlardı ve **hiçbir test bunu
görmezdi**: akış hiç koşulmamıştı. Tam olarak
[[concepts/sessiz-yanlis-davranis]] sınıfı — belirti üretmeyen, yalnızca canlı
denendiğinde çıkan bir kırık.

## Desenin kanıtı bir tek testtir

T13 bunu açıkça yazıyor: erişim token'ı tarayıcıya hiç ulaşmıyor ve **bu testin
varlığı BFF deseninin tek kanıtı**. Çerez `HttpOnly` · `Secure` ·
`SameSite=Lax`; token çerez *içeriğine* girmiyor, sunucu tarafı oturum
deposunda duruyor; yenileme sunucuda şeffaf; çıkış hem Next hem Keycloak
oturumunu sonlandırıyor.

Kapanışta ölçülenler: oturum çerezi **opak** (JWT değil), BFF'in altı yanıtının
hiçbirinde token yok, yukarı akışta var, sahte çerez 401. Bekçi
(`token-isolation`) kümesini tahmin etmiyor, **yanıtın her baytını** tarıyor —
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]] tuzağına düşmeyen bir kapı
şekli.

T27 aynı kontrolü çapraz kesen bir bekçiye çevirdi: hiçbir yanıtta, çerezde ya
da `localStorage`'da token yok. `localStorage` ayağı **T27'de eklendi** — yani
desen ilk yazıldığında bir açık ucu vardı ve onu ancak doğrulama turu gördü.

## Ekranlar da desene uyuyor

T15 sevk edildiğinde ekranın **tamamı sunucu bileşeni**: veri sunucuda
çekiliyor, tarayıcı `Bizigo.Api`'yi hiç görmüyor, token sayfaya hiç geçmiyor.
Yan etkisi bir bonus: filtre formu düz bir `GET` formu, "Sonraki sayfa" bir
bağlantı — **JavaScript kapalıyken de çalışıyor**.

Aynı desen istemci tarafında hata eşlemesini de belirliyor (T14): `401` BFF'in
yenileme akışına, `403` "yetkiniz yok"a, `404` "bulunamadı"ya. Kapsam dışı olay
**404** dönüyor, 403 değil — 403 "böyle bir olay var" bilgisini sızdırırdı.
Ayrıntısı: [[concepts/f2-kapsam-tek-kapi]].

## Kabul edilen risk

**B7 — Next oturum deposu bellek içi.** Çok kopyaya çıkana kadar gerçek bir
sorun değil; Redis **dağıtım kararı**, kod kararı değil
(`docs/epic/f2-kapanis/index.md` §3). Riski kabul etmek onu unutmak olmadığı
için tabloda duruyor.

## Çıkarım

Desenin değeri "token'ı saklamak" değil, **token'ın nerede olabileceğini tek
bir yere indirmek**: sunucu tarafı oturum deposu. O yer bilindiği için bekçi
"her yanıtı tara" diyebiliyor; dağıtık bir saklama olsaydı bekçi de dağıtık bir
liste olurdu. ^[inferred]

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — K31 ve "K31'in sonucu: `Bizigo.Api` sadeleşiyor"
- `docs/epic/tickets-f2/nextjs-iskelet-bff/index.md` — T13 kapsam ve kabul kriterleri
- `docs/epic/tickets-f2/openapi-tip-uretimi/index.md` — T14, 401/403/404 eşlemesi
- `docs/epic/tickets-f2/log-arama-ekrani/index.md` — T15 "Sevk edilen": sunucu bileşeni
- `docs/epic/tickets-f2/f2-dogrulamasi/index.md` — T27 token sızıntısı bekçisi
- `docs/epic/f2-kapanis/index.md` — §1 Keycloak ölçümü, §3 B7, §4 `token-isolation`
