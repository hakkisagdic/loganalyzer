---
title: "T48 — `Produces<T>` kapısının kör noktası: dördüncü tekrar"
kind: ticket
status: 2
---

# T48 — Kapının asgari servis listesi ile uç dosyaları arasına bir bağ

## 1 · Bu ticket bir kusur değil, bir tekrar sayısı

`ProducesContractTests` uçları bulmak için minimal API'yi yansıtmayla geziyor
ve bunun için **asgari bir servis listesi** tutuyor. Bir uç dosyası listede
olmayan bir servisi enjekte ederse, minimal API o parametreyi **gövde
parametresi** sanıyor ve o dosyadaki **bütün uçlar** kapıya görünmez oluyor.

Bu delik dört kez açıldı:

| # | Eksik servis | Bedeli |
| --- | --- | --- |
| 1 | `AlertPreview` | Uçlar kapıya görünmez |
| 2 | `CatalogCoverageCache` | Aynı |
| 3 | `ParserPublishGate` | Aynı |
| 4 | `RcaAdmission` | `EvidenceEndpoints.cs`'in **tamamı** — 15 test birden düştü |

**Her seferinde bulan kişi farklı.** Yani sorun dikkat değil, bir mekanizmanın
yokluğu. Dördüncü tekrar bir kalıp değil bir karar noktası.

## 2 · Neden bu deponun en tehlikeli kapı sınıfı

Kapının kendisi §7'nin *"bir bekçinin sessizce atlaması"* örneği olarak zaten
kayıtlı: liste elle tutulduğu için **16 uç kapıya hiç görünmedi ve üç test de
yeşildi.** Yeşilliği hiçbir şey ifade etmiyordu.

Şimdiki hâlinde iki katman var ve ikisi de sessiz:

1. **Uç görünmez olur** — kapı onun sözleşmesini hiç sınamaz.
2. **Kapı yine yeşil kalır** — çünkü göremediği bir şeyi eksik saymıyor.

Dördüncü açılışta 15 test düşmesi bir iyileşme değil **tesadüf**: `RcaAdmission`
bir *gövde* parametresi gibi görünecek şekle sahipti ve yansıma patladı. Şekli
biraz farklı olsaydı uçlar sessizce kaybolurdu.

## 3 · Ne isteniyor

**Uç dosyasının enjekte ettiği servisler ile kapının asgari listesi arasına bir
bağ.** Bağın şekli açık bırakılıyor; üç aday var ve seçimi ticket sahibi
gerekçeleyecek:

| Aday | Ne yapar | Riski |
| --- | --- | --- |
| **A · Listeyi türet** | Servisleri DI kayıtlarından ya da uç imzalarından **keşfet**, elle listeden değil | Keşif yüzeyi kapının kendi kör noktasını devralabilir |
| **B · Sayım kapısı** | Kapının **kaç uç gördüğünü** sabitle; sayı düşerse kırmızı | Sayı elle tutulan ikinci bir liste olur — ama tek bir sayı, ve `ExpectedExemptCount` emsali var |
| **C · Dosya kapsaması** | Her uç **dosyasının** kapıda en az bir ucu olduğunu sın | Boş bir dosya ya da tek uçlu bir dosya yanlış güven verir |

**A ve C birlikte muhtemelen doğru cevap** ama ölçülmeden seçilmemeli. B'nin
tek değeri şu: **bugün ölçülebilir ve hemen kırmızı yanabiliyor**, diğer ikisi
yazılana kadar bir emniyet ağı sağlıyor.

## 4 · Kabul kriterleri

1. Bir uç dosyasına listede olmayan bir servis eklemek **kırmızı yakıyor**, ve
hata mesajı **hangi dosya** ve **hangi servis** olduğunu söylüyor. Bugünkü hata
ucu değil **parametre çıkarımını** gösteriyor ve okuyan kişiyi yanlış yere
gönderiyor.
2. Kırmızı yanabildiği **ölçülmüş** ve rapora yazılmış (§6).
3. Kapının **göremediği** bir uç kalırsa bu **sayılıyor** — yani kapı kendi
kapsamını beyan ediyor. Beyan etmeyen bir kapı, kapsamını iddia etmiş sayılır.
4. Seçilen adayın **kaçırdığı hâl yazılı**. Üç adayın üçü de bir şey kaçırıyor;
kaçırdığı yazılmayan bir bekçi, bu deponun beş kez adını koyduğu şeye dönüşüyor.

## 5 · Kapsam dışı

- `Produces<T>` bildirimlerinin **içeriğini** doğrulamak — bu kapı zaten var.
- `Pending` / `Exempt` listelerinin bakımı — ayrı disiplin, §8'de yazılı.

## 6 · Ne ölçüldü, ne arandı

**İlk bakılacak yer:** `ProducesContractTests`'in asgari servis listesi ve
yansıma yolu.

**Aradım ve elemedim:** dört olayın dördü de aynı mekanizmadan doğdu —
farklı uçlar, farklı ajanlar, tek sebep. Bedelin dördüncüde büyümesi mekanizma
değişikliğinden değil, eksik servisin **şeklinden** kaynaklandı.

**Aramadım:** aynı yansıma kalıbını kullanan başka bir kapı var mı — `ui/`
tarafındaki sözleşme kapıları ve `ArchitectureTests` bu açıdan taranmadı. Aynı
kör noktanın ikinci bir örneği orada duruyor olabilir.

---

## 7 · Ne yapıldı — seçilen aday ve gerekçesi

**A + C seçildi, B reddedildi.**

| Aday | Karar | Gerekçe |
| --- | --- | --- |
| **A · Listeyi türet** | **Alındı** | Elle servis listesi tamamen kaldırıldı. Kaydedilecek küme artık **uç dosyasının kendi metotlarının parametre tiplerinden** türetiliyor. |
| **B · Sayım kapısı** | **Reddedildi** | Tek bir toplam sayı, on beş uçlu bir dosyanın kaybolmasını gizleyebiliyor: kalan dosyalar sayıyı doldurur. Ayrıca altı ajan paralel uç ekliyor; her uçta güncellenen bir sabit, güncellenmesi **rutinleşen** bir sabittir ve rutin güncelleme bekçiyi kayıt olmaktan çıkarır (`CLAUDE.md` §6). C, aynı soruyu dosya başına soruyor ve rutin güncelleme gerektirmiyor. |
| **C · Dosya kapsaması** | **Alındı** | Her `Map*` uzantısının kapıya **en az bir** uç verdiği ayrı ayrı sayılıyor; ayrıca hangi uzantının kaç uç verdiği tutuluyor. |

### Türetme nasıl çalışıyor

1. Uç dosyasının (ve derleyicinin ürettiği `<>c` / `<>c__DisplayClass` lambda
   taşıyıcılarının) bütün metot parametreleri geziliyor.
2. Yapısal eleme: değer tipi, `string`, dizi, delege, açık generic ve minimal
   API'nin kendi bağladığı tipler (`HttpContext`, `ClaimsPrincipal`, …) düşüyor.
3. **Neyin servis olduğu tahmin edilmiyor:** boş bir kapta
   `IServiceProviderIsService`'e soruluyor. Çerçevenin zaten tanıdığı tipler
   (`ILogger<T>`, `IOptions<T>`, `TimeProvider`) kaydedilmiyor; tanımadığı her
   şey kaydediliyor. Elle bir "çerçeve tipleri" listesi tutmak, kaldırdığımız
   listenin ikinci bir kopyası olurdu.

Sonuç: bir uç dosyasına yeni bir servis eklemek **artık kapıda hiçbir satır
gerektirmiyor**. Dört kez açılan delik bu.

### Kapsam beyanı (kabul kriteri 3)

Kapı artık kendi kapsamını **sayıyor**:

- Her uç dosyası ya **ölçüldü** (kaç uç verdiği yazılı) ya **kör** sayıldı
  (neden görülemediği dosya adıyla yazılı). İkisinin toplamı uzantı sayısına
  eşit olmalı — arada kaybolan yok.
- `RouteEndpoint` olmadığı için denetime girmeyen uç sayısı ayrıca sayılıyor.
- Kapının **yapısı gereği** hiç göremediği uçlar (`Program.cs` içinde satır içi
  kayıtlı `/`, `/healthz`, iki `/internal/*`, `/openapi/*`) `OutsideTheGate`
  listesinde **gerekçesiyle** yazılı ve sayısı `ExpectedOutsideCount` ile sabit
  — `Exempt` emsali. Biri bir gün bir `Map*` uzantısına taşınırsa liste
  bayatlamış olur ve bu kırmızı yanıyor.

## 8 · Seçilen çözümün kaçırdığı hâller (kabul kriteri 4)

1. **Handler imzası uç dosyasının dışında tanımlıysa** türetme onu göremez —
   başka bir sınıfın metot grubu gibi. O hâlde `Map*` yine patlar, **ama
   sessizce değil**: hata `Blind`'a dosya adıyla düşüyor ve
   `Kapi_hicbir_uc_dosyasini_kaybetmiyor` kırmızı yanıyor. Türetmenin
   kırılganlığı bu yüzden güvenli — eksik türetme sessizlik değil gürültü
   üretiyor.
2. **Tek eksik tip artık ucu düşürmüyor — ve bu ölçüldü.** Türetme gövde
   kayıtlarını da servis saydığı için, bir handler'da tek bir tip eksik kalırsa
   minimal API onu tek gövde parametresi sayıp kaydı **tamamlıyor**: uç
   görünür kalıyor, sözleşmesi denetleniyor, hiçbir test düşmüyor. Kırmızı
   ancak **aynı handler'da iki tip birden** eksikse yanıyor. Yani kapı bu
   sınıfa karşı bağışık, ama bağışıklığın sebebi "hata veriyor" değil "hata
   veremiyor" — okuyan kişi bunu bilmeli.
3. **Dosya başına uç sayısı sabitlenmiyor.** İki uçlu bir dosyanın bir ucunu
   kaybetmesi burada görünmez; dosya hâlâ ≥1 veriyor. Alan sahiplerinin
   sabitlediği sayılar (`Olay_yuzeyi_uc_uctan_ibaret`) ayrı duruyor.
4. **`Program.cs` içindeki satır içi uçlar denetlenmiyor.** Sayılıyor ve
   gerekçesi yazılı, ama sözleşmeleri sınanmıyor.
5. **Keşfedilen bir uzantının gerçekten `Program.cs`'te çağrıldığı
   sınanmıyor.** Var olan ama hiç bağlanmayan bir uç dosyası kapıya görünür,
   ürüne görünmez. Aramadım; ayrı bir kapı işi.

## 9 · Ölçülen kırmızılar (kabul kriteri 2)

`CLAUDE.md` §6'nın iddia adımı her kusurda uygulandı: kusur yazıldı → dosya
okundu ve kusurun orada olduğu **iddia edildi** → koşuldu → geri alındı.

| # | Kusur | Sonuç |
| --- | --- | --- |
| **Kontrol** | **Eski kapı** + adı `Map` ile başlamayan yeni bir uç dosyası (`ProbeRoutes.AddProbeRoutes`, gerçek bir `GET /v1/probe` ucu, `Produces<T>` yok) | **16/16 YEŞİL.** Kapı ucu hiç görmedi ve hiçbir şey söylemedi. |
| **B** | Yeni kapı + aynı kusur | **KIRMIZI** — `Uç kaydedebilecek ama keşfedilmeyen metot(lar): ProbeRoutes.AddProbeRoutes` |
| **A** | Türetme `RcaAdmission`'ı görmesin | **YEŞİL — kusur etkisiz.** Sebebi ölçüldü ve §8.2'ye yazıldı. |
| **A2** | Türetme `RcaAdmission` **ve** `EvidenceBundleFactory`'yi görmesin | **KIRMIZI** — `EvidenceEndpoints kapıya görünmüyor — MapRca kaydedildi ama uçları alınamadı`, ardından minimal API'nin parametre tablosu |
| **C2** | `MapOtlpLogs` hiç uç kaydetmesin | **KIRMIZI** — `Bu uzantı(lar) çağrıldı ama hiç uç kaydetmedi: LogsEndpoint.MapOtlpLogs` |
| **D** | Kapsam dışı yazılan bir ucun aslında kapıya görünmesi | **KIRMIZI** — `Kapsam dışı yazılan uç(lar) artık kapıya görünüyor: POST /v1/replay` |

**Kontrol satırı ticket'ın asıl ölçütü.** *"Kaç test düştü"* değil, *"kapı ucu
görüyor mu"*: eski kapıda gerçek bir ürün ucu tamamen görünmezdi ve on altı
testin on altısı yeşildi.

Kusur A ayrıca §6'nın kendi uyarısının örneği oldu: yeşil bir sonuç *"kusur
etkisiz"* de anlatabiliyor. Kusurun dosyada olduğu iddia edilmişti, yani
"uygulanmadı" ihtimali elenmişti — geriye tek açıklama kaldı ve o açıklama
mekanizma hakkında **yeni bir bilgi** oldu.

## 10 · Ölçülen sayılar

- Birim paketi: **1184 geçti / 4 atlandı / 0 düştü**, 5 dk 47 sn.
- `ProducesContractTests`: 16 test → **19 test**, sessiz makinede **826 ms**.
  (Yüklü makinede aynı paket 35–53 sn okudu; `CLAUDE.md` §6'nın "yüklü makine
  yanlış sayı üretir" maddesinin bir örneği daha.)
- Kaldırılan elle servis listesi: **27 `typeof(...)` satırı** → 0.
