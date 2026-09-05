---
title: "T55 — Keşif yükleminin üç kopyası ve keşfin iki kör noktası"
kind: ticket
status: 2
---

# T55 — Keşif yükleminin üç kopyası ve keşfin iki kör noktası

**Borç:** §9 · **Ödendiği yer:** `tests/Bizigo.UnitTests/ProductDiscovery.cs`

## Borç nasıl doğdu

Üç kapı aynı yüklemi ayrı ayrı yazmıştı — `ArchitectureTests`,
`ProducesContractTests` (T48), `CompositionRootTests` (T50). Üçüncü kopya
eşiği geçtiğinin işaretiydi. T50'yi yazan ajan borcu **gizlemedi, ödemedi** ve
gerekçesi doğruydu: ortak yüzeye taşımak `ArchitectureTests`'i düzenlemeyi
gerektiriyordu, o dosya T44'ün altında canlıydı, ve aynı satırı iki ajana
yazdırmak §9'un ayrıca yasakladığı şey.

## Ticket'ın hedefi ölçümle değişti: **tek küme yanlış hedefti**

Konsolidasyondan **önce** üç kümenin listesi çıkarıldı — sayı değil liste — ve
üç kapının **üç farklı soru** sorduğu görüldü:

| Kapı | Kapsam | Küme |
| --- | --- | --- |
| `ArchitectureTests` | üretim host'unun derleme kapanışı | **16** |
| `ProducesContractTests` | yalnızca kompozisyon kökü | **1** |
| `CompositionRootTests` | bütün ürün projeleri | **18** |

Fark **iki yönlü** ve yalnızca biri delik:

- **`bizigo` (CLI) kapanışta yok** — ticket bunu önek körlüğüne bağlıyordu.
- **`Bizigo.Simulators` kapanışta yok** — ve bu **delik değil**. Simülatör
üretim kompozisyonunun parçası değil; kapanışta görünmesi bir kusur olurdu.

Yani üç kümeyi tek listeye indirmek kapsamı sessizce **daraltmakla** kalmaz,
**genişletirdi** de. Genişlemenin bedeli ölçüldü: `ArchitectureTests` bulduğu
her kayıt uzantısını **gerçek bir `WebApplicationBuilder` üzerinde çağırıyor**
ve tanımadığı imzada bilerek fırlatıyor. Kapsamı açmak, simülatörün `Add*`
uzantılarını üretim servis grafiğine çağırmak olurdu.

**Sonuç:** tekrarlanan şey **yüklem**, istenen şey kümenin **farklı dilimleri**.
Ortak yüzey adlandırılmış kapsamlar sunuyor; üç küme **birebir korundu**.

## Ticket'ın nedensellik zinciri de yanlıştı

Ticket: *"`Bizigo.` önekine bakan her yansıma keşfi CLI derlemesini hiç
görmüyor — ve `ProductAssemblies` tam o öneke bakıyor."*

Ölçüldü: **`src/` ve `sim/` altında hiçbir proje `Bizigo.Cli`'yi
referanslamıyor.** `ProductAssemblies` `Bizigo.Api`'den başlayıp referansları
yürüyor; CLI **ikinci bir kompozisyon kökü** ve o kapanıştan erişilebilir
değil.

> **Öneki düzeltmek CLI'yi görünür yapmıyor.** İki bağımsız sebep var ve baskın
> olanı **erişilebilirlik**. Önek yine de gizli bir kusur ve kapatıldı — ama
> gerekçesi bu, CLI değil.

## Keşfin **iki** kör noktası

### 1 · Önek körlüğü — bilinen, bugün kurbanı yok

`reference.Name.StartsWith("Bizigo.")` bir **konvansiyona** bakıyor.
`Bizigo.Cli` onu `<AssemblyName>bizigo</AssemblyName>` ile zaten bozuyor.
Süzgeç artık **diskten** soruyor: *"bu ad, ürün projelerinden birinin gerçek
derleme adı mı?"*

### 2 · Budama körlüğü — **yeni ölçüldü, kurbanı bugün çıktı**

`Assembly.GetReferencedAssemblies()` **meta veriye yazılmış** referansları
döndürüyor, ve derleyici kodda hiçbir tipine dokunulmayan referansı **meta
veriden düşürüyor**. Yani `Bizigo.` önekli, `ProjectReference`'ı olan bir proje
bile kapanışta **olmayabilir**.

Bu ticket'ta hipotez olarak yazıldı ve **ölçülemedi**; ölçümü **M04'ün ajanı**
yaptı ve tam olarak yaşadı: beş araç yazıldı, `ProjectReference` eklendi, çözüm
0 hata 0 uyarı derlendi, uyum kapısı **yeşil kaldı**, ve sunucu hâlâ tek araç
ilan ediyordu. `strings` ile bakıldığında derleme adı meta veride **hiç yoktu**.

**Bu deponun ölçtüğü şey burada iki yönlü:** aynı olgunun diğer ucu
`CompositionRootTests`'in belgesinde kayıtlı — T44 **ilgisiz** bir referans
ekleyince `Bizigo.ScenarioPlugin` kapanışa girmiş ve keşif bir fazla bulmuştu.
Orada ilgisiz bir referans bir şeyi **görünür** yaptı, burada kullanılmayan bir
referans bir şeyi **görünmez** bıraktı. İkisi de tesadüfen keşfedildi.

**Bu ticket'ın kendi ölçümü ise farklı bir şey söylüyor ve ikisi çelişmiyor:**
bugünkü grafikte **budama olmamış** — `Bizigo.Api`'nin meta veri kapanışı 16 ve
diskten hesaplanan `ProjectReference` kapanışıyla birebir aynı. Yani mekanizma
gerçek ama bugünkü kapanışta kurbanı yok; **doğruluğu tesadüfe bağlı**, çünkü
bir proje kullanılmaz hâle geldiği gün sessizce düşer.

Disk tabanlı keşif **ikisini birden** kapatıyor: meta veriye hiç bakmıyor.

## Kapsamın beyanı — ve bekçisi

Keşif diskten gidiyor ve yalnızca `src/` ile `sim/` altına bakıyor. Oraya
konmayan bir ürün projesi **hiçbir kapıya görünmez** ve görünmediği hiçbir
yerde şikâyet üretmez.

Beyan bir yoruma bırakılmadı: ölçüt **çözümün kendi proje listesi**.
`Bizigo.sln` hangi projeleri derliyorsa, test projeleri dışındakilerin hepsi
beyan edilen alanların altında olmak zorunda. Aciliyeti bugün gerçek — M02
`Bizigo.Commands` ve `Bizigo.Commands.Mcp`, M04 `Bizigo.Mcp.Product` getiriyor.

## Ölçülen bekçiler

Yordam: kusuru uygula → dosyada olduğunu **iddia et** → koştur → **yedekten**
geri al.

| Kusur | Sonuç |
| --- | --- |
| Ortak **yüklem** bozuldu (uzantı süzgeci ters çevrildi) | **19 test düştü** — üç kapı birden ✅ |
| Kapsam beyanı yanlış alana bakıyor | **5 test düştü** ✅ |
| Önek körlüğü geri kondu | **üç kapı da YEŞİL kaldı** ⚠️ |

Üçüncüsü bu ticket'ın en öğretici ölçümü: **düzeltmenin bekçisi yoktu.** Bugün
yeniden adlandırılmış hiçbir proje kapanışta olmadığı için davranışsal bir
iddia kurulamıyordu, ve bir sonraki kişi düzeltmeyi gerekçesiz bir süsleme
sanıp geri alabilirdi.

Çözüm süzgeci **adlandırmak** oldu (`ProductDiscovery.IsProduct`) ve onu
doğrudan sınamak: *`bizigo` bir ürün derlemesi, ve `Bizigo.` öneki onu
reddederdi.* Kusur geri konduğunda artık **kırmızı yanıyor** — ölçüldü.

## Öncesi = sonrası

| | Önce | Sonra |
| --- | --- | --- |
| Kapanış | 16 | **16** (birebir aynı liste) |
| Kök | 1 (`Bizigo.Api`) | **1** |
| Bütün ürün | 18 | **18** (birebir aynı liste) |
| Kayıt uzantısı toplamı | 33 | **33** (17 servis + 16 uç) |

Kapsam değişmedi. Değişen tek şey yüklemin **tek yerde** olması ve keşfin
konvansiyon yerine diske bakması.
