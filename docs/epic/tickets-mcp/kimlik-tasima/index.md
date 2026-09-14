---
title: "M08 — Kimlik taşıma"
kind: ticket
status: 1
---

# M08 — Kimlik MCP oturumundan uca

[MCP teknik plan §6](../../mcp-teknik-plan/index.md): MCP istemcisi bir
kullanıcı adına konuşuyor; `bizigo` yüzeyi Keycloak'tan gelen kimliği
**taşımak** zorunda.

> **Servis hesabıyla koşan bir MCP sunucusu bütün kapsam kapılarını atlar.**

Bu cümle bu ticket'ın tek çivili kısıtı: hangi yol seçilirse seçilsin **servis
hesabı yasak**. *"Sunucu kendi servis hesabıyla bağlansın"* bir çözüm değil,
kapının kendisini kaldırmaktır.

## 1 · Bugünkü hâl — ölçüldü

### CLI'nin kimlik yolu **yok**, ve API'ye hiç çıkmıyor

`src/Bizigo.Cli` üzerinde arandı (main `a71df17`):

| Aranan | Sonuç |
| --- | --- |
| `HttpClient` | **yok** |
| `Authorization` · `Bearer` · Keycloak · oidc | **yok** |
| `"/v1…"` çağrısı | **yok** |
| `token` eşleşmeleri | hepsi `CancellationToken` parametresi — **yanlış pozitif**, ayıklandı |

CLI referansları: `Bizigo.Query` · `Bizigo.Ingest` · `Bizigo.Parsing` ·
`Bizigo.Alerting` · `Bizigo.Simulators`. Yani CLI **alan kütüphanelerine süreç
içi** gidiyor; API'nin kimlik katmanına hiç uğramıyor.

### Kapsam kimlikten nasıl doğuyor

`src/Bizigo.ControlPlane/AccessScopeResolver.cs` ölçüldü: doğrulanmamış
principal → `AccessScope.Denied`; `admin` rolü → `AccessScope.System(subject)`
(kaynağın kendi yorumu: *"`admin` kapsam filtresinden muaf — ama bu **BİLİNÇLİ
ve tek yerde**"*); diğerleri → gruplarından türetilmiş kapsam.

Yani **kapsamın doğduğu yer tek ve belli**. M08'in işi yeni bir kapsam yolu
açmak değil, MCP oturumunun principal'ını bu resolver'a **ulaştırmak**.

## 2 · Seçilen yol ve nasıl seçildiği

Üç aday vardı:

| # | Aday | Değerlendirme |
| --- | --- | --- |
| (a) | `stdio` yüzeyi `bizigo` araçlarını hiç sunmaz, yalnızca HTTP sunar | **Elendi** |
| (b) | `stdio` başlarken bir **cihaz akışıyla** oturum açar | **Önce seçildi, sonra çürütüldü** |
| (c) | `stdio` yalnızca `bizigo-sim` içindir | **Elendi** |
| (d) | `stdio` kimliği **ortamdan okur** | **Seçildi** |

**(a) ve (c)'nin elenmesi bir tercih değil bir ölçümün sonucu.** Ölçüm §1'de:
CLI'nin bugün kimlik yolu **yok**. Eğer olsaydı, stdio onu miras alırdı ve M08
*"var olanı MCP oturumuna taşı"* olurdu (`CLAUDE.md` §9: ikinci kopya yazma).
Olmadığı için ikisi de eleniyor — `bizigo` yüzeyini stdio'da işe yaramaz
kılıyorlar, oysa **stdio'nun var olma sebebi** yerel bir ajanın ürüne
konuşmasıdır.

### (b) neden çürütüldü — ve bu satırın burada durma sebebi

Bu belge önce **cihaz akışını** seçmişti: terminal süreci için standart olan
ve servis hesabı yasağıyla uyumlu görünen yol. **Yanlıştı.**

M08 uyduğumuz revizyonun yetkilendirme spesifikasyonunu okudu ve şunu
bildirdi: **spesifikasyon `stdio` taşıması için OAuth akışlarını dışlıyor** ve
kimliğin **ortamdan** okunmasını söylüyor. Yani (b) yalnızca gereksiz değil,
spesifikasyona **aykırı**.

> **Bu satırı silmek yerine tutuyorum.** Silseydim belge doğru görünürdü ama
> bir sonraki kişi cihaz akışını yeniden önerirdi — ve aynı eleme bir kez daha
> yapılırdı. Yanlış çıkmış bir karar, gerekçesiyle birlikte, kararın kendisi
> kadar bilgi taşıyor.
>
> **Kaynak ayrımı:** spesifikasyon iddiası **M08'in ölçümü**, bu belgenin
> değil — spesifikasyon metnini kendim okumadım. Yanlışlanırsa buraya
> dönülmeli.

Ayrıca §7.2'nin ölçümü aynı sonucu **ikinci bir yönden** destekliyor: realm'de
cihaz akışı için bir istemci kaydı **yok** — yalnızca `bizigo-ui` ve
`bizigo-collector` var. (b) seçilseydi bu ticket realm-as-code'a da dokunacak,
yani canlı Keycloak doğrulaması gerektirecekti.

### `stdio`'nun kendine özgü zorluğu — (d) bunu nasıl çözüyor

`stdio`'da **HTTP başlığı yok**, yani *"kimliği istekten al"* cümlesinin
karşılığı yok. (d) bunu ortamdan okuyarak çözüyor: token'ı süreç ortamı
taşıyor, MCP sunucusu onu üretmiyor.

Ama sorunun tamamı çözülmüş olmuyor ve kalan yarısı **açık**: token'ın
**süresi dolduğunda** ne oluyor? Ortamdan okunan bir değer kendini
tazeleyemez. Araç çağrısı reddediliyor mu, süreç yeniden mi başlatılıyor,
yoksa ortam değişkeni canlı mı okunuyor? **Karar M08'in.**

## 3 · Kapsam

**İçinde**

- Keycloak kimliğinin MCP oturumundan `bizigo` araçlarının çağırdığı uca
  taşınması.
- Kapsamın `AccessScopeResolver` üzerinden **türetilmesi** — kurulması değil.
- Token'ın yaşam döngüsü: alınması, tutulması, tazelenmesi, süresi dolduğunda
  davranış.

**Dışında**

- Araçların kendisi — [M04](../okuma-araclari/index.md).
- `bizigo-sim` yüzeyi: ürün verisi döndürmüyor, kimlik taşımıyor.

## 4 · Kabul kriterleri

1. `bizigo` yüzeyinin çağrıları **kullanıcının** kimliğiyle koşuyor; sunucunun
   servis hesabı **yok**.
2. Kapsam `AccessScopeResolver`'dan geliyor; yüzey `AccessScope.System`'i
   **kendi çağırmıyor** ([M04](../okuma-araclari/index.md) kriteri 2 ile aynı
   kapı — ikinci bir kapı yazılmıyor).
3. Kimliksiz bir MCP oturumu `bizigo` araçlarını çağıramıyor; ret **araç
   hatası** olarak (`isError`) dönüyor, protokol istisnası olarak değil
   (plan §2'nin hata ayrımı).
4. Token'ın süresi dolduğunda davranış **yazılı ve sınanmış**.
5. Kırmızı yanabildiği ölçüldü: servis hesabıyla koşan bir kurulumun
   reddedildiği görüldü.

## 4.1 · Neden hâlâ `1` — tek kriter, ölçüldü

Beş kriterin dördü karşılanmış; **kriter 4 karşılanmamış**: *"Token'ın süresi
dolduğunda davranış yazılı ve sınanmış."*

Arandı ve **yok**: `tests/Bizigo.UnitTests/Mcp*.cs` altında süre sonuna dair tek
bir iddia geçmiyor (`expired`, `exp`, `NotBefore`, `ValidateLifetime` — hiçbiri).
Yani davranış bugün SDK'nın varsayılanı ne yapıyorsa o, ve ne yaptığı **yazılı
değil**.

Bu M13'ün (stdio kimliği) kapsamıyla kesişiyor ve orada aynı soru bir kez daha
soruluyor: süre sonu hatası, kimliğin **yokluğundan** ayırt edilebiliyor mu? İkisi
aynı `unauthenticated`'e düşüyorsa okuyan kişi yanlış yere bakar. Cevap M13'te
ölçülecek ve buraya da yazılacak.

## 5 · Bitti tanımından karşıladıkları

Doğrudan bir madde **sahiplenmiyor** — bu yazılı olsun ki M08 bir maddeye
bağlı sanılmasın. **§7**'yi (kapsam tek kapıdan) taşıyor: kapsamın doğru
kullanıcıdan doğması o maddenin ön şartı, ama maddenin sahibi M04.

## 6 · Bağımlılık ve sıra

**M04.** Kimliğin taşınacağı bir uç olmadan taşıma sınanamaz. Plan §9 bunu
ayrıca yazıyor: akışlanabilir HTTP'nin oturum yönetimi **M01'in taşıma
oturumu** olarak duruyor ve BFF'in `redis-session`'ından bağımsız; ikisini
bağlamak M08'in cevabını önden vermek olurdu. *"Taşıma oturumu M01'in, kimlik
M08'in."*

## 7 · Bilinen sınırlar ve açık sorular

1. **Keycloak realm'i ölçülü ve M08 bunu varsaymalı:** realm'de yalnızca
   `bizigo-claims` client scope var. `scope=openid` geçiyor;
   `openid profile email` **canlıda `invalid_scope` alıyor**. Ölçüldü, plan §6
   de yazıyor.
2. **Realm'de iki istemci var — ölçüldü.** `deploy/keycloak/realm-bizigo.json`:
   **`bizigo-ui`** ve **`bizigo-collector`**. MCP için ya da cihaz akışı için
   bir istemci kaydı **yok**.

   Bu ölçüm önce *"M08 realm-as-code'a da dokunuyor"* diye okunmuştu — o okuma
   **(b) cihaz akışı** seçiliyken doğruydu. **(d) seçildikten sonra geçerli
   değil:** ortamdan okunan bir token için yeni bir istemci kaydı gerekmiyor,
   token'ı zaten var olan bir istemci basıyor. Yani M08 realm'e
   **dokunmayabilir** — ve dokunmuyorsa canlı Keycloak doğrulaması da
   gerekmez.

   **Hangi istemcinin bastığı açık soru:** ortamdaki token `bizigo-ui`'nin mi,
   yoksa ayrı bir istemcinin mi olmalı? Cevap *"ayrı"* ise realm yine
   değişiyor. **Ölçmedim.**

   Aynı dosyada ölçülen ikinci şey M09'un konusu: `bizigo-claims` client
   scope'unda bir **audience mapper** var
   (`included.client.audience: bizigo-api`) ve API `ValidateAudience = true`
   ile onu doğruluyor. Yani kitlenin bağlanması **bugün çalışıyor** — eksik
   olan, MCP sunucusunun **kendine ait bir kaynak kimliğinin** olmaması.
3. **Açık soru:** HTTP taşımasında kimlik `Authorization` başlığıyla mı
   geliyor, yoksa MCP oturum kurulumunda bir kez mi? İki taşıma **aynı araç
   kümesini** sunuyor (plan §2) ama kimlik yolları farklı olabilir; farklıysa
   bu bir karar olmalı, kaza değil.
4. **`admin` rolü tam kapsam alıyor** (§1) — yani bir admin kullanıcının MCP
   oturumu bütün grupların verisini modele açabilir. Bu resolver'ın bilinçli
   kararı ve M08 onu **değiştirmiyor**; ama MCP yüzeyinde sonucun daha keskin
   olduğu yazılı olmalı, çünkü veri bir ekrana değil **modelin bağlamına**
   gidiyor.
