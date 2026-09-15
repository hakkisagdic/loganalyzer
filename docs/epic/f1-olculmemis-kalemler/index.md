---
title: "F1'in ölçülmemiş kalemleri"
kind: spec
---

# F1'in ölçülmemiş kalemleri

F1 **kapandı** ve bu belge onu geri almıyor. Yaptığı şey tek: kapanmış bir fazın
hangi iddialarının **ölçülmemiş** olduğunu görünür kılmak.

Varlık sebebi ölçüldü. 2026-09-14/15'te F1'in kabul kriteri tablosu kriter kriter
tarandı ve **iki gün arayla iki kez** aynı sınıf bulundu: bir kriter *"karşılandı"*
diye duruyordu ama karşılandığını gösteren bir koşum yoktu. Bir kez daha tek tek
keşfedilmesin diye envanter burada duruyor.

> **Bir kriterin "karşılandı" diye durması, karşılandığının ölçüldüğü anlamına
> gelmiyor.**

## Nasıl okunacak

Her kalem üç soruya cevap veriyor: **bugün ne ölçülüyor**, **ne ölçülmüyor**,
**ölçmek neyi gerektiriyor**. Belge bir **envanter**; kalemleri ölçmeye
çalışmıyor ve değeri **tam** olmasında.

Kriter metinleri `docs/epic/f1-teknik-plan/index.md`'de. İki kriter
(**parse doğruluğu**, **dayanıklılık** dışındaki hâliyle) bu listede yok çünkü
ölçülüyor:

| Ölçülen kriter | Nerede |
| --- | --- |
| Parse doğruluğu — 4 vendor, altın örneklerin %100'ü | CI, her PR: `parser lint` + `parser test catalog/parsers` (`ci.yml:82,87`). Vendor sayısı bugün de **4**: `cisco.asa`, `fortinet.fortigate`, `mikrotik.routeros`, `nginx.access` |
| Ham sadakat — **depolama katmanı** yarısı | `F2FlowTests.Coklu_alfabeli_govde_depolamada_bozulmuyor`, `F2ChainTests`, `RawEventLocatorTests` |

CI entegrasyon paketini gerçekten koşuyor (`ci.yml:429`, servisler `:317`) — yani
aşağıdaki kalemler *"entegrasyon testi koşmuyor"*dan **değil**, o testlerin
iddiayı ölçmemesinden doğuyor.

---

## 1 · "**Her** olay ham baytına geri götürülebilir" — evrensel iddia, örneklem kanıtı

| | |
| --- | --- |
| **Kriter** | Ham sadakat: *"Her olay ham baytına geri götürülebilir; byte-for-byte doğrulanır."* |
| **Bugün ölçülen** | Belirli olayların ham baytına inilebildiği: `F2ChainTests.Arama_detay_ham_bayt_ayni_olayi_tasiyor` (arama → detay → arşiv zinciri), `RawEventLocatorTests`, `RawArchiveTests` (yükle → geri oku → sha256). |
| **Ölçülmeyen** | **`her` kelimesi.** Örneklem bir evrensel iddiayı **doğrulayamaz**, yalnızca yanlışlayabilir. Bugünkü bekçiler *"bu olay geri götürülebildi"* diyor; kriter *"her olay geri götürülebilir"* diyor. |

**Ölçmek neyi gerektiriyor.** Örneklemi büyütmek **yanlış cevap** — 10 olay da,
10 000 olay da evrensel bir iddiayı kanıtlamıyor. Doğru kapanış bir **değişmez**:
*ham referansı olmayan bir olayın yazılamaması.* O da bir test değil bir kapı —
`EventWriter` yolunda `raw_ref`'i boş bir olayın reddedilmesi, ya da tip düzeyinde
imkânsız olması.

Bugünkü hâlin bilinen gevşekliği yazılı: `raw_ref` **bayt konumu değil arşiv ön
eki** taşıyor, çünkü ingest ile arşiv yükleyici bilinçli olarak bağımsız çalışıyor
ve olay yazılırken nesne henüz yok. Yani *"referans var"* ile *"nesne var"*
arasındaki boşluğu kapatan şey manifest ve scrub, yazma anındaki bir kontrol
değil.

---

## 2 · Çok dilliliğin "**aranır**" yarısı — bu turda bulundu

| | |
| --- | --- |
| **Kriter** | *"Türkçe/Arapça/Çince gövdeli log doğru kodlamayla **saklanır ve aranır**."* |
| **Bugün ölçülen** | **Saklanır**: `F2FlowTests.Coklu_alfabeli_govde_depolamada_bozulmuyor` üç alfabeli gövdeyi yazıp `body`'nin sha256'sını karşılaştırıyor. **Zincir**: `F2ChainTests` aynı çok alfabeli satırı arama → detay → ham bayt boyunca taşıyor. |
| **Ölçülmeyen** | **Çok dilli metnin kendisiyle arama.** `F2ChainTests`'in sorgusu yalnızca **zaman penceresi** taşıyor (`EventQuery { From, To }`) — hiçbir metin yüklemi yok. Yani ölçülen şey *"çok alfabeli gövdeli bir olay aramada dönüyor"*; ölçülmeyen şey *"`المستخدم` ya da `用户登录失败` aranarak bulunuyor"*. |

**Bu ayrımın neden önemli olduğu bu depoda ölçülmüş bir tuzağa dayanıyor:**
Türkçe kültür tuzağı. `tr-TR`'de `ToLower()` `I` harfini `ı` yapıyor ve
`INTERFACE` gibi kelimeler aramada **sessizce** eşleşmiyor — `.editorconfig`
CA1304/1305/1307/1310/1311/1862'yi **hata** seviyesinde tutuyor tam bu yüzden.
Yani kriterin *"aranır"* yarısı, ürünün bildiği en sinsi arıza sınıfının tam
üstünde duruyor ve orada bekçi yok.

**Ölçmek neyi gerektiriyor.** Çok alfabeli gövdeye **metin yüklemiyle** vuran bir
arama: büyük/küçük harf duyarsız Türkçe eşleşme (`İ`/`ı` çifti dâhil), Arapça
sağdan sola metin, ve CJK'de kelime sınırı olmadığı için alt dize eşleşmesi. Üçü
farklı kırılıyor, yani üç ayrı iddia — tek bir test değil.

> ⚠️ **Bu kalem, adı iddiasından geniş bir testin ikinci örneği.** İlkinde aynı
> dosyadaki test T27'de **yeniden adlandırıldı**: eski adı
> `Ham_bayt_sadakati_zincir_boyunca_korunuyor`'du ve okuyan *"arama → detay → ham
> iniş doğrulanmış"* sanıyordu; gövdesi ise yalnızca `body`'nin sha256'sını
> okuyordu. O gün iddia sessizce düşürülmedi, zincir **gerçekten yazıldı**
> (`F2ChainTests`). Bu kalemin doğru kapanışı da aynı biçimde: ya arama ölçülür,
> ya kriter *"saklanır"*a daraltılır — ama sessizce değil.

---

## 3 · "7 günlük veri" — birim düzeltildi, **hacim** yorumu açık kaldı

| | |
| --- | --- |
| **Kriter** | Replay: *"7 günlük veri, düzeltilmiş parser sürümüyle yeniden işlenir; eski satır kalmaz."* |
| **Bugün ölçülen** | Replay'in kendisi: `F2FlowTests` (kuru koşu = gerçek koşu, açık bölüm ayrımı), `ReplayStoreTests` (gölge tablo, `REPLACE PARTITION`), `ReplayDiffTests`, `ReplayOpenPartitionTests`. |
| **Ölçülen (M19)** | **Gün sayısının ne anlama geldiği.** `events` tablosu `PARTITION BY toYYYYMMDD(ts)` → **gün = bölüm**, ve `ReplayEngine` bölüm başına dönüyor (`:270`, `:301`). Sonuç: **1 gün ile çok gün farklı kod yolları** (tek gün bugünse açık bölümdür ve replay hiçbir şey uygulamaz), ama **7'nin özel bir yanı yok** — 2 gün de aynı yolları koşuyor. Kriter bu yüzden *"çok bölümlü veri, açık/kapalı sınırı dâhil"* olarak yeniden yazıldı. |
| **Ölçülmeyen** | Kriterin **hacim** okuması: *"7 günlük hacim bir seferde işlenebiliyor"*. Gün sayısı bir **kod yolu** ölçüsü olarak anlamına çekildi; bir **kapasite** ölçüsü olarak hiç ölçülmedi. |

**Ölçmek neyi gerektiriyor.** Bu bir kapasite ölçümü, doğruluk ölçümü değil —
B01–B05 ile aynı aile. Gerçek hacimde veri (`bizigo seed golden --span-days`),
bellek tavanı, ve replay'in bölüm başına ne kadar tuttuğu. Ve bir karar:
*"bir seferde"* ne demek — tek süreçte mi, tek `REPLACE PARTITION` işleminde mi.
İkisi farklı sayı verir.

---

## 4 · Dayanıklılık — `kill -9` **taklit** ediliyor, depo kesintisi hiç ölçülmüyor

| | |
| --- | --- |
| **Kriter** | *"Süreç `kill -9` ile öldürülür; ack'lenen hiçbir olay kaybolmaz. RustFS durdurulur; ingest devam eder."* |
| **Bugün ölçülen** | `WriteAheadLogTests`: **yarım yazılmış bir çerçevenin** okuyucu tarafından atlanması. Test kendi yorumunda ne yaptığını yazıyor — *"kill -9 **taklidi**: son çerçevenin gövdesi yarıda kesilmiş"* (satır 80). |
| **Ölçülmeyen** | **İki yarısı da.** (a) Süreç öldürülmüyor; ölçülen şey dosyanın şekli. Gerçek `kill -9` ile aradaki fark tam olarak **fsync'in tuttuğu yer** — işletim sistemi tamponunda bekleyen ve diske hiç inmemiş veri, yani *"ack verdik ama veri diskte değil"* hâli. (b) *"RustFS durdurulur; ingest devam eder"* yarısını ölçen **hiçbir test yok**. |

Bu ikinci yarı, ürünün en merkezî iddiasının (**ham veri her şeyden önce
gelir**) tam üstünde: dayanıklılık sınırının bilinçli olarak WAL'da olması,
depo düştüğünde ingest'in **durmadığını** varsayıyor. Varsayım ölçülmemiş.

**Ölçmek neyi gerektiriyor.** Protokolü yazıldı:
[F1-D1 — Dayanıklılık kriterinin ölçümü](../tickets-f1/dayaniklilik-olcumu/index.md).
Taşıdığı kararlar: `SIGKILL` (`SIGTERM` değil — o düzgün kapanışı tetikler ve
zaten fsync ediyor), öldürme anının ack'ten hemen sonra olması, olayın **sorguyla**
aranması (WAL dosyasına bakılarak değil) ve **sayının** karşılaştırılması, depo
kesintisinin **compose'dan** yapılması (taklit edilmemesi), ve devamın bir
**sayaçtan** okunması — *"hata log'u yok"* kanıt değil, sessizce duran boru hattı
da hata basmaz.

---

## 5 · Arşiv bütünlüğü — M19'da **kapatıldı**, ama koşum bekliyor

| | |
| --- | --- |
| **Kriter** | *"Manifest'ten silinen nesne replay'de sessizce atlanmaz."* |
| **Bugün ölçülen** | Bekçi **yazıldı** (M19): `F2FlowTests.Eksik_nesne_replayi_durduruyor` ve `Bayrak_acikken_eksik_nesneye_ragmen_devam_ediyor`. |
| **Ölçülmeyen** | **Koşum.** İkisi de entegrasyon paketinde (§2: container gerektiren testler yazılır, koşturulmaz) ve bu turda **koşturulmadı** — yeşil gösterilmedi. |

**Kalemin iki alt bulgusu, ikisi de kayda değer:**

- **Bekçi yoktu ve olduğu sanılıyordu.** Testlerdeki iki `MissingObjects` iddiası
  (`RawArchiveTests`) **scrub**'ı ölçüyor — nesne kaybının *tespitini*. Replay'in
  **durmasını** ölçen hiçbir şey yoktu. İkisi ayrı ayrı kaybedilebilir: scrub
  kaybı görürken replay onu atlarsa replay *"7 gün yerine 5 gün"* döner, üstelik
  **başarıyla**.
- **Kriterin metni yanlıştı ve düzeltildi.** Eski hâli *"…hata olur"* diyordu.
  Ölçüldü: `ReplayEngine.cs:79` istisna **fırlatmıyor**; `LogError` basıp
  `Applied = false` ve `MissingObjects` dolu bir rapor **döndürüyor** — ve bu
  **daha iyi** bir tasarım, çünkü çağıran eksik listesini görüyor, istisna
  yakalamak zorunda kalmıyor. Yanlış olan kod değil kriterdi.

---

## 6 · Kapsam — kriter **üç yol** sayıyordu, yalnızca biri kapıdan geçiyor

| | |
| --- | --- |
| **Kriter (eski)** | *"Kapsam dışı sorgu **hiçbir** yoldan (REST, replay okuma, CLI) veri döndürmez."* |
| **Bugün ölçülen** | REST: `ScopeNegativeTests`, `F2ChainTests` (kapsam dışı olayın dönmediği), `McpResourceScopeIntegrationTests`. |
| **Ölçülen (M18/M19)** | Kriterin saydığı üç yoldan **yalnızca REST** kapsam kapısından geçiyor. Replay `AccessScope.System("replay")` ile okuyor; `Bizigo.Cli` `IScopedQuery`'yi **hiç anmıyor** (`ClickHouseContext`/`EventWriter` ile doğrudan çalışıyor). |
| **Ölçülmeyen** | Artık bir boşluk **değil** — kriter ürün yüzeylerine (REST, MCP, arayüz) çekildi ve replay ile CLI'ın kapsam dışı olması **gerekçeleriyle** yazıldı. |

Bu kalem listede **kapanmış** olarak duruyor ve sebebi bir uyarı taşıyor: ikisinin
kapsam dışı olması **doğru** (replay bir sistem işlemi — kapsamlanmış bir kurtarma
arşivin yalnızca bir kısmını geri yükleyebilirdi; CLI'da kimlik yok ve olamaz),
ama **F1 kapandığından beri hiçbir koşum kriteri üç yol için sınamamıştı.** Yani
kriter yanlış yazılmıştı ve yanlışlığı iki yıla yakın görünmedi.

Gerekçeler `IScopedQuery`'nin belgesinde ve
[F1 kapsam kriteri düzeltmesi](../f1-kapsam-kriteri-duzeltmesi/index.md)'nde.

---

## Kalemlerin ortak şekli

Altı kalemin beşi **aynı** biçimde doğdu ve bu tesadüf değil:

| Şekil | Kalem |
| --- | --- |
| Kriter, bekçisinden **geniş** yazılmış | 1 (*"her"*), 2 (*"aranır"*), 4 (*"kill -9"*) |
| Kriter, ölçülene göre **yanlış** yazılmış | 5 (*"hata olur"*), 6 (üç yol sayıyor) |
| Kriterin **birimi** yanlış | 3 (gün ≠ hacim) |

Yani F1'in kabul kriterleri **kod yanlış olduğu için** değil, **metin ölçümden
önce yazıldığı için** ayrışmış. Ve ayrışmanın hiçbir yerde alarm üretmemesinin
sebebi yapısal: bir kabul kriteri tablosu çalıştırılabilir değil, yani kimse onu
koşturup kırmızı göremiyor.

Bu belgenin taşıdığı ders de o:

> **Çalıştırılamayan bir iddia, ancak birisi elle karşılaştırdığında yanlış
> çıkar.** Kapanmış bir fazın kriterleri, kapanışın kendisi tarafından
> doğrulanmıyor.

## Nereden çıktı

M18 ve M19 (`docs/epic/tickets-mcp/`). M19'un F1 taraması yedi kriteri kriter
kriter ölçtü; kriter 5 aynı turda kapatıldı, kriter 4 ticket'a döndü, kriter 3'ün
birimi düzeltildi, kalem 2 ise bu belgenin **tamlık kontrolünde** bulundu — yani
envanteri yazma işi, envantere bir kalem daha ekledi.
