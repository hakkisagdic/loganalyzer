---
title: "M04 — bizigo okuma araçları"
kind: ticket
status: 2
---

# M04 — Ürünün okunması, tek kapsam kapısından

[MCP teknik plan §5](../../mcp-teknik-plan/index.md)'in okuma yüzeyi:
`logs.*` · `alerts.*` · `inventory.*` · `catalog.*`. Planın §5'i bir uyarıyla
bitiyor ve bu ticket'ın asıl konusu o: **kapsam bu depoda bir sessiz-hata
mıknatısı ve MCP yeni bir kaçış yolu.**

## 1 · Bugünkü hâl — ölçüldü

### Uçların gerçek adresleri, ve planın tablosunun düzeltilmesi

Plan `logs.search`'ü *"Log arama"* diye yazıyor, ucu adlandırmıyor. Ölçüm:

| Araç | Bugünkü uç | Not |
| --- | --- | --- |
| `logs.search` | **`GET /v1/events`** | — |
| `logs.context` | `/v1/events` (pencere parametreleriyle) | ayrı uç **yok**, aramadım demiyorum: `/v1/*` sabitlerinin tamamına baktım |
| `alerts.list` · `alerts.get` | `/v1/alerts` | ayrıca `/v1/alerts/channels`, `/v1/alerts/triggers/{id}/close` |
| `inventory.list` | `/v1/sources` | — |
| `catalog.parsers` | `/v1/parsers` + `/v1/parsers/coverage` | — |

> **`/v1/logs` log arama ucu DEĞİL.** `LogsEndpoint.MapOtlpLogs` — OTLP
> **ingest** ucu, `RequireAuthorization(BizigoAuthPolicies.Ingest)` ile ve
> rolü yalnızca yazma. Plandan kopyalayıp *"`logs.search` → `/v1/logs`"*
> yazmak, aracı **yazma ucuna** bağlamak olurdu.

### Kapsam kapısı **derleyicide** — iyi haber

`src/Bizigo.Query/IScopedQuery.cs` her metodunda `AccessScope` istiyor;
dosyanın kendi cümlesi: *"Her metot `AccessScope` istiyor; kapsamsız çağrı
yazılamıyor."* Yani kapsam API katmanının bir alışkanlığı değil, arayüzün
şartı. Süreç içi koşan bir MCP aracı bile **kapsamsız sorgu yazamaz**.

### Kaçış deliği tek bir statik metot — kötü haber

`AccessScope.System(subject)` `IsUnrestricted = true` döndürüyor. Planın §5'i
*"bir aracın `owner_group` almayı unutması"* diyor; ölçülen şey daha kötü:
**unutmak gerekmiyor, çağırmak yetiyor**, ve çağrı derleme hatası vermiyor.

**Bugünkü çağıranları arandı ve bulundu — ürün kodunda iki tane:**

| Yer | Ne yapıyor |
| --- | --- |
| `src/Bizigo.Replay/ReplayEngine.cs:180` | `AccessScope.System("replay")` — sistem içi yazım |
| `src/Bizigo.ControlPlane/AccessScopeResolver.cs:131` | Kimlik doğrulanmış principal `admin` rolündeyse tam kapsam. Kaynağın kendi yorumu: *"`admin` kapsam filtresinden muaf — ama bu **BİLİNÇLİ ve tek yerde**."* |

Üç ayrı dosya da (`ChangeConnectorService`, `RcaScheduleWorker`,
`ChangeWebhookEndpoints`) yorumlarında **neden `AccessScope.System` olmadığını**
yazıyor — yani depoda bu kararı gerekçelendirme kültürü zaten var.

**Sonucu M04'ün kriterini keskinleştiriyor:** doğru yol kapsamı
`AccessScopeResolver`'dan **almak**; yanlış yol onu **kurmak**. Admin'in tam
kapsam alması bir kusur değil, resolver'ın bilinçli kararı — ve bir aracın
`AccessScope.System`'i kendi çağırması o kararı **atlamak** olur.

## 2 · Kapsam

**İçinde** — planın §5 tablosundan sekiz aracın okuma yarısı: `logs.search` ·
`logs.context` · `alerts.list` · `alerts.get` · `inventory.list` ·
`catalog.parsers`. Hepsi `McpSurface.Product` beyan ediyor.

**Dışında**

- RCA araçları — [M05](../rca-araclari/index.md).
- Redaksiyon kapısının **kendisi** — [M06](../redaksiyon-kapisi/index.md).
  M04 kapıyı kurmuyor ama **onsuz sevk edilmiyor** (plan §7).
- Kimliğin uca taşınması — [M08](../kimlik-tasima/index.md).

## 3 · Kabul kriterleri

1. Altı araç ilan ediliyor, hepsi `McpSurface.Product`.
2. **`bizigo` yüzeyinin derlemesi `AccessScope.System`'e hiç başvurmuyor.**
   Ölçülebilir bir kriter, tahmin değil: [T50](../../tickets-f3/kompozisyon-koku-bagi/index.md)'nin
   IL çağrı kapanışı bu sınıfı zaten yürüyor — `call`/`callvirt` operandları
   çözülüp o metoda başvuru aranıyor. Kapsam `AccessScopeResolver`'dan
   **alınıyor**.
3. Kapsam filtresi **tek kapıdan** geçiyor: araçlar `IScopedQuery` üzerinden
   sorguluyor, ikinci bir sorgu yolu açmıyor.
4. **İmleç sözleşmesi sınanıyor** — §6.1.
5. `notifications/cancelled` uzun bir ClickHouse sorgusunu **gerçekten** iptal
   ediyor ve bu **ölçülüyor**. Bitti tanımı §3'ün mekanizması M01'in, ama uzun
   sorgu ilk M04'te doğuyor: kanıt burada üretiliyor.
6. Kapıların kırmızı yanabildiği ölçüldü (`CLAUDE.md` §6): en az
   `AccessScope.System` kriteri için bir kusur uygulanıp geri alındı.

## 4 · Bitti tanımından karşıladıkları

- **§7** — *"Kapsam filtresi MCP yüzeyinde de tek kapıdan geçiyor."*
- **§3** — iptalin **kanıtı** (mekanizma M01'in).
- **§2**'yi besliyor: altı aracın şeması ve örnek çağrısı M01'in sözleşme
  kapısından geçiyor. Kapıyı M01 kurdu; yükü **araç ekleyen** taşıyor.

## 5 · Bağımlılık ve sıra

**M01** (sözleşme, keşif, iptal) **ve M02** (komut çekirdeği). M02 önce, çünkü
araçlar çekirdeğin üstüne biniyor; çekirdek sonradan çıkarılırsa altı araç iki
kez yazılır.

**M06 sonra ama sevk şartı:** araçlar yazılır, kapı takılır, ikisi birlikte
açılır. Kapıyı önce yazmak tüketicisi olmayan bir tip yazmak olurdu
(`CLAUDE.md` §8); sonra açmak arada bir sürüm boyunca kapısız yüzey bırakmak.

## 6 · Bilinen sınırlar ve açık sorular

### 6.1 · İmleç — F1'in dersi bir MCP aracında daha pahalı

Plan sayfalama sözleşmesini **söylemiyor**, ve bu ticket'ın en riskli boşluğu.
F1'de ölçülen kusur şuydu: **yanıttaki imlecin adı istekten farklıydı**, ekran
aldığı imleci geri gönderemiyordu ve **yarım imleç sessizce ilk sayfayı
tekrarlıyordu**. Hata yok, sayaç yok, belirti yok.

Aynı kusur bir MCP aracında **modele sonsuz aynı sayfayı** verir ve model bunu
fark etmez — ilerlediğini sanarak aynı veriyi özetler. M04'ün ilk yazacağı
şeylerden biri bu sözleşme olmalı: imlecin adı istek ile yanıtta **aynı**, ve
bir testte iki ardışık sayfanın **farklı** olduğu görülüyor.

### 6.2 · Açık sorular

1. **`logs.context` ayrı bir uç mu, `logs.search`'ün parametresi mi?** Bugün
   ayrı uç yok. Karar M04'ün.
2. **`AccessScope.System`'i kim daha çağırıyor?** Ürün kodunda iki çağıran
   ölçüldü (§1). `tests/` ve `tools/` altındakilere **bakmadım** — kriter 2
   yalnızca `bizigo` yüzeyinin derlemesine baktığı için bugün gerekmedi, ama
   kriterin şeklini değiştirebilecek bir şey çıkarsa oradan çıkar.
3. **Çekirdeğin imzası kapsamı taşıyor mu?** [M02](../komut-cekirdegi/index.md)
   §7.4'ün açık sorusu burada patlıyor: bugünkü altı CLI komutunun hiçbiri
   kapsamlı sorgu yapmıyor, yani soru M02'de **görünmüyor**.

### 6.3 · Bağlam maliyeti

M01 ölçtü: araç başına **194 belirteç**, en pahalı kalem `description`. Altı
araç ≈ 1160 belirteç, **her bağlamda** taşınıyor. On beş araçlık tam yüzey
≈ 2900 belirteç — planın §9'unda *"ölçülmedi"* diye duran kalem artık ölçülü.
