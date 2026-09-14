---
title: "M11 · Kestrel'in kör noktası: beyanın topolojisi"
kind: ticket
status: 2
---

# M11 · Kestrel'in kör noktası

**K6'nın kendisi:** *log verisi kurum dışına çıkmaz.* M06 bu cümleyi MCP
yüzeyinde bir kapıya çevirdi ve kapının **kör noktasını da yazdı**:

> **HTTP taşımasında dinlenen adresi doğrulamıyor.** Kestrel'in nereye
> bağlandığı `Bizigo.Api`'nin yapılandırması ve bu kapının göremediği yer.
> Kurum dışına açılmış bir dinleyici + `internal` beyanı, "yalan söyleyen
> yönetici" hâlinin **kazara** olanı.

Yani bugüne kadar `Mcp:DataBoundary=Internal` beyanı kabul ediliyordu ve sunucu
`0.0.0.0` üzerinde koşuyor olabiliyordu. Beyan *"iç ağ"* diyor, gerçek *"her
arayüz"* — ve **hiçbir yerde kırmızı yanmıyordu**.

## 1 · Ölçüm: kapı bugün neyi görüyor

Kapının imzası `Require(McpBoundaryDeclaration, McpSurface)`. Dinleyici bilgisi
**eksik değil, imzada hiç yok** — yani bu bir ihmal değil bir kapsam.

| Soru | Bugünkü kapı | Nasıl ölçüldü |
| --- | --- | --- |
| Beyan var mı (`Unspecified` reddi) | **Görüyor** | `McpBoundaryDeclaration` yapıcısı `private`; tip beyansız var olamıyor |
| Beyanın gerekçesi var mı (`Basis`) | **Görüyor** | aynı yapıcı; boş `Basis` reddediliyor |
| Ürün yüzeyi `external` mi | **Görüyor** | `McpBoundaryGate.Require`, iki üretim çağrısı (`BizigoMcpServer.Apply:127`, `McpCommandHandlers:147`) |
| Beyan hangi yapılandırmadan geldi | **Görüyor** | `BizigoMcpSetup.ReadBoundary`, anahtar `Mcp:DataBoundary` |
| **Sunucu hangi adresi dinliyor** | **GÖRMÜYOR** | `IServerAddressesFeature` deponun hiçbir yerinde geçmiyordu (`rg` → 0 eşleşme) |
| Dinleyici dışa açık mı | **GÖRMÜYOR** | aynı sebep |

Sevk edilen bağlamaların envanteri — kör noktanın bugün **kurbanı var mı**
sorusunun cevabı:

| Nerede | Bağlama | Sınıf |
| --- | --- | --- |
| `src/Bizigo.Api/Dockerfile:85` | `ASPNETCORE_URLS=http://0.0.0.0:8080` | **joker** |
| `deploy/docker-compose.yml:337` | `"${API_PORT:-5080}:8080"` publish | süreç dışı |
| `src/Bizigo.Api/Properties/launchSettings.json` | `http://localhost:5058` | loopback |
| `ui/playwright.config.ts:180` | `ASPNETCORE_URLS=http://localhost:5080` | loopback |
| `tests/…/McpKeycloakIdentityTests` | `http://127.0.0.1:0` | loopback |

**Sevk edilen tek joker bağlama container imajında** ve `Mcp:DataBoundary`
`Internal`. Yani kör noktanın kurbanı bugün **var**: `docker compose --profile
api up` ile kalkan sunucu her arayüzü dinliyor ve beyan bunu hiçbir yerde
sorgulamıyor.

## 2 · Kararın merkezi: sıra sorunu

`IServerAddressesFeature` **Kestrel bağlandıktan sonra** doluyor; M06'nın kapısı
**kayıt anında** koşuyor (`AddBizigoMcpCore`). İki cevap vardı.

| Yol | Bedeli |
| --- | --- |
| **Kapıyı geciktir** (tek kapı) | Üç şeyi birden bozuyor. (1) M06 beyan reddini **bilerek** kurulum anına çekti — *"yanlış yapılandırılmış bir sunucu hiç başlamıyor, yarım başlamıyor"*; kapıyı kalkışa taşımak o kararı geri alır. (2) Kapı taşımadan bağımsız ve `Bizigo.Cli` de onu çağırıyor; stdio'da dinleyici adresi diye bir şey yok, yani ortak kapı ASP.NET'e bağlanırdı — `Bizigo.Mcp`'nin web çatısından uzak tutulması ölçülmüş bir karar (beş `CS0433`). (3) Çağrı yeri M07'nin dosyası. |
| **İkinci kapı** (seçilen) | Beyanın **dürüstlüğü** kurulum anında, **topolojisi** kalkışta sınanıyor. Bedeli iki yerde bakmak, ve bir kapının ne zaman koştuğunu bilmeyen okuyucunun ikisini tek sanması. |

**Seçilen: ikinci kapı.** Gerekçe, iki sorunun **farklı bilgiye** ihtiyaç
duyması: dürüstlük yapılandırmadan okunuyor, topoloji işletim sisteminden.
Birini diğerinin anına taşımak, bilgiyi olmadığı yerde aramak demekti.

Kanca `IHostedLifecycleService.StartedAsync` — **bütün** `StartAsync`'ler
bittikten sonra koşan aşama, yani Kestrel çoktan bağlanmış oluyor. Kayıt sırasına
bağlı değil ve bu ölçüldü (`Gercek_kestrel_dinleyici_adresini_kalkista_bildiriyor`:
0 numaralı porta bağlanan bir host, kancanın gördüğü adreste **gerçek** portu
taşıyor).

**Seçimin ölçülen bedeli:** reddedilen bir topoloji sokete hiç açılmamış
olmuyor; açılıp milisaniyeler içinde kapanıyor (istisna `app.RunAsync()`'ten
çıkıyor, süreç sıfırdan farklı bir kodla ölüyor). O pencerede gelen bir isteğin
`/mcp`'ye ulaşması için ayrıca kimlik doğrulamasından geçmesi gerekiyor.
Pencereyi sıfırlamanın yolu bağlamadan **önce** adresi bilmek, o da
yapılandırmayı **tahmin etmek** demekti (`ASPNETCORE_URLS`, `--urls`,
`Kestrel:Endpoints`, `UseUrls`, `launchSettings`) — tam olması gereken bir liste,
ve bu depo eksik listelerin bedelini beş kez ödedi.

## 3 · Kapının kuralı: joker bağlama KANIT DEĞİL

En önemli tasarım kararı burada ve **yanlış pozitiften kaçınmak** için verildi.

Container'da `0.0.0.0` **normal ve doğru**: alternatifi yok, container'ın IP'si
imaj yazılırken bilinmiyor. Yani *"joker ⇒ herkese açık"* diyen bir kapı sevk
edilen yapılandırmayı kırmızı yakardı — ve bu depoda yanlış pozitifin bedeli
yazılı: *bir bekçinin yanlış pozitifi, kırmızı yanmamasından farklı bir tehlike
— insanlar onu susturmayı öğreniyor.*

Tersi de yanlış: *"container'da normaldir ⇒ iç ağ"* sessiz yanlış olurdu.

Kapı **üçüncüyü** seçiyor: joker bağlamanın dışa açık olup olmadığı süreç
içinden **bilinemiyor** — cevabı container ağı, publish kuralı ve ana makinenin
güvenlik duvarı veriyor, üçü de bu sürecin göremediği yerde. O yüzden kapı
**yazılı bir gerekçe** istiyor. Kalıp M06'nın stdio kararının aynısı: mekanik
çıkarımı **beyanla** değiştirmek.

Üç hâl ayrı adlarda duruyor ve ayrı olmaları §8'in *"bir gün kapanacak ile hiç
kapanmayacak aynı listede duramaz"* kuralının aynısı:

| Sonuç | Ne demek |
| --- | --- |
| `Verified` | Çözülen **her** adres yönlendirilemez — beyan topolojiyle destekli |
| `Exempt` | Kanıtlanamadı ama yazılı gerekçe var; gerekçe koşum kaydına giriyor (**uyarı** seviyesinde) |
| `Rejected` | Kanıtlanamadı ve gerekçe yok → sunucu kalkmıyor |
| `NoListener` | Bakacak bir adres yok. **Yeşil değil**, uyarı — `TestServer`'ın hâli |
| `NotApplicable` | Beyan `internal` değil; topoloji şartı yalnızca o iddianın bedeli |

Adres sınıfı ölçütü **ikinci kez yazılmadı**: T42'nin
`ModelBoundaryGate.YonlendirilemezMi` yüklemi çağrılıyor (§9). İki kopya,
birinin `172.16/12` sınırını düzelttiği gün diğerinin yanlış kalması demekti.

## 4 · Muafiyet: bir tane, gerekçeli, sayısı çivili

Sevk edilen tek muafiyet **imajın içinde**, joker bağlamanın **hemen altında**:

```dockerfile
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Mcp__ListenerBoundaryOverrideReason="Konteyner agi: 0.0.0.0 yalnizca …"
```

Yeri bilinçli: bağlamayı değiştiren kişi gerekçeyi de görüyor.

Bekçi (`Sevk_edilen_muafiyet_baglamasina_bagli_ve_sayisi_civili`) **iki yönlü**
ve asıl değeri ikinci yönde:

- `Dockerfile` joker bağlıyorsa gerekçe **zorunlu**,
- joker bağlamıyorsa gerekçe **yasak**.

Tek yönlü olsaydı, bir gün bağlama loopback'e çekildiğinde gerekçe dosyada kalır
ve *"bu kurulum muaf"* diye okunmaya devam ederdi. Muafiyetin kendi
gerekçesinden uzun yaşaması bu depoda ölçülmüş bir olay (T53'ün
`KnownDivergence` girişleri, T44'ün *"belge yok"* → *"belge var ve çelişiyor"*
kayması).

Sayı `ExpectedExemptCount = 1` ile çivili: ikinci bir muafiyet **iki ayrı
bilinçli hareket** gerektiriyor (T42/T48 kalıbı).

## 5 · Bu kapının TUTAMADIKLARI

Yazılı olmayan hâli, kapı varmış gibi okunan **olmayan bir kapı** olurdu.

- **Ters vekil arkasındaki `127.0.0.1`'i göremiyor** — kapının en önemli kör
noktası. Yalnızca loopback dinleyen bir sunucu, önünde dışa açık bir nginx varsa
**herkese açıktır** ve kapı ona `Verified` diyor. Kapatılamıyor: vekilin varlığı
ve dinlediği arayüz bu sürecin bilgi alanında değil. Kapatmaya çalışmak
*"`X-Forwarded-For` görüyorsam vekil vardır"* gibi bir **sezgi** yazmak olurdu ve
susturulmayı öğrenen bekçiyi üretirdi.
- **Ağı değil adresin sınıfını görüyor.** RFC1918 bir adres kurumun ağı olmak
zorunda değil; VPN, tünel, paylaşılan bir bulut segmenti de o uzayda durur.
T42'nin aynı beyanı.
- **Yalan söyleyen bir yöneticiyi tutmuyor.** Gerekçeyi yazan kişi kurumun
kendisi (K10, K16). Kapının işi kararı imkânsız kılmak değil, **kazara**
olmasını imkânsız kılmak.
- **MCP'nin beyanı yüzünden koşuyor ama sürecin tamamını ölçüyor.** Tek Kestrel
var: `/mcp` ile diğer 45 uç aynı adresleri dinliyor ve kapı ikisini ayırt
edemiyor. Sonucu bilinçli kabul edildi — MCP `internal` beyan ettiği an ürünün
dinleyicisi de o beyanın kapsamına giriyor.
- **Kalkıştan sonra değişen bir bağlamayı görmüyor.** Ölçüm bir kez yapılıyor.
Bugün kurbanı yok (`UseUrls` dinamik bir kaynağa bağlı değil), yarın olabilir.
- **`publish` kuralını göremiyor** ve göremeyeceği yazılı: `0.0.0.0:8080`'in
ana makinede hangi arayüze çıktığı compose'un kararı. Kapının verdiği şey o
kararın **yazılı bir gerekçesi olduğu**, doğru olduğu değil.

## 6 · Kabul kriterleri

1. Dinleyici adresi kapının görüşünde — ✅ `IServerAddressesFeature`,
`StartedAsync` aşamasında, ve o aşamanın adresi bildiği **ölçüldü**.
2. Joker bağlama gerekçesiz geçmiyor — ✅ gerçek Kestrel `0.0.0.0:0` üzerinde
kalkmıyor, aynı bağlama gerekçeyle kalkıyor (kontrol çifti).
3. Muafiyet gerekçeli, sayısı sabitle çivili, ve **bağlamasına bağlı** — ✅ iki
yönlü bekçi.
4. Kapı üretimin kaydında **bağlı** — ✅ `AddBizigoMcp` onu kuruyor; T50'nin
*var olmak ile bağlı olmak* ayrımı.
5. Kapının tutamadıkları sınıfın üstünde ve bu belgede yazılı — ✅ altı kalem.
6. Bekçilerin kırmızı yanabildiği ölçüldü — ✅ yedi kusur,
`tools/m11-kirmizi-olcumu.py`.

Bitti tanımı: **6. madde** (*MCP sunucusu ağ sınırını beyan ediyor*) M06'da
kapandı; M11 o maddenin **beyandan** ibaret olmadığını ekliyor.

## 7 · Yapılmayanlar ve açık kalanlar

- **Diğer 45 ucun kendi sınır beyanı yok.** Kapı MCP'nin beyanı yüzünden koşuyor;
ürünün geri kalanı için *"bu dinleyici nerede"* sorusu hiç sorulmuyor. Bugün
tesadüfen aynı soket olduğu için kapsam örtüşüyor — ayrı bir dinleyici açıldığı
gün örtüşme biter. **Aramadım:** ayrı bir uç açan bir yol var mı.
- **Compose'un publish kuralı ölçülmedi.** `docker compose --profile api up` ile
kalkan konteynerin gerçekten muafiyeti okuduğu ve kaydın `Exempt` bastığı
**koşturulmadı** (§2: Docker koordinatörün). Kod okundu, ölçülmedi.
- **`0.0.0.0` dışındaki joker biçimleri gerçek Kestrel'de görülmedi.** `+` ve
`*` birim testinde ölçüldü, gerçek bağlamada değil — `UseUrls("http://+:0")`
Kestrel'de geçerli ama testte kullanılmadı.
- **Entegrasyon testi yazılmadı ve sebebi yazılı** (§2'nin ekseni *"konteyner
gerekiyor mu"*): bu kapının ölçülmesi için konteyner gerekmiyor, gerçek Kestrel
döngüsel arayüzde 0 numaralı porta bağlanıyor. Konteyner isteyen tek kalem
yukarıdaki compose koşumu ve o bir **test** değil bir yığın koşumu.

## 8 · M11'in yan bulgusu: M06'nın bir bekçisi yanlış sebeple yeşil

M11'in ölçüm aracı `tools/m06-kirmizi-olcumu.py` ile aynı yordamı paylaşınca
(§9, ortak yüzey) M06'nın ölçümü yeniden koşturuldu ve **bir kusur kırmızı
yakamadı**:

> `K6 kapısı sunucu kurulumundan çıkarılıyor` →
> `Kurum_disi_beyan_sunucu_kurulumunda_reddediliyor`: **YEŞİL KALDI**

Yani `BizigoMcpServer.Apply`'den `McpBoundaryGate.Require` satırı silindiğinde o
test hâlâ geçiyor. Sebep ölçüldü — geçici bir testle, `Internal` beyanıyla (yani
kapı sessizken) aynı çağrı yapıldı:

```
InvalidOperationException — MCP aracı `Bizigo.Commands.Mcp.FieldsCoverageTool`
kurulamadı: Unable to resolve service for type 'Bizigo.Commands.ParserToolbox'…
```

Test boş bir `ServiceProvider` veriyor ve yalnızca **istisna tipini** sınıyor.
M02/M04 DI'ya bağımlı araçlar ekledikçe o çağrı **her hâlde**
`InvalidOperationException` fırlatmaya başladı; yani testin yeşilliği artık K6
kapısı hakkında hiçbir şey söylemiyor. §7'nin *"bir bekçinin sessizce atlaması,
bekçinin kendisinden tehlikelidir"* sınıfı ve T48'in `GET /v1/probe` kontrol
satırının aynısı.

**Düzeltilmedi ve bilerek:** `McpComplianceTests.cs` M06'nın dosyası ve M07 aynı
bölgede canlı. Koordinatöre bildirildi; düzeltmenin şekli de ölçülmüş durumdan
çıkıyor — ya mesajın yüzeyi/K6'yı adlandırdığı sınanmalı, ya araçları
kurabilecek bir sağlayıcı verilmeli. İkisi farklı şeyi ölçüyor.

Bu bulgu M11'in kapsamı dışında ama **bu belgede duruyor**: bir ajan raporunda
kalsaydı bir sonraki kişi aynı ölçümü ikinci kez yapardı.

## Bağımlılıklar

M06 (kapının kendisi) ve T42 (adres sınıfı yüklemi). M07 ile **kesişiyor**:
`BizigoMcpServer.Apply` M07'nin dosyası ve M11 ona dokunmadı — kapının ikinci
olmasının üçüncü gerekçesi bu.
