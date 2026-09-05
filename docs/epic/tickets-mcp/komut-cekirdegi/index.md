---
title: "M02 — Komut çekirdeği ve CLI paritesi"
kind: ticket
status: 0
---

# M02 — Tek çekirdek, iki sunum

[MCP teknik plan §3](../../mcp-teknik-plan/index.md) tek cümleyle çiviliyor:
her MCP aracının bir CLI karşılığı olacak, ama **iki uygulama yazılmayacak** —
ortak bir komut çekirdeği, iki sunum katmanı.

Gerekçe bu deponun beş kez ödediği ders: iki liste sessizce ayrışıyor. Bir araç
MCP'de var CLI'da yok ise fark **bir karar** olmalı, kaza değil.

## 1 · Bugünkü hâl — ölçüldü

`src/Bizigo.Cli/Program.cs` üzerinde sayıldı (main `a71df17`): **6 komut grubu,
10 yaprak komut**, `SetAction` çağrısı **10**.

| Grup | Yapraklar |
| --- | --- |
| `parser` | `lint` · `test` · `try` · `coverage` |
| `schema` | `migrate` |
| `fleet` | `apply` |
| `seed` | `golden` |
| `fields` | `coverage` · `values` |
| `sigma` | `sync` |

**On birinci komut bu ağaçta yok.** `mcp serve` M01 ile geliyor ve M01 bu
worktree'ye (main `a71df17`) **birleşmedi**: `src/Bizigo.Mcp` dizini yok,
`"mcp"`/`"serve"` dizgileri CLI'da geçmiyor. Ölçüm ikisinde ayrışıyor ve
ayrışma sayının kendisinde — aşağıdaki sabit bu yüzden **koşullu**.

### CLI bugün API'ye konuşmuyor

Ayrıca ölçüldü, çünkü çekirdeğin şeklini belirliyor: `Bizigo.Cli` içinde
**`HttpClient` yok**, `Authorization`/`Bearer`/Keycloak/oidc izi yok, `"/v1…"`
çağrısı yok. (`token` eşleşmelerinin tamamı `CancellationToken` parametresi —
ayıklandı.) CLI referansları `Bizigo.Query` · `Bizigo.Ingest` ·
`Bizigo.Parsing` · `Bizigo.Alerting` · `Bizigo.Simulators`: **alan
kütüphanelerine süreç içi** gidiyor.

Yani çekirdek bugünkü komutlardan türetilirse **kimlik taşımayan** bir şekli
miras alır. [M08](../kimlik-tasima/index.md) bu ölçümün üstüne kuruluyor.

## 2 · Çekirdek nerede yaşıyor — **`Bizigo.Commands`, yeni proje**

Karar koordinatörde verildi. İki gerekçesi var ve ikisi de ölçüme dayanıyor.

**Birincisi T50'nin ölçtüğü şey:** `Bizigo.Cli`'nin derleme adı `Bizigo.Cli`
değil **`bizigo`** (`<AssemblyName>` ile yeniden adlandırılmış). Çekirdeği
CLI'nin içine koymak `Bizigo.Mcp → bizigo` referansını doğurur — protokol
katmanı bir **sunum** katmanına bağlanmış olur, üstelik `Bizigo.` önek
konvansiyonunun dışında kalan ve bu yüzden yansımayla keşfeden bekçilere
görünmeyen bir derlemeye.

**İkincisi plandan:** *"ortak çekirdek, **iki** sunum katmanı."* Çekirdek
sunum katmanlarından birinin içinde yaşarsa o katman ayrıcalıklı olur ve
parite iddiası tek yönlü hâle gelir — CLI'de olan MCP'ye taşınır, tersi
taşınmaz.

## 3 · Çivilenen küme

Koordinatör onayladı; **öneri değil karar**.

**Araç olacak altı komut:** `parser lint` · `parser test` · `parser try` ·
`parser coverage` · `fields coverage` · `fields values` — altısı da **yan
etkisiz**.

**Gerekçeli muafiyet, beş kalem:**

| Komut | Gerekçe |
| --- | --- |
| `schema migrate` | Göç **geri alınamaz**; bu deponun en pahalı önlenmiş hatası bir göçtü |
| `seed golden` | Ölçüm verisinin kirlenmesi **sessiz** — yanlış sayı üretir, hata vermez |
| `fleet apply` | [M03](../sim-araclari/index.md)'ün yüzeyi; `bizigo` yüzeyine ait değil |
| `mcp serve` | Özyineleme — sunucuyu sunucunun aracı olarak sunmak |
| `sigma sync` | Alarm kurallarına **yazıyor** |

**Sabit sayı: `ExpectedExemptCount = 5`.** `Exempt` kalıbı: muafiyet eklemek
iki ayrı bilinçli hareket istiyor.

### `sigma.plan` — komutu olmayan ilk araç

`sigma sync` **bölünmüyor**; zaten ayrı duran saf yarısı **ilan ediliyor**.
`Plan` bugün veritabanına dokunmuyor ve değer döndürüyor, yani araç olmaya
uygun; `sync`'in yazan yarısı muafiyette kalıyor.

Bunun bir sonucu var ve yazılı olmalı: **kapı tek yönlü.** *"Her komut ya araç
ya muafiyet"* sınanıyor; *"her aracın bir komutu var"* **sınanmıyor**.
`sigma.plan` bu asimetrinin ilk örneği ve planın istediği türden bir **karar**
— kaza değil. Kapıyı çift yönlü yapmak, saf bir yarıyı ilan etmeyi imkânsız
kılardı.

## 4 · Kabul kriterleri

1. `Bizigo.Commands` projesi var; `Bizigo.Cli` ve MCP yüzeyi **onu** kullanıyor,
   birbirini değil.
2. Çekirdekteki her komut ya bir MCP aracı ya `Exempt` listesinde **gerekçesiyle**;
   sayı `ExpectedExemptCount = 5` ile çivili.
3. Muafiyet listesi ile araç listesi **ayrık**, ve kapının kümeyi **yansımayla**
   bulduğu — elle yazılmış bir komut listesinden değil. `ProducesContractTests`
   ve `CompositionRootTests` emsal.
4. **Hesap katmanında sunum yok:** M02'nin ölçtüğü hâliyle sunum katmanında
   **117** `Console` çağrısı, hesap katmanında **0** var — ve o sıfırı bugün
   hiçbir mimari test korumuyor. Kapı bunu tutuyor. Çekirdek `Console`'a
   yazarsa MCP aracı çıktıyı **yakalayamaz**: protokol gövdesine gitmesi
   gereken metin süreç çıktısına gider ve stdio taşımasında **protokolü
   bozar**.
5. Kapının kırmızı yanabildiği ölçüldü ve rapora yazıldı (`CLAUDE.md` §6).

## 5 · Bitti tanımından karşıladıkları

- **§4** — *"Komut çekirdeğindeki her komut ya MCP aracı ya gerekçeli muafiyet;
  sayı sabitle tutuluyor."* Bu maddenin tek sahibi M02.
- **§2**'yi **besliyor**: M02'nin ilan ettiği altı aracın şeması ve örnek
  çağrısı M01'in sözleşme kapısından geçmek zorunda. Kapıyı M01 kuruyor ama
  yükü **araç ekleyen** taşıyor.

## 6 · Bağımlılık ve sıra

**M01.** Çekirdek ancak araç sözleşmesi (`BizigoMcpTool`) ve keşif
(`McpToolDiscovery`) varken ikinci sunumunu üretebilir.

## 7 · Bilinen sınırlar ve açık sorular

1. **Sabit sayı M01'e bağlı.** `ExpectedExemptCount = 5` on bir komut
   varsayıyor; bu ağaçta **on** var. M01 birleşmeden çivilenirse kapı kırmızı
   yanar. Sayıyı M01'den **sonra** çivileyin.
2. **Kapı tek yönlü** (§3) — komutu olmayan bir araç sınanmıyor.
3. **Bağlam maliyeti.** M01 ölçtü: araç başına **194 belirteç**, ve en pahalı
   kalem şema değil **`description` metni**. Altı araç ≈ 1160 belirteç ve bu
   **her bağlamda** taşınıyor. Açıklamaları kısa tutmak bir üslup tercihi değil
   bir bütçe kalemi.
4. **Açık soru:** çekirdek komutları hangi imzayla alıyor — `AccessScope`
   parametresi çekirdeğin sözleşmesinde mi, sunum katmanında mı? Plan sessiz.
   `bizigo` yüzeyi için kapsamın çekirdekten geçmesi gerekiyor
   ([M04](../okuma-araclari/index.md)); bugünkü altı komutun hiçbiri kapsamlı
   sorgu yapmadığı için soru bugün **görünmüyor** ve M04'te patlıyor.
