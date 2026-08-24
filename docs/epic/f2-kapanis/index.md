---
kind: spec
title: "F2 kapanışı — görünürlük sevk edildi"
---

# F2 kapanışı

F1 boru hattını kurdu; F2 ona bir yüz verdi. Arayüz, parser editörü ve yayın
akışı, alarm motoru ve bildirimler, değişiklik beslemesinin üç kaynağı, ve
hepsinin üstünde bir doğrulama turu.

Bu belge **kodun bugünkü hâlini** anlatıyor. Bir başarı raporu değil: F1'in
kapanışı işe yaradıysa kendi kırıklarını saydığı için yaradı, ve bu turda ona
defalarca o yüzden dayandık. F3'e başlarken okunacak belge budur.

`main` = `962ccae` · 18 proje 0 uyarı · **740** birim testi (3 atlandı) ·
**247** UI testi · **96** entegrasyon testi · `api:check` birebir · `tsc` temiz.

## 1 · Ölçülen kısıtlar

Bunlar tercih değil **kısıt**: F3 bunlara dayanacak ve tahmin edilmiş olsalar
yanlış yere dayanmış olurdu.

### Kimlik — canlı Keycloak'a karşı

Realm dosyası `clientScopes` verdiği için Keycloak **yerleşik scope'ları hiç
oluşturmuyor**. Keşif belgesi
`scopes_supported = ["openid", "offline_access", "bizigo-claims"]` diyor.

| İstenen scope | Cevap |
| --- | --- |
| `openid` | geçiyor |
| `openid profile` | `invalid_scope` |
| `openid profile email` | `invalid_scope` |

F1'den kalma OIDC işleyicisi **üçüncü satırı istiyordu**. Tarayıcı akışı sevk
edilseydi ilk kullanıcının ilk girişinde patlardı ve hiçbir test bunu görmezdi:
akış hiç koşulmamıştı. K31'in (BFF'i Next'e taşıma) gerekçesi böylece kaynağında
doğrulandı.

Aynı koşumda ölçülen diğer şeyler: oturum çerezi **opak** (JWT değil), BFF'in
altı yanıtının hiçbirinde token yok, yukarı akışta var, sahte çerez 401, ve
K17'nin kapalı-başlaması **sessiz değil** — eşleme tablosu boşken kullanıcı
giriyor ama ekran sebebini söylüyor.

### Sıcak yol maliyeti (K35) — ve ölçümün kirlendiğinin kanıtı

İki koşum: **1,46×** ve **1,62×**.

İkinci koşumda *yalnız ayrıştırma* kolu *ayrıştırma+etiketleme*'den **yavaş**
göründü — fiziksel olarak imkânsız, çünkü ikincisi birincinin üstüne iş
ekliyor. Yani makine sessiz değildi ve sayı kirliydi.

**Bu, sayının kendisinden değerli bir bulgu.** Tek bir rakam seçilseydi hangisi
olduğu bilinmezdi; iki koşumu da kaydetmek, ölçümün ne zaman güvenilmez
olduğunu görünür kılıyor. F3'ün ölçüm kuralı buradan geliyor: **bağlayıcı sayı
sessiz makine ister**, ve imkânsız bir sıralama gördüğünde ölçüm atılır.

### Sigma — sayı var ama kullanılamaz, ve sebebi veri

`compiled=24, runs=14` · `match_ratio = %0`.

Eşleşme oranı sıfır çünkü ClickHouse'daki 1M satır **tek vendor'lu sentetik
benchmark verisi**, altın örnek değil. Aracın ön kontrolü "tablo boş mu" diye
soruyordu, "doğru veri mi" diye değil — sonradan altın örnek sondası arayacak
şekilde sertleştirildi.

**Veriden bağımsız tek kullanılabilir sayı:** on kural var olmayan kolonlara
giden SQL üretiyor. Bu, kapsam daraltarak çözülmez; F3'ün derleme adımının
cevaplaması gereken bir soru.

### Arayüzü bağlayan iki F1 ölçümü hâlâ geçerli

Tam metin indeksi ~10-11 karakterden sonra seçici, ve keyset sayfalama ancak
`owner_group` + `source_id` verildiğinde sabit süreli. İkisi de ekranda
karşılığını buldu: kısa sorgu uyarısı ve kaynak filtresi teşviki. Alarm
bağlantısının kaynak taşıması da bu ölçümden geliyor, kolaylıktan değil.

### Entegrasyon paketi yerelde koştu

**93/93.** Bugüne kadar yalnızca CI koşuyordu — yani paketin yeşilliği tek bir
ortamın yeşilliğiydi.

## 2 · Yanlış çıkan iddialar — bu fazın en değerli çıktısı

Altı kusur çıktı ve **altısı da aynı şekle sahip**:

> Belgelenmiş bir iddianın kodda karşılığı yok, ve sonuç sessizce yanlış.

| # | İddia | Gerçek |
| --- | --- | --- |
| 1 | "`REPLACE PARTITION` atomik, replay canlı ingest'i bozmuyor" | Anlık görüntüden sonra o bölüme yazılan her satır **siliniyor** |
| 2 | "Ekran kuralı kimliğinden okuyor" (`AlertLinkBuilder`) | Hiç okumuyor; alarm işaret ettiğinden **geniş** bir ekran açıyor |
| 3 | "Gizli değerler maskeleniyor" (T26) | Maskeleniyor, ama sır **bölüm adının içinde** kalıyordu |
| 4 | "İki Jenkins fazı aynı olguyu bildiriyor" | Post-build adımı durumu çevirebiliyor; erken ve **yanlış** durum kalıcı oluyordu |
| 5 | "Kimlik grafiği kapsam doğrulamasından geçiyor" | `AddBizigoAuthentication` bekçinin listesinde **hiç yoktu** |
| 6 | "`UNMAPPED_FIELDS` eşlenemeyen alanları koruyor" | Tanımlıydı, **hiçbir yere bağlı değildi** |

**Hiçbiri koşturarak bulunmadı.** Altısı da okurken ya da o yorumun doğru
söyleyip söylemediğini soran testi yazarken çıktı. F1'in dersi
*"doğrulanmamış her katman kırıktı"* idi; F2'nin dersi bir kademe ötesi:

> Bir yorumun bir şeyi iddia etmesi, kodun onu yaptığı anlamına gelmiyor — ve
> ikisi ayrıştığında **belirti üretmiyor**.

Yeni bir bekçi yazarken sorulacak soru *"bu davranış doğru mu"* değil,
**"bu yorumun söylediği şey gerçekten oluyor mu"**.

### İkinci kalıp: elle tutulan liste bekçiyi körleştiriyor

Aynı şey **beş kez** oldu ve her seferinde bekçi yeşil yandı:

| Nerede | Ne göremedi |
| --- | --- |
| `Produces<T>` kapısı | Elle `Map*` listesi — **16 uç** kapıya hiç görünmedi, üç test de geçti |
| Ömür bekçisi | Elle `Add*` listesi — kanıt katmanı görünmedi |
| Ömür bekçisi | Aynı liste **doğduğu gün** eksikti: kimlik grafiği hiç denetlenmemişti |
| Ömür bekçisi | `AddBizigoDiscovery` başka bir uzantının içinden çağrılıyor; elle listeye girmesi akla gelmezdi |
| Kapı, ikinci yarı | Uç **bulunuyordu ama çağrılamıyordu**; bağımlılığı kayıtlı olmayan `Map*` patlıyor ve o dosya yine denetlenmemiş kalıyordu |

Çözüm her seferinde aynı yöne gitti: **denetlenen kümeyi yansımayla bul.** Elle
kalan tek şey artık *denetlenen* küme değil *beklenen* küme —
`ExpectedExemptCount` gibi, ve o da tek bir sayı.

### Üçüncü kalıp: doğrulama listesinden düşen kapı, kapı olmaktan çıkıyor

T26 indikten sonra `api:generate`/`api:check` **kırıldı ve kimse görmedi**:
bir singleton scoped bir bağımlılık alıyordu ve belge üretimi `Main`'i
gerçekten çalıştırdığı için tek gerçek DI doğrulaması oydu. Kusur ancak başka
bir dalın birleştirmesinde, ona hiç dokunmamış birinin gözünde görüldü.

İkinci ders daha önemli: **bir kusurun yalnızca tesadüfen görülebilir olması,
kusurun kendisi kadar ciddi.** Kapsam doğrulaması artık her birim testi
koşumunda.

## 3 · Kapanmayan kalemler — ve neden

| # | Risk | Neden kalıcı |
| --- | --- | --- |
| B7 | Next oturum deposu bellek içi | Çok kopyaya çıkana kadar gerçek bir sorun değil; Redis dağıtım kararı, kod kararı değil |
| B8 | Keşif belgesi süresiz önbellekli | Kısaltmak her isteği Keycloak'a bağlar. İşletim kuralı olarak yazılı |
| B9 | `GetDocument.Insider` adına bağımlılık | Ayrımı ortam değişkenine bağlamak **daha kötü**: bayrak üretime taşınabilir ve göçleri sessizce atlayan bir API bırakır. Araç adı değişirse belge üretimi kırmızı yanıyor — hatanın doğru yönü |
| B14 | Şema tamamlama listesi motorun kopyası | Bedeli "öneri eksik görünüyor" — sessiz yanlış davranış **değil**. Sunucudan çekmek tamamlamayı her tuşta ağ isteğine bağlardı |
| B16 | Worktree `node_modules` bayatlığı | Ajanlar ayrı worktree'lerde çalıştığı sürece yapısal. Bu turda **üç yerde** aynı duvara toslandı; azaltılabilir, yok edilemez |
| B17 | `docs/epic` iki yazarlı | Yön düzeltildi (depo → epic) ama iki ajan aynı belgeyi paralel güncellerse çakışma kaçınılmaz. Koordinatör tek yazar atamalı |

**B9 ve B14 bilinçli olarak kabul edildi**: ikisinin de alternatifi daha
sessiz bir kusur üretiyordu. Bir riski kabul etmek onu unutmak değil; bu tablo
o yüzden var.

## 4 · Bekçilerin durumu

Bu fazın kalıcı çıktısı ekranlar değil **bekçiler**. Ekranlar değişecek;
bekçiler bir sonraki kusuru yakalayacak.

| Bekçi | Kümesini nasıl buluyor | Kırmızı yandığı ölçüldü mü |
| --- | --- | --- |
| `ProducesContractTests` — uç kapısı | **Yansıma** (uzantılar + rotalar) | ✅ `Produces<T>` kaldırılarak |
| `ArchitectureTests` — ömür/DI grafiği | **Yansıma** (uzantılar + derlemeler, geçişli) | ✅ |
| `ExpectedExemptCount` | Elle — ama **tek sayı**, ve büyümesi görünür bir karar | ✅ |
| `alert-criteria-bridge` | `PARAM` anahtarlarından türüyor | ✅ |
| `AlertLinkTargetTests` | Dosya sisteminden: rota var mı, `PARAM` ne diyor | ✅ maske ve `eksik` bekçileri |
| `changes-screen` — kimlik bilgisi maskesi | Saf fonksiyon | ✅ maske kontrolü kaldırılarak |
| `ui-consistency` / `contrast` | Jetonlardan türüyor | koordinatör turunda |
| `token-isolation` | Yanıtın **her baytını** tarıyor | ✅ |

**Elle beslenen liste kalmadı** — `ExpectedExemptCount` dışında, ve o zaten
denetlenen kümeyi değil beklenen sayıyı tutuyor. Bu ayrım F2'nin en pahalı
dersiydi.

Bir uyarı: **bekçinin kendisi de bayatlayabiliyor.** `Pending`'de replay'in
kaldığını sabitleyen test, liste *kapanış yönünde* değişince kırmızı yandı.
Doğru davranış, ama devralan bilmeli: sayıyı sabitleyen bir test, sayı
düzeldiğinde de düşer.

## 5 · F3'e devredilen sorular

| Soru | Neden burada cevaplanamadı |
| --- | --- |
| **Baseline penceresi ne kadar olmalı?** | Araç en az taban süresi kadar gerçek geçmiş istiyor ve bunu **kendisi söylüyor** — sessizce anlamsız sayı üretmiyor. Altın örnek yükleyicisinden sonra ölçülecek |
| **Sigma kapsamı gerçekte ne?** | `match_ratio` verinin yanlışlığından sıfır çıktı. On kuralın var olmayan kolonlara SQL üretmesi ise **veriden bağımsız** ve kapsam daraltarak çözülmez |
| **`Weight` nasıl normalleştirilecek?** | Kanıt sağlayıcıları ağırlık taşıyor ama ölçek tanımlı değil; iki sağlayıcının "yüksek"i aynı şey mi, ancak üçüncüsü gelince belli olur |
| **Kuru koşu gerçek çalıştırmayla aynı sonucu veriyor mu?** | İskelet ve numaralı adımlar yazıldı, `Skip` ile duruyor. Postgres manifest satırları **ve** S3 nesneleri istiyor |
| **Replay canlı ingest'i bozmuyor — artık doğru mu?** | Açık bölüm dışarıda bırakıldı ve atlandığı rapora yazılıyor. Yük altında ölçüm hâlâ yapılmadı; kapatma **yapısal**, ölçümle değil |

## 6 · F2'nin bitiş şartı: `Pending` boş

T27'nin kabul kriteri *"bekleme listesi boşalmadan F2 bitmiş sayılmaz"*
diyordu. Liste 21 satırdan **sıfıra** indi ve her satır, o ucu tüketen ekranla
birlikte gitti — çünkü tüketicisi olmadan yazılan bir yanıt tipi tahmindir.

Son satır `POST /v1/replay`'di ve **sahipsizdi**: replay ekranının ticket'ı hiç
açılmamıştı. Kararı iki seçenek arasında verildi ve ikisi de "tiplendir" dedi:

- **Muafiyete taşımak yanlış olurdu** — muafiyet "hiç tüketicisi olmayacak"
demek ve bunu kimse söyleyemez.
- **Tiplendirmek "tahmin" değildi** — uç zaten `ReplayReport`'u döndürüyordu,
yani domain tipi fiilen tel sözleşmesiydi. Bu, tiplenmemiş olmaktan **kötü**:
tip yok ama sızıntı var, üstelik sessiz.

`Izin_listesi_bosaldi_mi` listeyi boş tutuyor. Yeni bir satır eklemek serbest
ama bedeli görünür: o test kırmızı yanıyor ve F2'nin kapandığı iddiası düşüyor.

### `Pending` bitiş şartlarından **biriydi**, tamamı değil

Bu bölüm bir kez yazıldı ve **eksik yazıldı**: T27'nin kabul kriterleri
`Pending`'in yanında dört uçtan uca akış ve iki çapraz doğrulama da istiyordu,
ve bu belge onlara hiç değinmiyordu. Eksiklik T27 taramasında bulundu
([tarama](../t27-kapanis-taramasi/index.md)).

Bugünkü durum:

| Bitiş şartı | Durum |
| --- | --- |
| `Pending` boş, `Exempt` sabit | ✅ |
| Replay sırasında canlı ingest bozulmuyor | ✅ ölçüldü — ve F1'in varsayımı **yanlış** çıktı (§2'ye eklendi) |
| Kuru koşu = gerçek koşu | ✅ |
| 4 uçtan uca akış | ⚠️ **ikisi akış, ikisi parça** — parser ve değişiklik akışları var; giriş→arama→detay→ham bayt ile sussun→alarm→bildirim yalnızca parça hâlinde |
| Kapsam ayrışması bekçisi | API'de ✅ · ekranda ✅ (T27'de eklendi: sunucuda kapsamlı veri çizen sayfa önbelleklenemiyor) |
| Token sızıntısı bekçisi | yanıt/çerez ✅ · `localStorage` ✅ (T27'de eklendi) |

**Gözlem, öneri değil:** bir kapanış belgesinin *"fazın bitiş şartı"* bölümü
eksik olduğunda, belge okuyanı **"faz bitti"** diye bırakıyor — ve bu belge tam
olarak onu yaptı. Kapanış belgelerinin ticket'ın kabul kriterlerini tek tek
karşılayan bir kontrol listesi taşıması gerekiyor olabilir. Kaydı burada
duruyor.

## 7 · Bilerek yapılmayanlar

| Ne | Neden |
| --- | --- |
| Cihaz config için REST/SNMP taşıması | Üç vendor da SSH konuşuyor. Üç yöntemi tek soyutlamaya baştan sıkıştırmak erken genelleme olurdu; yüzey iki somut vendor yazıldıktan sonra çıkarıldı |
| Ham config yedekleme | Bu ürün config yedeklemiyor. Kopya tutulduğu an saklama, erişim ve sızıntı sorumluluğu doğar; RCA'nın ihtiyacı "ne değişti" |
| Cihaz connector'ı için ayrı ekran | T25'in formu tipi zaten sunuyor; ikinci bir ekran aynı alanları iki yerde tutmak olurdu |
| Alarm bağlantısının kural kimliğini çözmesi | Bağlantı bildirime gömülüyor, kullanıcı günler sonra tıklıyor — kimliği çözen ekran **bugünkü** kuralı gösterirdi. Filtreler bağlantıya gömüldü |
| Bölüm içi satır taşınmasını fark saymak | Bildirimsel config'te sıra anlam taşımıyor ve cihazlar yeniden yazımda sırayı değiştiriyor. LCS her `write mem`'de yüzlerce sahte değişiklik üretirdi |

## 8 · F3'ün ilk gününde okunacaklar

1. **§2'nin altı satırı.** F3'te yeni bir katman yazarken, o katmanın yorumları
ne iddia ediyorsa onun testi yazılmalı — davranışın testi değil, **iddianın**.
2. **§1'in K35 paragrafı.** F3 ölçüm ağırlıklı bir faz; imkânsız bir sıralama
görüldüğünde ölçüm atılır.
3. **§4'ün son uyarısı.** Yeni bekçi yazan, kümesini yansımayla bulsun; elle
liste bu fazda beş kez kör çıktı.
4. **§5.** Beş sorunun beşi de F3'ün ilk turunda planlanmalı — hiçbiri
kendiliğinden kapanmıyor.
