---
title: "M08 — Kimlik taşıma"
kind: ticket
status: 0
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
| (b) | `stdio` başlarken bir cihaz akışıyla oturum açar | **Seçildi (ön koşullu)** |
| (c) | `stdio` yalnızca `bizigo-sim` içindir | **Elendi** |

**Eleme bir tercih değil bir ölçümün sonucu.** Ölçüm §1'de: CLI'nin bugün
kimlik yolu **yok**. Eğer olsaydı, stdio onu miras alırdı ve M08 *"var olanı
MCP oturumuna taşı"* olurdu (`CLAUDE.md` §9: ikinci kopya yazma). Olmadığı
için (a) ve (c) eleniyor — ikisi de `bizigo` yüzeyini stdio'da işe yaramaz
kılıyor, oysa **stdio'nun var olma sebebi** yerel bir ajanın ürüne
konuşmasıdır.

Geriye cihaz akışı kalıyor: terminal süreci için standart olan ve **servis
hesabı yasağıyla uyumlu tek yol**, çünkü token kullanıcının olmak zorunda.

> **Ön koşul, ve bu belgede yazılı olması bunun için:** bir sonraki kişi
> *"neden cihaz akışı"* diye sorduğunda cevabı burada bulmalı. Cevap
> *"seçtik"* değil, *"CLI'nin bugünkü kimlik yolu ölçüldü ve yoktu."*
> Ölçüm bugün geçerli; CLI bir kimlik yolu kazanırsa **bu karar yeniden
> değerlendirilmeli** ve (b) gereksiz hâle gelebilir.

### `stdio`'nun kendine özgü zorluğu

`stdio`'da **HTTP başlığı yok**. Yani *"kimliği istekten al"* cümlesinin
karşılığı yok; token bir yerde **tutulmak** zorunda ve tazelenmesi gerekiyor.
Süresi dolduğunda ne olduğu bir tasarım kararı: araç çağrısı reddediliyor mu,
akış yeniden mi başlıyor?

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
2. **Açık soru:** cihaz akışının istemci kaydı realm'de var mı? **Bakmadım.**
   Yoksa M08 realm-as-code tarafına da dokunuyor demektir ve bu ticket'ın
   kapsamı büyür.
3. **Açık soru:** HTTP taşımasında kimlik `Authorization` başlığıyla mı
   geliyor, yoksa MCP oturum kurulumunda bir kez mi? İki taşıma **aynı araç
   kümesini** sunuyor (plan §2) ama kimlik yolları farklı olabilir; farklıysa
   bu bir karar olmalı, kaza değil.
4. **`admin` rolü tam kapsam alıyor** (§1) — yani bir admin kullanıcının MCP
   oturumu bütün grupların verisini modele açabilir. Bu resolver'ın bilinçli
   kararı ve M08 onu **değiştirmiyor**; ama MCP yüzeyinde sonucun daha keskin
   olduğu yazılı olmalı, çünkü veri bir ekrana değil **modelin bağlamına**
   gidiyor.
