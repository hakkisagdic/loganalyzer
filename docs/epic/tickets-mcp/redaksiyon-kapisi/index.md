---
title: "M06 — Redaksiyon ve K6 kapısı"
kind: ticket
status: 0
---

# M06 — Planın ilk cümlesi bir yasaktı; burada mekanizma oluyor

[MCP teknik plan §1](../../mcp-teknik-plan/index.md) iki cümleyi geri kalan
her şeyden **önce** yazdı, ve sebebini de yazdı: MCP'nin cazibesi tam olarak
*"araç zaten veriyi getiriyor, modele de versin"* cümlesinde.

> MCP araçlarının log içeriği döndüren her çıktısı `RedactedPrompt`'tan geçmek
> zorunda. MCP katmanı `string` döndürüyorsa kapı **atlanmış** olur.

> MCP sunucusu kendi ağ sınırını **beyan etmek** zorunda; `Unspecified`
> reddediliyor.

M06 bu iki cümleyi mekanizmaya bağlıyor.

## 1 · Bugünkü hâl — ölçüldü

**İki ilkel de bugün var.** M06 sıfırdan kurmuyor; var olanı MCP yüzeyine
zorunlu kılıyor.

| İlkel | Nerede | Ölçülen |
| --- | --- | --- |
| `RedactedPrompt` | `src/Bizigo.Contracts/Security/RedactedPrompt.cs` | `sealed partial class`, yapıcısı **`private`** (satır 131) — T41 kapıyı gerçekten derleyiciye bağlamış |
| Ağ sınırı | `src/Bizigo.Rca/Models/` | `ModelDataBoundary` enum'ı · `ModelBoundaryGate` · `ModelBoundaryVerdict(ModelEndpoint?, string? Rejection)` · `ModelEndpointOptions.DataBoundary` varsayılanı **`Unspecified`** |

**M01'in yüzey ayrımı aynı kalıbı zaten kullanıyor** (birleşmemiş dalından
okundu, değişebilir): `McpSurface` enum'ında `Unspecified = 0` **reddediliyor**
ve gerekçesi kaynakta yazılı — *"varsayılanı `Product` yapmak, yüzeyini
yazmayı unutan bir aracı sessizce ürün verisi döndüren kümeye koyardı; yani
unutkanlığın bedeli en tehlikeli tarafa düşerdi."* Kalıbın kendisi T42'den
alınmış.

## 2 · Kapsam

**İçinde**

- Log içeriği döndüren MCP araçlarının dönüş tipinin `RedactedPrompt`'a
  bağlanması — **derleyicide**, bir çağrı alışkanlığında değil.
- MCP sunucusunun ağ sınırını beyan etmesi; `Unspecified` reddi.
- İki kapının **kırmızı yanabildiğinin ölçülmesi**.

**Dışında**

- Araçların kendisi — [M04](../okuma-araclari/index.md) ve
  [M05](../rca-araclari/index.md).
- Redaksiyon tabanının **içeriği** (hangi desen maskelenir, hangisi sayılır):
  T41'in işi, burada tüketiliyor.

## 3 · Kabul kriterleri

1. **Log içeriği döndüren bir araç `string` döndüremiyor.** Kapı bir kural
   değil bir tip: aracın sözleşmesi redakte edilmiş tipi istiyor, ve
   `RedactedPrompt`'ın yapıcısı `private` olduğu için tek çıkış yolu T41'in
   kapısı.
2. **Sunucu ağ sınırını beyan ediyor**, `Unspecified` reddediliyor — reddin
   `ModelBoundaryVerdict.Rejection` gibi **gerekçeli** olması, sessiz bir
   `false` değil.
3. `bizigo-sim` yüzeyi için ayrım yazılı: kapı `sim.syslog.burst`'ün **bastığı
   satırlara** değil, `sim.state`'in **döndürdüğü örneklere** bakıyor (plan §4).
   Ayrım yazılmazsa kapı ya gereksiz yere her şeyi maskeler ya hiçbir şeyi.
4. **İki kapı da kırmızı yanabiliyor ve bu ölçüldü** (`CLAUDE.md` §6): bir
   araca `string` döndürten bir kusur uygulanıp derlemenin kırıldığı görüldü;
   sınır beyanı kaldırılıp reddin geldiği görüldü. Kusurun dosyada olduğu
   **iddia edildi**, sonra geri alındı.

## 4 · Bitti tanımından karşıladıkları

- **§5** — *"Log içeriği döndüren hiçbir MCP aracı `RedactedPrompt` kapısını
  atlamıyor — ve bu derleyiciye bağlı, bir çağrı alışkanlığına değil."*
- **§6** — *"MCP sunucusu ağ sınırını beyan ediyor; `Unspecified`
  reddediliyor."*

İkisinin de tek sahibi M06.

## 5 · Bağımlılık ve sıra — **sona bırakılmadı, sonda duruyor**

**M04 · T41 · T42.**

Plan §7'nin cümlesi: M04 ve M05 **M06 olmadan sevk edilmiyor**. Sıra şu —
araçlar yazılır, kapı takılır, **sonra ikisi birlikte açılır**.

Kapıyı önce yazmak *tüketicisi olmayan bir tip* yazmak olurdu (`CLAUDE.md`
§8); sonra açmak ise arada **bir sürüm boyunca kapısız bir yüzey** bırakmak.
Bu ticket'ın sondaki yeri bir erteleme değil bir sıra.

## 6 · Bilinen sınırlar ve açık sorular

1. **Kapı tipi bağlıyor, içeriği bağlamıyor.** `RedactedPrompt` bir metnin
   kapıdan **geçtiğini** garanti ediyor; o kapının hangi sırrı yakaladığını
   değil. Redaksiyon tabanının kapsamı T41'in ölçümü ve bu belgenin iddiası
   değil.
2. **Açık soru:** ağ sınırı beyanı **nerede** duruyor — MCP sunucusunun
   yapılandırmasında mı, araç başına mı? Plan *"sunucu beyan eder"* diyor;
   ama `stdio` ile HTTP taşımaları farklı sınırlarda koşabilir ve o hâlde
   beyanın **taşıma başına** olması gerekir. Plan bunu söylemiyor,
   uydurmuyorum.
3. **Açık soru:** `sim.*` araçları sınır beyanından muaf mı? Yüzey ürün verisi
   döndürmüyor, ama simülatör çıktısı yine de modele gidiyor. Kriter 3 ayrımı
   yazmayı istiyor; **kararı vermiyor**.
4. **Aramadım:** `RedactedPrompt`'ın bugün kaç çağrı yerinden üretildiğine
   bakmadım — kapının bugünkü tüketicileri M06'nın tasarımını
   kolaylaştırabilir.
