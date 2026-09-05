---
title: "M03 — bizigo-sim araçları"
kind: ticket
status: 0
---

# M03 — Simülatörü çalışırken yönetmek

[MCP teknik plan §4](../../mcp-teknik-plan/index.md): FS'in ürettiği
simülatörler bugün **başlarken** yapılandırılıyor — profil, senaryo, filo. MCP
bunu **çalışırken** değiştirilebilir yapıyor.

Bu kol ürün verisine hiç dokunmuyor ve ayrı durmasının sebebi bu: M03'ün en
kötü hâli **yanlış bir simülatör durumu**, ürün kolunun en kötü hâli **log
verisinin kurumdan çıkması**. İki risk aynı ticket'ta olmamalı.

## 1 · Bugünkü hâl — ölçüldü

`sim/Bizigo.Simulators` içinde bugün duranlar: `Fleet.cs` · `Scenario.cs` ·
`SimulatorProfile.cs` · `SimulatorProfileStore.cs` · `SyslogEmitter.cs` ·
`SimulatedDeviceTransport.cs` · `WebhookDeliveryFactory.cs` · `WebhookSender.cs`.

Son ikisi **S07 ile bu hafta girdi**, yani `sim.webhook.emit`'in dayanağı hazır.

### Yüzey ayrımı kodda kurulu

`Scenario.cs` bir `ScenarioSurface` enum'ı ve
`ScenarioDefinition(string Name, ScenarioSurface Surface, string Claim)`
kaydı taşıyor. **Altı senaryo ölçüldü:**

`bozuk-kodlama` · `cihaz-yeniden-yazdi` · `kural-eklendi` · `saat-kaymasi` ·
`sidecar-yok` · `sir-dondu`

Yani M03 yüzey ayrımını **sıfırdan kurmuyor**; var olan ayrımı protokolde
görünür kılıyor.

> **Sayı karışıklığına dikkat:** plan **yedi araçtan** söz ediyor, senaryo
> sayısı **altı**. İkisi farklı şey; ticket'ın kabul kriteri araç sayısına
> bakıyor, senaryo sayısına değil.

## 2 · Kapsam

**İçinde** — planın §4'ündeki yedi araç:

| Araç | Ne yapıyor |
| --- | --- |
| `sim.fleet.list` | Filodaki cihazlar, grupları, o anki senaryoları |
| `sim.scenario.set` | Bir cihazın senaryosunu değiştirir |
| `sim.scenario.list` | Tanımlı senaryolar ve **hangi yüzeye** ait oldukları |
| `sim.syslog.burst` | Sayılı satır basar; hız ve profil parametreli |
| `sim.device.silence` | Bir cihazı susturur — sessizlik korelasyonunun tek gerçek sınavı |
| `sim.webhook.emit` | İmzalı değişiklik olayı gönderir (S07) |
| `sim.state` | Simülatörün o anki hâli: hangi cihaz hangi senaryoda, ne kadardır |

**Dışında**

- Ürün verisi döndüren hiçbir şey. Bu yüzeyin araçları `McpSurface`'te
  **`Simulator`** olarak beyan ediliyor; `Product` beyan eden bir `sim.*` aracı
  bir hata.
- FS'in kendi ticket'larının işi (yeni senaryo yazmak, yeni profil eklemek).

## 3 · Kabul kriterleri

1. Yedi araç ilan ediliyor ve her biri `McpSurface.Simulator` beyan ediyor.
2. **Yanlış yüzey hatası yüzeyi söylüyor.** `sim.scenario.set` bir `Config`
   senaryosunu syslog cihazına uygularsa hata *"bu senaryo başka bir yüzeye
   ait"* diyor — *"profilde yok"* **değil**. Aynı cümle MCP hata gövdesinde,
   ve hata **araç hatası** olarak (`isError`) dönüyor, protokol istisnası
   olarak değil.
3. Redaksiyon kapısının **neye baktığı yazılı**: plan §4'ün son paragrafı bunu
   ayırıyor — kapı `sim.syslog.burst`'ün **bastığı satırlara** değil,
   `sim.state`'in **döndürdüğü örneklere** bakıyor. Ayrım yazılmazsa kapı ya
   gereksiz yere her şeyi maskeler ya hiçbir şeyi.
4. Kapının kırmızı yanabildiği ölçüldü: yanlış yüzeye uygulanmış bir senaryo
   ile bir koşum yapılıp hata metninin **yüzeyi** adlandırdığı görüldü.

## 4 · Bitti tanımından karşıladıkları

- **§8** — *"Simülatör senaryosu yanlış yüzeye uygulandığında hata yüzeyi
  söylüyor, profili değil."* Tek sahibi M03.
- **§2**'yi besliyor: yedi aracın şeması ve örnek çağrısı M01'in sözleşme
  kapısından geçiyor. Kapıyı M01 kurdu; yükü **araç ekleyen** taşıyor.

## 5 · Bağımlılık ve sıra

**M01** (araç sözleşmesi ve keşif) **ve FS-a** (filo, senaryo motoru, profil
deposu). İkisi de bugün var: FS-a tarafı ölçüldü (§1), M01 tarafı birleşmemiş
dalda duruyor.

## 6 · Bilinen sınırlar ve açık sorular

1. **Bağlam maliyeti.** M01 ölçtü: araç başına **194 belirteç**, en pahalı
   kalem `description`. Yedi araç ≈ **1360 belirteç** ve bu **her bağlamda**
   taşınıyor. `bizigo` yüzeyiyle birlikte on beş araç ≈ 2900 belirteç.
   Açıklama uzunluğu bir bütçe kalemi.
2. **Açık soru:** `sim.*` araçları hangi süreçte koşuyor? Simülatör ayrı bir
   süreç; MCP sunucusu ona nasıl bağlanıyor — süreç içi mi, bir kontrol
   soketiyle mi? Plan **söylemiyor** ve ben de **aramadım**. Cevap
   `sim.state`'in canlılığını belirliyor: süreç içi değilse "o anki hâl" bir
   anlık görüntü olur.
3. **Açık soru:** `sim.syslog.burst` uzun sürebilen bir iş. Bitti tanımı §3
   iptali `bizigo` yüzeyindeki ClickHouse sorgusu üzerinden tarif ediyor;
   `burst`'ün iptali aynı mekanizmayı kullanacak mı? M01'e bağlı.
4. **Bu ticket ürün verisi görmüyor** — ama `sim.state`'in döndürdüğü örnek
   satırlar simülatörün ürettiği veri. Sentetik olması onları redaksiyondan
   **muaf yapmıyor**; kriter 3 bunun için var.
