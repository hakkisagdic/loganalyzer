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

| Mapper | Neden |
| --- | --- |
| `roles` (realm rolleri) | Keycloak realm rollerini varsayılan olarak `realm_access.roles` içine gömüyor. Düz `roles` claim'ine çekiyoruz ki claim sözleşmesi IdP'ye özel bir yoldan bağımsız kalsın — yarın Entra ID'ye geçilirse uygulama tarafı değişmesin. |
| `groups` (`full.path=true`) | **Bilinçli.** Kapatılırsa iç içe gruplarda ad çakışması olur: `network/core` ile `platform/core` ayırt edilemez. Bedeli, claim'in başında eğik çizgi olması (`/network/core`) — `AccessScopeResolver` bunu normalize ediyor. |

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
