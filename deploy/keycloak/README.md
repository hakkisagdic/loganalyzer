# Keycloak realm-as-code

`realm-bizigo.json` **elle admin konsolundan değiştirilmez** — konsolda yapılan
değişiklik bir sonraki `--import-realm` ile kaybolur. Dosyanın asıl değeri claim
mapper'ları: hangi claim'lere ihtiyaç duyduğumuz belgeye değil **dosyaya** yazılı.
Olmasaydı grup claim eşlemesi herkesin makinesinde farklı davranırdı ve sebebi
günlerce bulunamazdı. (K26 · F1 §10.1)

> **Yorumlar neden burada, JSON'da değil:** dosyada `_comment` alanları vardı ve
> Keycloak'ın import'u bilinmeyen alanı **reddediyor** —
> `Unrecognized field "_comment" (class RealmRepresentation)`. Sonuç sessiz
> değildi ama geç görüldü: Keycloak hiç başlamıyordu, konteyner sağlık kontrolü
> bootstrap sırasında geçtiği için bir süre "healthy" görünüyordu. Gerekçe
> kaybolmasın diye buraya taşındı.

## Claim sözleşmesi

**Tamamı `bizigo-claims` scope'unda — ve bu bir zorunluluk.** Realm dosyasında
`clientScopes` dizisi verildiği için Keycloak **yerleşik scope'ları hiç
oluşturmuyor**: import sonrası realm'de yalnızca `bizigo-claims` ve
`offline_access` bulunuyor. İstemcilerin `defaultClientScopes` listesinde
`profile`/`email`/`roles` yazsa bile bunlar var olmadığı için sessizce düşüyor.

Ölçülen sonucu şuydu: kullanıcı access token'ında **`sub` yoktu** (Keycloak 24+
`sub`'ı `basic` scope'una taşıdı) ve hiçbir token'da `preferred_username`
bulunmuyordu. `/auth/me` `subject: "unknown"`, `username: ""` döndürüyordu.
Yetkilendirme rollerden yürüdüğü için çalışıyor görünüyordu; kırılan şey **denetim
kimliğiydi** — "bu isteği kim yaptı" sorusunun cevabı yoktu.

Yerleşik scope'ları dosyaya kopyalamak yerine ihtiyacımız olan claim'leri kendi
scope'umuza yazıyoruz: Keycloak sürümleri arasında taşınabilir ve dosyanın var
olma sebebiyle tutarlı — claim sözleşmesi burada, tek yerde.

| Mapper | Neden |
| --- | --- |
| `roles` (realm rolleri) | Keycloak realm rollerini varsayılan olarak `realm_access.roles` içine gömüyor. Düz `roles` claim'ine çekiyoruz ki claim sözleşmesi IdP'ye özel bir yoldan bağımsız kalsın — yarın Entra ID'ye geçilirse uygulama tarafı değişmesin. |
| `groups` (`full.path=true`) | **Bilinçli.** Kapatılırsa iç içe gruplarda ad çakışması olur: `network/core` ile `platform/core` ayırt edilemez. Bedeli, claim'in başında eğik çizgi olması (`/network/core`) — `AccessScopeResolver` bunu normalize ediyor. |
| `subject` (`sub`) | `basic` scope'u olmadığı için elle. Denetim kimliği bundan okunuyor. |
| `username` (`preferred_username`) | `profile` scope'u olmadığı için elle. |
| `audience-bizigo-api` (`aud`) | Servis hesabı token'ında `aud` hiç yoktu ve API doğrulaması onu reddediyordu. Paylaşılan scope'ta olması, UI ile collector'ın **aynı** sözleşmeyi tek yerden almasını sağlıyor. |

## Issuer sabit olmak zorunda

`docker-compose.yml` içinde `KC_HOSTNAME` ayarlı. Olmazsa Keycloak issuer'ı isteğin
geldiği host'tan türetiyor: collector ağ içinden `http://keycloak:8080/...` ile
token alıyor, API `http://localhost:8180/...` bekliyor ve **her satır 401 ile
düşüyor**. `--hostname-strict=false` ile birlikte Keycloak yine iki host'tan da
cevap veriyor, yalnızca issuer sabit kalıyor.

Realm'i yeniden import ederken (veritabanı silinerek) **imzalama anahtarları
değişiyor**; collector'ın önbellekteki token'ı geçersiz kalır. Yeniden import
sonrası `docker compose restart otel-collector` gerekiyor.

## İstemciler

| Client | Yapılandırma | Neden |
| --- | --- | --- |
| `bizigo-ui` | `publicClient=false`, `standardFlow=true` | BFF deseni (F1 §10.1.2): authorization code + PKCE, gizli anahtar sunucuda. Token **tarayıcıda saklanmıyor** — BFF cookie veriyor. `directAccessGrantsEnabled=false`, yani parola akışı yok. |
| `bizigo-collector` | servis hesabı, rol **yalnızca** `ingest` | Kimlik sızarsa veri **yazılabilir, okunamaz**. Rol ayrımının tek sebebi bu. Collector tarafında `oauth2clientauthextension` token'ı kendi alıp yeniliyor. |

Keycloak servis hesabı kullanıcısını `service-account-<clientId>` adıyla
oluşturuyor; rolü realm dosyasında o ada bağlanıyor.

## Geliştirme kullanıcıları

Kapsam ayrımının **gerçek gruplarla** gösterilebilmesi için iki farklı grupta iki
kullanıcı var — tek kullanıcıyla kapsam filtresinin çalıştığı gösterilemez.

| Kullanıcı | Grup | Rol |
| --- | --- | --- |
| `analyst.core` | `/network/core` | `analyst` |
| `analyst.edge` | `/network/edge` | `analyst` |
| `admin.bizigo` | — | `admin` |

Parolalar yalnızca geliştirme içindir ve `.env` ile birlikte üretime taşınmaz.

## Doğrulama

Realm'in gerçekten yüklendiği, konteyner durumundan **değil** şu uçtan anlaşılır:

    curl -sf http://localhost:8180/realms/bizigo/.well-known/openid-configuration

Konteynerin `healthy` olması yetmiyor: sağlık kontrolü bootstrap sırasında da
geçiyor ve import o sırada henüz bitmemiş oluyor.
