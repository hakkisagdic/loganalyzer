---
title: "M03 — bizigo-sim araçları"
kind: ticket
status: 1
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
kaydı taşıyor. **Yedi senaryo ölçüldü** (`Scenarios.All`):

| Senaryo | Yüzey |
| --- | --- |
| `kural-eklendi` | `Config` |
| `sir-dondu` | `Config` |
| `cihaz-yeniden-yazdi` | `Config` |
| `gurultu` | `Config` |
| `saat-kaymasi` | `Syslog` |
| `bozuk-kodlama` | `Syslog` |
| `sidecar-yok` | `Infrastructure` |

Yani M03 yüzey ayrımını **sıfırdan kurmuyor**; var olan ayrımı protokolde
görünür kılıyor.

> **Düzeltme (M03 uygulaması sırasında ölçüldü).** Bu bölüm önce *"altı senaryo"*
> diyordu ve `gurultu`'yu (`Scenario.cs:154`, `Config` yüzeyi — *"ConfigNormalizer
> gürültüyü eliyor; fark BOŞ çıkıyor"*) saymıyordu. Kodda **yedi** var.
> Sayı burada bilinçli bir kalemdi (aşağıdaki kutu ona dayanıyor), o yüzden
> düzeltme kaydıyla birlikte duruyor.

> **Sayı karışıklığına dikkat:** plan **yedi araçtan** söz ediyor, senaryo sayısı
> da **yedi** — ama ikisi **farklı şeyler** ve eşitlik tesadüf. Ticket'ın kabul
> kriteri araç sayısına bakıyor, senaryo sayısına değil.

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

## 6.1 · Uygulamada ölçülenler — yukarıdaki üç sorunun cevabı

Bu bölüm uygulama turunda **ölçülerek** yazıldı. Yukarıdaki tahminler
silinmedi: neyin sorulduğu ile neyin çıktığı arasındaki fark bu belgenin en
pahalı bilgisi.

### 2. sorunun cevabı — ayrı süreç yok, ve bu ticket'ı yeniden tanımladı

Simülatör **tek atımlık bir CLI**: profili okuyor, basıyor, çıkıyor. Bağlanacak
uzun ömürlü bir süreç **yok**, dolayısıyla `sim.scenario.set`, `sim.device.silence`
ve `sim.state`'in altında hiçbir şey yoktu. **M03'ün gerçek işi üç araç değil, o
katman.**

Katman **stdio-only + dosya**: `artifacts/bizigo-sim/state.json`, kilit altında
oku-değiştir-yaz + atomik `rename`, `schema_version`. Bellek içi durum elendi
çünkü stdio'da **her bağlantı kendi süreci** — iki istemci farklı durum görür ve
`sim.state` ile `sim.device.silence` **yalan söylerdi**. Daemon elendi çünkü §3
kaçak proses konusunda net.

**`sim.state`'in vaadi bu yüzden daraldı.** Planın §4'ü onu *"simülatörün o anki
hâli"* diye tarif ediyor; doğru cevap **niyet**, gerçek değil. Niyet ile etki
**ayrı alanlar** (`scenario_set_at` ↔ `last_applied_at`) ve ayrıştıkları
`divergences` içinde adıyla raporlanıyor.

### 3. sorunun cevabı — iptal M01'in mekanizmasıyla, ayrı bir şey gerekmedi

`SyslogEmitter.EmitAsync` zaten `CancellationToken` alıyor ve basım döngüsünün
içine taşıyor. `sim.syslog.burst` belirteci olduğu gibi geçiriyor; M01'in
*"her araç iptal edilmiş belirteci gözetiyor"* kapısı bunu ölçüyor.

### 4. sorunun cevabı — bu yüzeyde redaksiyonun öznesi YOK

**Hiçbir `sim.*` aracı cihaz metni döndürmüyor.** `sim.syslog.burst` sayaç
döndürüyor (satır, bayt, süre); `sim.state` niyet/etki damgaları döndürüyor,
örnek satır **değil**. Yani kriter 3'ün istediği sınır şu: kapının bu yüzeyde
bakacağı bir şey bugün yok, ve bu bir eksik değil bir **tasarım sınırı**.
Bekçisi `SimulatorMcpToolTests.Hicbir_sim_araci_cihaz_metni_dondurmuyor` —
yazının bir gün eskimesine karşı.

Sınırın nerede biteceği de yazılı (`SyslogBurstTool` belgesi): bir araç örnek
satır döndürmeye başlarsa o metin **sentetik olduğu için muaf değil**, ve o gün
tek yol `McpLogText` menteşesi olacak.

### 1. maddenin cevabı — bağlam maliyeti tahminin İKİ KATI

§6.1'in üstündeki 1. madde araç başına **194 belirteçten** yola çıkıp yedi araç
için *≈1360* diyordu. Ölçüldü (`McpSchemaBudgetTests`, `o200k_base`):

| Yüzey | `tools/list` toplam | Araç |
| --- | --- | --- |
| `bizigo` | **198** belirteç | 1 |
| `bizigo-sim` | **2977** belirteç (10 259 karakter) | 8 |

Araç başına: `sim.webhook.emit` 537 · `sim.state` 424 · `sim.syslog.burst` 424 ·
`sim.fleet.list` 382 · `sim.scenario.set` 355 · `sim.scenario.list` 330 ·
`sim.device.silence` 327 · `server.info` 194.

**Tahminin neden yanlış olduğu ölçülebilir bir şey:** taban olarak alınan
`server.info` argümansız ve tek alanlı. Gerçek bir aracın `inputSchema`'sı,
`outputSchema`'sı ve alan açıklamaları var; maliyeti belirleyen şey şemanın
varlığı değil **alan sayısı**. On beş araçlık iki yüzey için plandaki *≈2900*
sayısı da bu yüzden düşük — yalnız simülatör kolu neredeyse o kadar.

### Ayrıca ölçülen iki şey — ikisi de kapı kusuru

- **Uyum kapısı her iki yüzeyi de `Bizigo.Api` kökünden denetliyordu**, ama
  `bizigo-sim` üretimde `Bizigo.Cli` kökünden koşuyor ve `Bizigo.Api`'nin
  `Bizigo.Simulators`'a **hiç referansı yok**. Yani yedi araç kapıya
  görünmüyordu ve kapı `bizigo-sim` için yine tek araç sayıp **yeşil kalıyordu**.
  Kök artık yüzeye göre seçiliyor.
- **`Bizigo.Cli` → `Bizigo.Simulators` bağı bugün kazaen ayakta.** `bizigo.dll`
  ölçüldü: `Bizigo.Simulators` **var**, `Bizigo.Query` **yok** (budanmış — tuzağın
  depoda canlı örneği). Simülatör ayakta çünkü `FleetCommandHandlers`
  `bizigo fleet apply` için `FleetStore`'a dokunuyor. `McpCommandHandlers` artık
  `AddBizigoSimulatorTools`'u **MCP yolundan** çağırıyor; bağ tesadüften çıktı.
  ⚠ **Geçici**: M05'in açık derleme listesi geldiğinde doğru cevap bir çağrı yan
  etkisi değil, `SimulatorMcpSetup.ToolAssembly`'nin o listeye verilmesi olacak.
