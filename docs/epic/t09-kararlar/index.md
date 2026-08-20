---
kind: spec
title: "T09 — kimlik ve yetkilendirmede alınan kararlar"
---

# T09 — kimlik ve yetkilendirmede alınan kararlar

> Bu belge geriye dönük yazıldı: kaynağı kod, commit geçmişi ve F1 kapanışı.
> Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan gerekçeler
> kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen alternatifler
> kayıtta yok.

Ticket: [T09 — Kimlik: Keycloak, OIDC, servis hesapları](../tickets/kimlik/index.md) ·
Yöneten kararlar: [K17, K18, K26](../mimari-kararlar/index.md), sonradan **K31**

## 1 · Ticket ne yaptı

T02'nin kurduğu `IScopedQuery` kapısına gerçek kimlik bağlandı: Keycloak
realm-as-code, dört claim'lik bir sözleşme, JWT Bearer doğrulaması, IdP grubundan
`owner_group`'a çevrim, collector için `client_credentials` servis hesabı ve
denetim kaydı.

**Ticket'ın bir maddesi bugün kodda yok.** Madde 4 BFF'i API içinde
tanımlıyordu: `AddOpenIdConnect` + cookie + YARP ters vekil. F2'de **K31** o
akışı Next.js tarafına taşıdı; `AuthenticationSetup` bugün tek şema tanıyor ve
kendi yorumunda "API saf kaynak sunucusu (K31)" yazıyor. Aşağıdaki kararların
hangisinin T09'da, hangisinin K31'de alındığını **koddan ayırt edemediğim**
yerlerde bunu belirtiyorum.

## 2 · Koddan okunan kararlar

### 2.1 Dört claim — ve dar tutulmasının bedeli ödendi

`BizigoClaims`: `sub`, `preferred_username`, `roles`, `groups`. Sınıf yorumu
gerekçeyi yazıyor: sözleşme **bilinçli olarak dar**, çünkü Keycloak → Entra ID
geçişi IdP tarafında mapper ayarı ve `idp_group_mapping` satırları demek —
kodda değişiklik yok. "Buraya beşinci bir claim eklemek o özelliği kaybetmek
olur."

Bunun realm tarafındaki karşılığı `bizigo-claims` adında **tek bir client
scope**: dört claim'in dördü de orada, artı `aud`.
`Paylasilan_scope_bes_claimi_de_uretiyor` testinin yorumu sebebi yazıyor —
`sub` ve `preferred_username` normalde Keycloak'ın `basic`/`profile`
scope'larından gelir, **onlar bu realm'de oluşmadığı için elle yazılmak
zorunda**. Eksik olsalardı yetkilendirme çalışmaya devam ederdi (roller yerinde)
ama denetim kimliği kaybolurdu.

### 2.2 `MapInboundClaims = false` — sözleşmenin çalışmasının tek sebebi

Bu tek satırın gerekçesi kodda **ölçülmüş hâliyle** duruyor: varsayılan `true`
iken JWT işleyicisi gelen claim'leri Microsoft'un uzun URI şemasına çeviriyor,
`roles` görünmez oluyor ve `RoleClaimType`/`NameClaimType` ayarları hiçbir şeye
denk gelmiyor.

Ölçülen belirti: collector'ın `roles: ["ingest"]` taşıyan token'ıyla `/v1/logs`
**403** dönüyordu ve `/auth/me` `roles: []` gösteriyordu. Yorum ayrıca
belirtinin neden yanıltıcı olduğunu da yazıyor — `sub` yine bulunuyordu, çünkü
`AccessScopeResolver` onu `ClaimTypes.NameIdentifier` yedeğinden okuyor.
Bugün `Jwt_isleyicisi_claim_adlarini_ceviremiyor` bu satırı sabitliyor.

### 2.3 Grup claim'i doğrudan `owner_group` değil

| Karar | Koddan okunan gerekçe |
| --- | --- |
| Çevrim `idp_group_mapping` tablosundan | `AccessScopeResolver` yorumu: claim doğrudan `owner_group` sayılsaydı bir ekibin veri kapsamını değiştirmek için **IdP'ye dokunmak** gerekirdi |
| `full.path=true` (tam yol) | `Grup_claimi_tam_yol_veriyor` yorumu: kapatılırsa `network/core` ile `platform/core` ayırt edilemez ve kapsam kapısı **yanlış grubu** eşler |
| Baştaki `/` girişte normalize ediliyor | Keycloak Group Membership mapper tam yolu `/network/core` diye basıyor; tam yol açık bırakıldı, `Normalize()` `TrimStart('/')` yapıyor |
| IdP grup adı **kültür duyarsız**, `owner_group` **ordinal** | `GroupMapping.From` yorumu: IdP grup adları elle giriliyor, büyük/küçük harf farkı eşlemeyi bozmamalı. Karşılığı olan `owner_group` ordinal kalıyor |
| Eşleşmeyen grup sessizce atlanıyor, kapsam **boş** kalabiliyor | Yorum tam olarak şunu yazıyor: boş kapsam "her şey" değil "hiçbir şey" demek — eşleme eksikse kullanıcı **veri göremez, yanlış veri görmez** |
| `admin` kapsam filtresinden muaf | Yorumu "BİLİNÇLİ ve tek yerde" diyor. Muafiyetin neden `admin`'e verildiğinin gerekçesi kayıtta yok |

**`GroupMapping` veritabanından ayrı bir saf tip.** Gerekçe sınıf yorumunda:
çevrim mantığı bu üründeki en pahalı hata sınıfının (K17) tam ortasında ve bir
EF sağlayıcısı kurmadan sınanabilmesi gerekiyor. Ticket bunu "12 test bu yüzden
bağımlılıksız koşuyor" diye kaydetmiş; bugün `ClaimContractTests` yine 12 test.

`AccessScopeResolver` eşlemeyi belleğe alıyor ve `Interlocked.Exchange` /
`Volatile.Read` ile değiştiriyor. Bellekte tutmanın gerekçesi yazılı (tablo
onlarca satır, sorgu trafiği çok daha yoğun); **tazelemeyi kimin tetiklediği**
bu belgede incelenmedi.

### 2.4 Kimlik kapalıyken de kapsam kapalı

`Auth:Enabled=false` yalnızca kimlik doğrulamayı atlıyor; `AccessScope` yine
`Denied` başlıyor. Koddaki yorum gerekçeyi yazıyor: "kimlik yoksa her şeyi gör"
varsayılanı bu üründe yapılabilecek en pahalı hata olurdu (K17).

Bu dalda K31 sonrası ikinci bir karar var: boşluğu dolduran cookie işleyicisi
**kaldırıldı**, yerine hiçbir zaman kimlik üretmeyen ve 401 dönen
`AnonymousAuthenticationHandler` kondu. Gerekçe yazılı — üründe artık hiçbir
yerde cookie tabanlı kimlik yok ve "yerel geliştirmede duran" bir cookie
işleyicisi o kuralı **sessizce deler**. Teknik zorunluluk da yazılı: varsayılan
şema kayıtlı olmazsa yetki isteyen bir uç `500` verir.

Ticket'ın kendi ifadesiyle varsayılan şemanın Bearer olmasının sebebi:
kimliksiz bir API isteği 401 alıyor, giriş sayfasına **yönlendirilmiyor** —
tersi olsaydı collector 302 alırdı.

### 2.5 Roller ve servis hesabı

Dört politika (`ingest`, `read`, `author`, `admin`) rol kümelerine bağlı.
`BizigoRoles.Ingest`'in özet yorumu tek cümle: "Yalnızca `/v1/logs`. Okuma
yetkisi **yok**." Ticket gerekçeyi tamamlıyor: collector kimliği sızarsa veri
**yazılabilir, okunamaz**.

Realm dosyası bunu ayrı bir client'la kuruyor: `bizigo-collector` yalnızca
servis hesabı (`standardFlowEnabled=false`), `bizigo-ui` yalnızca tarayıcı akışı
(`serviceAccountsEnabled=false`).

### 2.6 Denetim kaydı

`IAuditSink` → `ControlPlaneAuditSink` kontrol düzlemine yazıyor (K23);
`NullAuditSink` testler ve CLI için. Kayıt kim (`Subject`), ne (`Action`,
`Resource`), **hangi kapsamla** (`Scope`), kaç satır ve ne kadar sürdü
(`RowCount`, `DurationMs`, `Succeeded`) tutuyor. `Scope` alanının özet yorumu
niyeti yazıyor: sonradan "bu kişi neyi görebiliyordu" sorusuna cevap.

Alanlar yazmadan önce kırpılıyor (`Truncate`). Kırpmanın — hata vermek yerine —
seçilme gerekçesi kayıtta yok.

## 3 · Bugün ayakta duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `ClaimContractTests` (12) | Claim → kapsam çevriminin tamamı: kimliksiz istek hiçbir şey göremiyor, baştaki eğik çizgi eşlemeyi bozmuyor, iç içe gruplar ayırt ediliyor, eşleşmeyen grup kapsam üretmiyor, `admin` muaf / `analyst` değil, `ingest` hiçbir veri göremiyor |
| `ClaimMappingTests` | `MapInboundClaims=false` (§2.2), issuer/audience doğrulamasının açık olması, ve **API'nin tarayıcı oturumu şeması taşımaması** — `Auth:Enabled` iki değeriyle de |
| `KeycloakRealmTests` (11) | Realm dosyasının kendisi: istemciler yalnızca var olan scope'lara referans veriyor, paylaşılan scope beş claim'i de üretiyor, grup claim'i tam yol veriyor, collector yalnızca `ingest` rolü taşıyor, compose issuer'ı sabitliyor, kaldırılmış API dönüş adresi realm'de yok |
| `ArchitectureTests.Uretim_DI_grafi_kapsam_dogrulamasindan_geciyor` | Kimlik kayıtları dahil bütün üretim servis grafiği (bkz. §4.1) |

**Bu bekçilerin kırmızı yanabildiğini bu turda ölçmedim.** Belge geriye dönük;
kod okundu, ölçüm yapılmadı.

## 4 · Açıkta kalanlar

### 4.1 Kimlik grafiği kapsam doğrulamasından hiç geçmiyordu — kapandı

`ArchitectureTests`'in kapsam (captive dependency) bekçisi denetlediği kayıt
uzantılarını **elle yazılmış bir listeden** alıyordu ve `AddBizigoAuthentication`
o listede **yoktu**. `Program.cs` onu çağırıyor, yani üretim grafiğinin parçası;
ama doğrulamaya hiç girmiyordu. Aynı boşlukta `AddBizigoDiscovery` de vardı.

Bu, bu deponun adını koyduğu hata sınıfının tam örneği: bekçi **yeşil
yanıyordu** ve yeşilliği kimlik katmanı hakkında hiçbir şey söylemiyordu.

**Kapandı** — `5dcb786` "Let the lifetime guard find its own layers". Keşif artık
kompozisyon kökünden (`Bizigo.Api`) başlayıp `Bizigo.*` referanslarını geçişli
yükleyerek bütün `Add*` uzantılarını yansımayla buluyor; elle yazılan liste
**denetlenen küme** olmaktan çıkıp **beklenen küme** oldu
(`Kapsam_bekcisi_butun_kayit_uzantilarini_kendisi_buluyor`). Keşif azını bulursa
bir katman doğrulama dışında kalmış demek, fazlasını bulursa yeni katman gelmiş
ve bilinçli yazılması gerekiyor.

### 4.2 Uçtan uca giriş akışı T09'da denenmedi — ve içinde bir kusur vardı

Ticket açıkça yazmış: "Gerçek Keycloak'a karşı uçtan uca giriş akışı
**denenmedi**." Bunun bedeli F2'de görüldü ve
[F2 kapanışı](../f2-kapanis/index.md)'nda ölçülü olarak duruyor: realm
`scopes_supported = ["openid", "offline_access", "bizigo-claims"]` diyor,
`openid profile email` **`invalid_scope`** alıyor — ve F1'den kalma OIDC
işleyicisi tam o üçlüyü istiyordu. Tarayıcı akışı sevk edilseydi **ilk
kullanıcının ilk girişinde** patlardı ve hiçbir test bunu görmezdi, çünkü akış
hiç koşulmamıştı.

Yani T09'un "denenmedi" satırı bir formalite değildi; içinde çalışmayan bir akış
vardı. K31 ile akış Next tarafına taşındığı için kusur ürüne çıkmadı.

### 4.3 Realm'in kendi varsayılan scope listesi tanımsız scope'lara işaret ediyor

`realm-bizigo.json` içinde `defaultDefaultClientScopes` şunu diyor:
`["profile", "email", "roles", "bizigo-claims"]`. Realm'in `clientScopes`
listesinde ise **yalnızca `bizigo-claims` var** — `profile`, `email` ve `roles`
tanımlı değil.

`Istemciler_yalnizca_var_olan_scopelara_referans_veriyor` bu durumu
yakalamıyor, çünkü **istemci düzeyindeki** listeleri denetliyor; iki istemcinin
de `defaultClientScopes` değeri sadece `bizigo-claims`. Realm düzeyindeki liste
bekçinin görüş alanının dışında.

Ölçülmüş sonuç §4.2'deki `invalid_scope`. Bu iki gözlem arasındaki nedensel
bağı **doğrulamadım** — yalnızca ikisinin de doğru olduğunu okudum.

### 4.4 Gerekçesi kayıtta olmayanlar

| Kalem | Not |
| --- | --- |
| Muafiyetin neden `admin` rolüne verildiği | Kararın bilinçli olduğu yazılı, sebebi değil |
| Denetim alanlarının hata yerine **kırpılması** | `Truncate` var, gerekçesi yok |
| `AccessScopeResolver.RefreshAsync`'i kimin/ne zaman çağırdığı | Bu belgede incelenmedi |
| Ticket madde 4'ün (YARP + cookie/OIDC) hangi kararla düştüğü | K31 olduğu koddan okunuyor, ama K31'in T09'un hangi bulgusundan doğduğu kayıtta yok |
