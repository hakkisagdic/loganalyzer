---
title: "M09 — Kimliğin bulunması ve kaynağa bağlanması"
kind: ticket
status: 2
---

# M09 — İstemci Keycloak'ı bulamıyordu, ve API'nin token'ı MCP'de geçiyordu

Uyduğumuz revizyonun (`2026-07-28`) yetkilendirme spesifikasyonu iki **MUST**
koyuyor ve ikisi de karşılanmıyordu.

## 1 · Bugünkü hâl — ölçüldü

### MUST 1 · Kaynak metadata'sı yok (RFC 9728)

Spesifikasyon: *"MCP servers **MUST** implement OAuth 2.0 Protected Resource
Metadata"*, ve 401'de `WWW-Authenticate: Bearer resource_metadata="…"`
dönmeli.

`src/Bizigo.Mcp` ve `src/Bizigo.Api` altında arandı: `WWW-Authenticate` ·
`resource_metadata` · `oauth-protected-resource` dizgilerinin **hiçbiri
geçmiyor**. `AddMcpServer()` çağrılıyor, `AddMcp()` / `McpAuthenticationOptions`
çağrılmıyor. `/mcp` çıplak 401 dönüyor, yani istemci yetkilendirme sunucusunu
**bulamıyor** ve token elle yapılandırılmak zorunda.

> **Koşturulmadı.** Bu tespit **kod okumasıdır**; `/mcp`'nin canlı 401 gövdesi
> görülmedi. Docker koordinatörde (`CLAUDE.md` §2).

### MUST 2 · Öncül düzeltildi — kitle bağlanması **çalışıyor**, eksik olan başka

Bu ticket'ın ilk hâli *"`AuthOptions.Audience = "account"` — Keycloak
varsayılanı"* diyordu. **Ölçüm bunu çürüttü:**

| Ölçüm | Değer |
| --- | --- |
| `AuthenticationSetup.cs` | `public string Audience { get; set; } = "account";` — **C# varsayılanı** |
| `appsettings.json` `Auth:Audience` | **`bizigo-api`** |
| `appsettings.Development.json` | `Audience` **yok** (ezmiyor) |
| `AuthenticationSetup.cs` | `ValidateAudience = true`, `jwt.Audience = options.Audience` |
| `realm-bizigo.json` | `bizigo-claims` scope'unda audience mapper: `included.client.audience: bizigo-api` |

Yani Keycloak `aud: bizigo-api` basıyor, API onu doğruluyor: **kitle bağlanması
uçtan uca çalışıyor.** *"Keycloak varsayılanı kullanıyoruz"* sevk edilen
yapılandırma için doğru değil.

**Ama MUST'ın özü ayakta ve şekli daha keskin:** RFC 8707 token'ın **o
kaynağa** düzenlenmiş olmasını istiyor, ve **MCP sunucusunun kendine ait bir
kaynak kimliği yok**. `/mcp` bugün `aud: bizigo-api` taşıyan bir token'ı kabul
ediyorsa, **API için basılmış bir token MCP sunucusunda yeniden
kullanılabilir** — kaynak bağlamanın engellemek istediği şey tam olarak bu.

Öncül farkı ticket'ın işini değiştiriyor: *"audience'ı düzelt"* çalışan bir
yapılandırmayı bozma riski taşırdı; *"MCP'ye ayrı kaynak kimliği ver"*
`bizigo-api`'yi olduğu yerde bırakıyor ve BFF ile collector akışına dokunmuyor.

### Patlama yarıçapı — ölçüldü, ve bu ticket'ın asıl riski

| Ölçüm | Değer |
| --- | --- |
| `RequireAuthorization` çağrısı | **45** |
| Bunları taşıyan uç dosyası | **14** |
| **Kendi kimlik şemasını belirten uç** | **0** |
| `AllowAnonymous` | 1 — `ChangeWebhookEndpoints` (gövdesi HMAC ile doğrulanıyor) |
| `OnChallenge` / `WWW-Authenticate` özelleştirmesi | yok |

`AuthenticationSetup.cs` hem `DefaultAuthenticateScheme`'i hem
`DefaultChallengeScheme`'i JWT Bearer'a bağlıyor ve **hiçbir uç kendi şemasını
belirtmiyor**. Yani `DefaultChallengeScheme`'e dokunmak **45 ucun tamamının**
401 davranışını değiştirir ve BFF'in oturum akışını (K31) ilgilendirir.

SDK'nın yaygın örneği tam olarak bunu yapıyor.

## 2 · Ticket'ın ilk ölçümü: SDK adlandırılmış şemayı destekliyor mu?

**Destekliyor.** `ModelContextProtocol.AspNetCore` 2.2.0'ın API'si okundu:

```
AddMcp(AuthenticationBuilder, string authenticationScheme, string displayName,
       Action<McpAuthenticationOptions>)
```

Üç argümanlı aşırı yükleme var, ve `McpAuthenticationHandler` sıradan bir
`AuthenticationHandler<>`. Yani varsayılana dokunmadan `/mcp`'ye kendi şeması
verilebiliyor.

Desteklemeseydi karar koordinatöre geçecekti (*"45 ucu etkileyen bir
değişikliği göze alıyor muyuz"*); geçmedi.

## 3 · Yapılanlar

### Adlandırılmış iki şema

- **`bizigo-mcp`** — meydan okumayı üretir (`WWW-Authenticate` +
  `resource_metadata`), `/.well-known/oauth-protected-resource` belgesini
  sunar, ve **doğrulamayı iletir**.
- **`bizigo-mcp-bearer`** — MCP'nin token doğrulayıcısı. Aynı issuer, aynı
  claim sözleşmesi, **farklı kitle**.

Varsayılan şemalar **değişmedi**.

Ortak yapılandırma `ConfigureBearer(...)`'a alındı; iki şema arasındaki tek
fark kitle. İkinci bir doğrulama yolu yazmak, iki yüzeyin claim sözleşmesinin
sessizce ayrışması demek olurdu (§9).

### Kaynak kimliği

`AuthOptions.McpResource` eklendi, `Audience`'tan ayrı. `/mcp` şemasını
**açıkça** belirtiyor ve şemasını belirten **tek** uç — diğer 45 çağrı
varsayılanda duruyor.

### Realm

`bizigo-mcp` client scope'u eklendi, audience mapper'ıyla
(`included.custom.audience`), ve `bizigo-ui`'ye **isteğe bağlı** bağlandı.

**İsteğe bağlı olması önlemin kendisi:** varsayılan olsaydı her token `aud`'una
MCP kaynağını da alırdı ve API için basılmış bir token `/mcp`'de geçerdi — yani
kaynak bağlaması hiç kurulmamış olurdu.

### `"account"` varsayılanı kaldırıldı

`Audience` ve `McpResource` artık **varsayılansız** ve eksikse **açılışta**
patlıyor. Doğru yapılandırılmış olmak, yanlış yapılandırılamayacağı anlamına
gelmiyor: `Auth:Audience` yazılmayan bir dağıtım sessizce Keycloak'ın varsayılan
kitlesine doğrular ve hata vermezdi — `CLAUDE.md` §7'nin sınıfı.

Emsali M08'de: çözücü kayıtlı değilse **kurulumda** patlıyor, çağrıda değil.

## 4 · Kabul kriterleri ve nasıl ölçüldükleri

| # | Kriter | Nasıl |
| --- | --- | --- |
| 1 | **`/mcp` dışındaki 44 uç etkilenmiyor** | `Varsayilan_semalar_MCP_yuzunden_degismiyor` — varsayılan şemaların JWT Bearer kaldığını tutuyor |
| 2 | MCP kendi şemasını taşıyor | `MCP_kendi_adlandirilmis_semasini_tasiyor` |
| 3 | Kaynak bağlama | `MCP_kendi_kaynagini_dogruluyor_APInin_kitlesini_degil` — kitlelerin **eşit olmadığını** da sınıyor |
| 4 | Keşif | `MCP_kaynak_metadatasini_ilan_ediyor` |
| 5 | Varsayılansız yapılandırma | `Eksik_kaynak_kimligi_acilista_patliyor` (4 kombinasyon) |
| 6 | Realm scope'u isteğe bağlı | `KeycloakRealmTests.Mcp_kaynak_scopeu_istege_bagli` |
| 7 | **Realm ↔ appsettings ayrışamaz** | `Realmin_bastigi_MCP_kitlesi_APInin_dogruladigiyla_ayni` |

Kriter 7 ayrı durmayı hak ediyor: ayrışırlarsa **hata yok** — Keycloak basar,
API reddeder, görünen tek şey *"MCP çalışmıyor"* olur. T53'ün ölçtüğü *"aynı
şeyin iki gösterimi"* sınıfının kimlik katmanındaki hâli.

### Ölçülen kırmızılar

| # | Kusur | Sonuç |
| --- | --- | --- |
| J | `DefaultChallengeScheme = bizigo-mcp` (SDK'nın yaygın örneği) | **KIRMIZI** — kriter 1 |
| K | `bizigo-mcp-bearer` API'nin kitlesini doğrulasın | **KIRMIZI** — kriter 3 |

İkisi de geri alındı; kusurun dosyada olduğu uygulandıktan sonra **iddia
edildi**, geri alındıktan sonra kalıntı olmadığı ayrıca iddia edildi
(`CLAUDE.md` §6).

## 5 · Bu değişikliğin kırdığı sekiz test — üçü de bulgu

Tam paket **8 kırmızı** verdi ve hiçbiri gürültü değildi:

1. **`ClaimMappingTests`** `McpResource` vermiyordu → yeni zorunluluk yakaladı.
2. **`McpIdentityTests` · `McpHttpTransportTests`** kendi şemalarını
   (`M08Test`, `M01Test`) kaydedip `/mcp`'nin **varsayılana düşmesine**
   güveniyordu. Harness'lar üretimin şema adına bağlandı — **uyarlama değil
   düzeltme**: harness artık üretimin gerçekten kullandığı şemayı ölçüyor.
3. **`McpKeycloakIdentityTests.BuildHost`** `Audience`/`McpResource`
   vermiyordu, yani **sessizce `"account"` varsayılanına dayanıyormuş**. Canlı
   Keycloak `aud: bizigo-api` bassaydı bu kurulum token'ı reddederdi ve sebebi
   görünmezdi. Değerler açıkça yazıldı.

Üçüncüsü kaldırılan varsayılanın neden kaldırılması gerektiğinin **kendi
kanıtı**: varsayılan, bir testin yanlış yapılandırıldığını gizliyordu.

## 6 · Canlı Keycloak — yazıldı, **koşturulmadı**

`McpKeycloakIdentityTests`'e iki test eklendi (yeni dosya açılmadı, §9):

- **`Canli_realmde_401_kaynak_metadatasini_isaret_ediyor`** — 401'in
  `resource_metadata` taşıdığı, **işaret edilen adresin gerçekten açıldığı**
  (işaret eden ama açılmayan bir adres, olmayan metadata'dan kötü), ve
  belgedeki `authorization_servers`'ın realm'e vardığı.
- **`Canli_realmde_API_tokeni_MCP_yuzeyinde_gecmiyor`** — **negatif** test.
  Diğer bütün testler *"doğru token geçiyor"* diyor; kaynak bağlamayı ölçen
  tek şey *"yanlış token geçmiyor"*. Kitle doğrulaması bir gün gevşerse her
  pozitif test yeşil kalır.

İkincisi yeni bir env değişkeni istiyor: **`BIZIGO_MCP_API_TOKEN`** —
`bizigo-mcp` scope'u **istenmeden** alınmış bir belirteç. Test iki token'ın
aynı olmadığını ayrıca sınıyor; aynı olurlarsa ölçüm sessizce anlamsızlaşır.

## 7 · Bilinen sınırlar ve açık sorular

1. **`/mcp`'nin canlı 401'i koşturulmadı** — §1'deki tespit kod okumasıdır.
2. **`McpResource` değeri yerel geliştirmeye göre** (`http://localhost:5080/mcp`).
   Dağıtımda kanonik kaynak kimliğinin ne olacağı **karar verilmedi**; değer
   realm'in bastığı `aud` ile birebir aynı olmak zorunda ve kriter 7 bunu
   tutuyor.
3. **`ScopesSupported` `openid` ilan ediyor.** Realm'de yalnızca
   `bizigo-claims` client scope var; `openid profile email` canlıda
   `invalid_scope` alıyor (ölçüldü, `CLAUDE.md` §12). İstemciyi var olmayan bir
   scope istemeye göndermemek için dar tutuldu — ama `bizigo-mcp`'nin de burada
   ilan edilmesi gerekip gerekmediği **açık soru**.
4. **`stdio` tarafı bu ticket'ın dışında.** RFC 9728 bir HTTP mekanizması;
   stdio'da kimlik ortamdan okunuyor ([M08](../kimlik-tasima/index.md)).
5. **Tam birim paketi bu turda koşturulmadı.** Makinenin disk kapısı kırmızı
   yandı (6 GiB boş, taban 8 GiB) ve `CLAUDE.md` §3 ağır işe başlamayı
   yasaklıyor. Koşturulanlar: **derleme** (20 proje, 0 hata, 0 uyarı) ve
   **hedefli** paketler — `McpIdentityDiscoveryTests` 8/8,
   `KeycloakRealmTests` + yenileri 22/22. Sekiz kırmızının düzeltmesi
   **derlendi ama tam pakette doğrulanmadı**.
6. **Bu ağaç `873d108`'in üstünde**, M08/F5/M00/T55 birleşmiş main'in değil.
   Kod o değişikliklerin üstünde **denenmedi**.
7. **Aramadım:** `AccessScope.System`'in `tests/`+`tools/` altındaki
   çağıranları · BFF'in 401'i nasıl işlediği (`ui/src/lib/auth/`'a bakılmadı) ·
   collector token'ının kitlesi · SDK'nın `ResourceMetadataUri` ile
   `ResourceMetadata` arasındaki önceliği.
