---
title: "M06 — Redaksiyon ve K6 kapısı"
kind: ticket
status: 2
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
   değil. **Açık kalıyor** — ve kalması doğru.

2. ~~**Açık soru:** ağ sınırı beyanı nerede duruyor?~~ **Cevaplandı: taşıma
   başına.** Belgenin sezgisi doğruydu — HTTP ile stdio farklı sınırlarda
   koşabiliyor, dolayısıyla tek bir sunucu-geneli ayar yanlış olurdu.
   HTTP `Mcp:DataBoundary` yapılandırmasından, stdio
   `bizigo mcp serve --data-boundary`'den okuyor. Beyan `BizigoMcpServer.Apply`
   içinde tek bir kapıda birleşiyor, yani iki yol tek ölçütle sınanıyor.

3. ~~**Açık soru:** `sim.*` araçları sınır beyanından muaf mı?~~
   **Cevaplandı: muaf değil, ama `External` onlarda geçiyor.** Beyan her yüzeyde
   zorunlu (`Unspecified` reddediliyor); reddedilen tek bileşim
   `External` + `bizigo`. `bizigo-sim` ürün verisi değil simülatör durumu
   döndürüyor ve iki yüzeyin ayrı olmasının **sebebi** o risk ayrımı.

   *Bu yol bugün ürün kurulumunda ulaşılamaz* — `BizigoMcpSetup` yalnızca ürün
   yüzeyini kuruyor ve `bizigo-sim`'in HTTP'si M03'ün kararı. Bekçi yazıldı,
   yorumuna *"M03 gerçek bir yol açtığında orada da ölçülmeli"* yazıldı.

4. ~~**Aramadım:** `RedactedPrompt` bugün kaç çağrı yerinden üretiliyor?~~
   **Ölçüldü: ürün tarafında iki yer, ikisi de `ScenarioPromptBuilder`**
   (T44'ün prompt yolu, `SystemText`/`UserText` için dört çağrı ifadesi).
   MCP üçüncü tüketici oldu. `RedactedPrompt` hiçbir yerde JSON'a
   serileştirilmiyordu — bu ölçüm 6. maddedeki dönüştürücü kararını
   ucuzlattı.

5. **Kriter 3'ün cevabı bir kural değil, mekanizmanın sonucu.** Kriter kapının
   `sim.syslog.burst`'ün **bastığı satırlara** değil `sim.state`'in
   **döndürdüğü örneklere** baktığını yazmayı istiyordu. Ayrım yazılacak bir
   politika olarak çıkmadı — kapının durduğu yerden **düşüyor**: kapı
   `McpToolResult`'ta, yani bir araç çağrısının **dönüş değerinde**.
   `sim.syslog.burst`'ün ürettiği syslog satırları bir toplayıcıya gidiyor ve
   `McpToolResult`'a hiç uğramıyor, dolayısıyla modele de hiç girmiyorlar;
   `sim.state`'in döndürdüğü örnekler dönüş değerinin içinde ve kapı orada.

   Ayrımın *yazılması* yine de gerekliydi: mekanizmadan düştüğünü görmeyen
   biri bunu bir muafiyet sanıp `sim.*` için kapı gevşetebilirdi.

6. **AÇIK VE ÖNEMLİ — yapısal yük kanalı kapıyı atlıyor.** MCP'nin iki kanalı
   var: `content` (metin) ve `structuredContent` (serbest JSON). Kapı
   birincinin **imzasında** duruyor (`WithLogText(RedactedPrompt[])`);
   ikincide `McpToolResult.Structured<TPayload>` serbest bir nesne alıyor ve
   `BizigoMcpTool.ToProtocol` onu hem `structuredContent` olarak hem de bir
   metin bloğu olarak gönderiyor. Yani bir aracın yükü içindeki `string` alan
   modele **kapıdan geçmeden** iniyor.

   **Bir tip bunu tamamen kapatamaz** ve kapatmaya çalışmak yanlış olurdu:
   yükte meşru `string`'ler var (kaynak adı, zaman damgası, kimlik). M06'nın
   yaptığı, kapıyı o kanalda **kullanılabilir** kılmak:
   `RedactedPromptJsonConverter` ile bir yük alanı `RedactedPrompt` olarak
   yazıldığında tel üzerinde maskelenmiş metin olarak çıkıyor — ve ters yön
   (JSON'dan `RedactedPrompt` okumak) **fırlatıyor**, çünkü okuma yolu
   yapıcının `private` olmasını anlamsız kılardı.

   **Alanın öyle yazılması bugün mekanik olarak tutulmuyor.** Bu bir çağrı
   alışkanlığı ve bu deponun beş kez ödediği ders tam olarak o. Mekanik bir
   bekçinin şekli **gerçek bir araç görülmeden** çizilemiyor (§8: tüketicisi
   olmayan bir tip tahmindir); M04/M05 ile birlikte kapanacak bir kalem, ve
   o güne kadar **yazılı** — çünkü yazılı olmayan hâli, kapı varmış gibi
   okunan olmayan bir kapıdır.
