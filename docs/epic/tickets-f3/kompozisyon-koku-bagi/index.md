---
title: "T50 — Keşfedilen uzantı ile kompozisyon kökü arasındaki bağ"
kind: ticket
status: 2
---

# T50 — Var olmak ile bağlı olmak ayrı sorulardır

## 1 · Neden bu ticket: aynı sınıf bir turda iki kez bağımsız bulundu

**T48** kendi raporunda "aramadım" diye yazdı:

> Keşfedilen bir uzantının `Program.cs`'te gerçekten çağrıldığı sınanmıyor:
> var olan ama hiç bağlanmayan bir uç dosyası kapıya görünür, ürüne görünmez.

**T44**, ondan bağımsız ve aynı gün, canlı örneğine çarptı:
`AddBizigoScenarioPlugins` T43'ten beri yazılı duruyor ve hiçbir kompozisyon
kökünden çağrılmıyor.

Görülmesi de **tesadüftü**: T44 ilgisiz bir referans ekleyip
`Bizigo.ScenarioPlugin`'i `ArchitectureTests`'in geçişli kapanışına sokunca
keşif bir fazla buldu ve elle tutulan beklenen küme kırmızı yandı. Referans
eklenmeseydi kimse görmeyecekti — `CLAUDE.md` §7'nin kaydettiği cümle:
*bir kusurun yalnızca tesadüfen görülebilir olması, kusurun kendisi kadar
ciddi.*

Ortak nokta iki bekçide de aynı: **bir şeyin var olduğunu sınayan bekçiler,
o şeyin bağlandığını sınamıyor.** `ArchitectureTests` keşfettiği her `Add*`
uzantısının kapsam doğrulamasından geçtiğini görüyor; `ProducesContractTests`
keşfettiği her `Map*` uzantısının uçlarının sözleşmesi olduğunu görüyor. İkisi
de uzantıyı **kendi kurduğu kapta** çağırıyor — yani ikisi de `Program.cs`'in o
uzantıyı hiç çağırmadığı bir dünyada yeşil yanar.

## 2 · Okuma yöntemi: kapının kendi kör noktası

İki yol vardı ve seçim kapsamı belirliyor.

| Yöntem | Görür | Göremez |
| --- | --- | --- |
| **Metin araması** (`Program.cs`'te `AddX()` aramak) | Doğrudan çağrılar | **Bir uzantının başka bir uzantının içinden çağrılması.** Bu deponun elle listesi tam olarak bu yüzden eksik kalmıştı: `AddBizigoDiscovery`, `AddBizigoIngest`'in içinden çağrılıyor. Ayrıca kaynak biçimine bağlı. |
| **Derlenmiş IL** (seçilen) | Koşullu çağrı, zincir, `if` içindeki kayıt — hepsi IL'de duruyor | Aşağıdaki üç madde |

**Seçilen: IL.** Giriş noktalarından (`Bizigo.Api` ve CLI) başlayıp
`call` · `callvirt` · `newobj` · `ldftn` · `ldvirtftn` operandları çözülerek
ürün derlemelerinin içinde geçişli bir çağrı grafiği kuruluyor.

### IL'in göremedikleri — beyan

1. **Yansımayla ya da yapılandırmadan** kurulan kayıt. Böyle bir çağrı IL'de
   `call` olarak durmuyor.
2. **Ölü kod.** `if (false)` içindeki bir çağrı IL'de duruyor ve kapı onu
   "bağlı" sayıyor. Kapı *"ulaşılabilir mi"* değil *"çağrı grafiğinde var mı"*
   sorusunu cevaplıyor.
3. **Kaynak üreteçlerinin** çalışma anında kurduğu bağlar.

Delege üzerinden kurulan bağlar **görülüyor** (`ldftn` toplanıyor), yoksa
`Map*` içindeki her lambda handler kapanışın dışında kalırdı.

### Taşıyıcı ayrıntı: async giriş noktaları

Her iki kök de `await` ile bitiyor, yani gövdeleri derleyicinin ürettiği durum
makinesinde ve oraya `call` ile gidilmiyor — çerçevenin generic
`Start<TStateMachine>` çağrısıyla giriliyor. `AsyncStateMachineAttribute`
takip edilmezse kapanış **giriş noktasının ilk satırında biter**. Bu tahmin
değil, ölçüldü: takip kırıldığında kapanış 3385 metottan **6**'ya düşüyor ve
33 uzantının 33'ü birden "bağlı değil" görünüyor.

## 3 · Ölçülen bugünkü durum

| Ölçüm | Değer |
| --- | --- |
| Ürün projesi (`src/` + `sim/`) | **18** — hepsinin derlemesi yüklenebildi |
| Keşfedilen kayıt/uç uzantısı | **33** |
| Kompozisyon kökünün çağrı grafiğinde | **32** |
| **Bağlı değil** | **1** — `ScenarioServiceCollectionExtensions.AddBizigoScenarioPlugins` |
| Çağrı grafiği büyüklüğü | 3385 metot |
| Çözülemeyen IL metot tokenı | **0** |

**Sorunun cevabı: tek örnek, desen değil.** Bugün ürüne bağlanmamış tek uzantı
`AddBizigoScenarioPlugins`. Ama *bir örnek* ile *bir desen* arasındaki fark
sayıda değil mekanizmada: onu görünmez kılan şey bir dikkatsizlik değil, hiçbir
bekçinin bu soruyu sormamasıydı — ve o mekanizma 33 uzantının hepsi için
geçerliydi.

### Yan bulgu: CLI derlemesinin adı `Bizigo.Cli` değil

`src/Bizigo.Cli/Bizigo.Cli.csproj` derlemeyi
`<AssemblyName>bizigo</AssemblyName>` ile yeniden adlandırıyor. Bu yüzden
**`Bizigo.` önekine bakan her yansıma keşfi CLI derlemesini hiç görmüyor** —
bu kapının ilk hâli de görmedi (kök bulunamadı diye kırmızı yandı), ve
`ArchitectureTests.ProductAssemblies` de aynı öneke bakıyor.

Bu, `CLAUDE.md` §4'ün *"dizin adına güvenme"* dersinin başka bir kılığı:
**derleme adı da bir konvansiyon ve konvansiyona bakan keşif, elle tutulan bir
listenin kılık değiştirmiş hâli.** Kapı bu yüzden derleme adı önekine değil,
diskteki proje dosyalarına ve onların gerçek `<AssemblyName>`'ine bakıyor.

**Ama kurbanı bugün yok, ve bunu iddia değil ölçü olarak yazıyorum:** 33
uzantının derleme dağılımında `bizigo` **hiç geçmiyor** — CLI derlemesi tek bir
`Add*`/`Map*` uzantısı bildirmiyor. Yani `ArchitectureTests`'in bu kör noktası
gerçek ama şu an hiçbir şeyi kaçırmıyor.

### Yan bulgu: CLI kökü bugün atıl

CLI kökünü listeden düşürmek **hiçbir şeyi değiştirmiyor** (ölçüm anındaki beş
testin beşi de yeşil kaldı):
CLI'den erişilen her uzantıya `Bizigo.Api`'den de erişiliyor. Kök listede
duruyor çünkü soru *"ürüne bağlı mı"* ve CLI ürünün bir parçası; ama bugün
**iş görmediği** yazılı olmalı, yoksa bir sonraki kişi kökün bir şey
kanıtladığını sanar.

## 4 · Ölçülen kırmızılar

`CLAUDE.md` §6'nın iddia adımı her kusurda uygulandı: kusur yazıldı → dosya
okundu ve kusurun orada olduğu **iddia edildi** → koşuldu → geri alındı.

| # | Kusur | Sonuç |
| --- | --- | --- |
| **Kontrol A** | Kusur yok — bugünkü depo, listeler boş | **KIRMIZI** — `Bu uzantı(lar) hiçbir kompozisyon kökünden çağrılmıyor: ScenarioServiceCollectionExtensions.AddBizigoScenarioPlugins` |
| **Kontrol B** | Kusur yok — `AddBizigoDiscovery` (kökte birebir GEÇMİYOR, `AddBizigoIngest`'in içinden çağrılıyor) | **YEŞİL** — zincir takip ediliyor, yanlış pozitif yok |
| G | Zincir takibini kapat (metin araması taklidi) | **KIRMIZI** — `AddBizigoDiscovery` dahil **12 uzantı** yanlış pozitif olarak düşüyor; `Kapi_zinciri_takip_ediyor` da kırmızı |
| E | CLI kökünü düşür | **YEŞİL** — kök bugün atıl (§3) |
| F | Async durum makinesi takibini kır | **KIRMIZI** — kapanış 3385 → 6 metot, 33/33 uzantı kopuk |
| — | (ilk hâl, CLI kökü `Bizigo.Cli` adıyla aranırken) | **KIRMIZI** — `Giriş noktası bulunamayan kompozisyon kökü: Bizigo.Cli` |

**Kontrol satırları ölçütün kendisi ve tek başına hiçbiri yetmiyor.** A tek
başına ölçülseydi her şeyi düşüren bir kapı da geçerdi; B tek başına ölçülseydi
hiçbir şeyi düşürmeyen bir kapı da geçerdi. İkisi birlikte kapının **doğru
yerde** durduğunu gösteriyor.

Ölçüt bu yüzden *"kompozisyon kökünden **erişilebiliyor mu**"*, *"`Program.cs`'te
birebir geçiyor mu"* değil. İkincisi `AddBizigoDiscovery`'yi düşürürdü ve bu bir
**yanlış pozitif** olurdu — bedeli bu depoda yazılı: insanlar yanlış pozitif
veren bir bekçiyi susturmayı öğreniyor, ve susturulan bekçi olmayan bekçiden
kötü. Zincir takibi bu yüzden `Kapi_zinciri_takip_ediyor` ile **çivilenmiş
durumda**: biri bir gün IL kapanışını metin aramasıyla değiştirirse orası
kırmızı yanıyor.

Kusur E'nin yeşil kalması bir başarısızlık değil bir **ölçüm**: T48'in
kusur A'sıyla aynı sınıf. İddia adımı "uygulanmadı"yı elediği için geriye tek
açıklama kaldı ve o açıklama kapı hakkında bilgi oldu.

## 5 · Kapsam

**İçinde**

- Keşfedilen her `Add*(this IServiceCollection)` ve
  `Map*(this IEndpointRouteBuilder)` uzantısı için: ya kompozisyon kökünün
  çağrı grafiğinde, ya `PendingWiring`'de (ticket atfıyla), ya `NeverWired`'da
  (gerekçeyle, sayısı `ExpectedNeverWiredCount` ile çivili).
- İki liste **ayrı** — `CLAUDE.md` §8: *"bir gün kapanacak" ile "hiç
  kapanmayacak" aynı listede duramaz.*
- Kapının kapsam beyanı: kök bulunamadıysa, IL tokenı çözülemediyse, ürün
  derlemesi yüklenemediyse **sayılıyor ve kırmızı yanıyor**.

**Dışında**

- **Bulunan kopuk bağı bağlamak.** `AddBizigoScenarioPlugins`'i bağlamak
  T46/T47 kolunun kararı. T50 ölçer ve kapıyı kurar; bağlamak ayrı karar —
  T44 de kendi başına bağlamadı ve doğru yaptı.
- `ui/` tarafı.

## 6 · Bilinen sınırlar

1. **IL'in üç kör noktası** — §2'de yazılı.
2. **`ProducesContractTests` harness'ında `Accepts` çıkarımı oluşmuyor.** T48
   türetmesi gövde kayıtlarını da servis saydığı için minimal API'nin gövde
   çıkarımı o harness'ta hiç çalışmıyor. Bu kapı için doğru karar — elemeye
   çalışmak "gövde mi servis mi" tahminini kapının içine geri koyardı — ama bir
   gün `Accepts` sözleşmesi için kapı yazılırsa **o harness olduğu gibi
   kullanılamaz**. Koordinatörün kararıyla burada bilinen sınır olarak duruyor,
   ayrı dosya açılmadı.
3. **Aynı keşif yüklemi depoda üç yerde duruyor** (`ArchitectureTests`,
   `ProducesContractTests`, `CompositionRootTests`) ve §9 ikinci kopyayı
   yasaklıyor. Ortak yüzeye taşımak `ArchitectureTests`'i düzenlemeyi
   gerektiriyordu; o dosya T44'ün altında **canlı ve kırmızı**, ve aynı satırı
   iki ajana yazdırmak §9'un ayrıca yasakladığı şey. **Borç ödenmedi,
   gizlenmedi.**

## 7 · Ne ölçüldü, ne arandı

**İlk bakılacak yer:** `CompositionRootTests.BuildCallGraph` ve
`ProductProjects` — kapanışın kökü ve keşfin kapsamı orada.

**Aradım ve elemedim:** 33 uzantının tamamının bağlılık durumu (32 bağlı, 1
değil) · 18 ürün projesinin tamamının yüklenebildiği · CLI derlemesinin hiç
`Add*`/`Map*` uzantısı bildirmediği · CLI kökünün bugün atıl olduğu ·
çözülemeyen IL tokenı kalmadığı.

**Aramadım:** `ui/` tarafındaki bağlar · `AddBizigoScenarioPlugins`'in
bağlanması gerekip gerekmediği (T46/T47'nin kararı, ölçmedim) · ölü kod
içindeki çağrıların bu depoda gerçekten var olup olmadığı — kapı onları "bağlı"
sayıyor ve **böyle bir örnek olup olmadığına bakmadım** · `ArchitectureTests`'in
elle tutulan beklenen kümesinin bu kapıyla nasıl ilişkilendirileceği (T44 orada
canlı).
