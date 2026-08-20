---
title: "T13 — Next.js iskelet ve BFF"
kind: ticket
status: 2
---

# T13 — Next.js iskelet ve BFF

**Bağımlılık:** — · **Sonraki:** T14
**Yöneten kararlar:** K31 (BFF Next'te) · K26 (Keycloak) · [F1 kapanışı](../../f1-kapanis/index.md)

## Amaç

Ürünün yüzünü taşıyacak uygulama ve **oturumun sunucuda kaldığı** kimlik akışı.
Bu ticket bitmeden hiçbir ekran yazılamaz.

## Kapsam

### İçinde

- `ui/` altında Next.js uygulaması (App Router, TypeScript).
- OIDC **authorization code + PKCE**, `bizigo-ui` confidential client'ı ile.
Realm'de hazır: `redirectUris` `http://localhost:3000/signin-oidc` içeriyor.
- Oturum çerezi: `HttpOnly`, `Secure`, `SameSite=Lax`. **Erişim token'ı çerez**
**içeriğine girmiyor** — sunucu tarafı oturum deposunda duruyor.
- API proxy: tarayıcıdan gelen istek Next sunucusunda token'la zenginleştirilip
`Bizigo.Api`'ye iletiliyor. Tarayıcı `Bizigo.Api`'ye **doğrudan konuşmuyor**.
- Token yenileme: refresh token sunucuda, süresi dolan erişim token'ı şeffaf
yenileniyor.
- `Bizigo.Api`'den **OIDC ve cookie işleyicilerinin kaldırılması** (K31).
- Oturum yoksa korunan yollarda giriş yönlendirmesi.
- **Tasarım temeli** (T28'in önkoşulu): renk, tipografi ölçeği, boşluk, köşe
yarıçapı ve gölge jetonları; açık/koyu tema; ortak bileşenler (tablo, boş durum,
hata durumu, yükleniyor, form alanı). Tutarlılık sonradan eklenmiyor — her ekran
kendi düğmesini çizerse F2 sonunda toparlanmıyor.

### Dışında

- Ekranların kendisi (T15+). Bu ticket'ta yalnızca iskelet, tasarım temeli ve bir
"giriş yaptım" sayfası.
- Çok dillilik (i18n) — F2 boyunca eklenecek, burada altyapısı kurulmayacak.

## Kabul kriterleri

- Giriş akışı tarayıcıda uçtan uca çalışıyor: `analyst.core` giriyor,
`/auth/me` üzerinden rolleri ve grubu görünüyor.
- **Erişim token'ı tarayıcıya hiç ulaşmıyor** — çerez içeriği ve tüm ağ yanıtları
sınanıyor. Bu testin varlığı BFF deseninin tek kanıtı.
- Süresi dolmuş erişim token'ı kullanıcıya hissettirilmeden yenileniyor.
- Çıkış hem Next oturumunu hem Keycloak oturumunu sonlandırıyor.
- `Bizigo.Api` artık cookie/OIDC işleyicisi taşımıyor; yalnızca JWT bearer.
- `ClaimMappingTests` sadeleşiyor ama **JWT tarafındaki**
**`MapInboundClaims = false` kalıyor** — o olmadan roller görünmez oluyor (F1'de
ölçüldü).

## Notlar

Keycloak realm'inde `post.logout.redirect.uris` zaten `attributes` içinde
tanımlı; Next'in çıkış yönlendirmesi bununla eşleşmeli.

Realm yeniden import edildiğinde **imzalama anahtarları değişiyor**; geliştirme
sırasında oturumlar geçersizleşir. Bu bir hata değil, beklenen davranış —
`deploy/keycloak/README.md`'de yazılı.
