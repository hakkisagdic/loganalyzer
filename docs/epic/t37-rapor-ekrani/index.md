---
kind: spec
title: "T37 — Rapor ekranı: dört durumu ayakta tutmak"
---

# T37 — Rapor ekranı ve export

`t37-rapor-ekrani` dalı. Ölçülen durum (`1ebebe8`): **18 proje 0 uyarı · 843
birim testi · 306 UI testi · `tsc` temiz · `api:check` birebir**.

> **Güncelleme (`3f634a0` sonrası).** İnceleme tablosu birleştirildi: T37'nin
> `evidence_reviews`'ü silindi, kayıt artık T38'in `golden_reviews`'ünde ve
> `state` alanı `verdict` oldu. Değerler **dört**: `correct` · `incomplete` ·
> `wrong` · `unknown`. Ayrıca `contradicting_evidence` eklendi
> (`not_present` / `sound` / `trivial` / `unknown`). Ayrıntı için T38 belgesi;
> §5 aşağıda güncellendi. **§1'in çivilenmiş değişmezi etkilenmedi.**

Bu belgenin asıl konusu tek bir şey: **arayüz, yalnızca sözcüklerde var olan
bir ayrımı yok etmekte alışılmadık derecede iyi.**

## 1 · Çivili değişmez ve nerede sınandığı

`empty` · `never_fed` · `unavailable`/`failed` · `not_registered` — beşi de
doğal olarak **boş bir kutu** gibi çizilir. Çizildiği an rapor, bakmadığı bir
şeye bakmış gibi görünür ve bunu hiçbir hata mesajı bozmaz.

Ayrım **üç ayrı yerde** sınanıyor, çünkü üçü ayrı sorular:

| Nerede | Test | Ne tutuyor |
| --- | --- | --- |
| Tel yüzeyi | `RcaFourStatesTests.Tel_yuzeyinde_dort_durum_ayirt_edilebiliyor` | Dört ayrı dizgi, `silent` ve `not_consulted` ayrı listeler |
| Export | `RcaFourStatesTests.Export_dort_durumu_ayri_etiketlerle_yaziyor` | Markdown'da dört ayrı etiket |
| Ekran | `rca-screen.test.tsx > Dort_durum_ekranda_ayirt_edilebiliyor` | Dört ayrı rozet + `data-status` |

Bir dördüncüsü ikisinin **ayrışmadığını** tutuyor
(`Ekran_ile_export_ayni_saglayicilari_gosteriyor`): telde görünen her sağlayıcı
export'ta da var. İkisinin sessizce ayrışmasını kovalayacak başka bir şey yoktu.

**Kırmızı yandığı ölçüldü, sonra geri alındı** — beşi de:

| Kırılan | Kırmızı yanan |
| --- | --- |
| Telde iki durum tek değere | `RcaFourStatesTests` 1/6 |
| Export'ta dört etiket tek etikete | `RcaFourStatesTests` 1/6 |
| Ekranda dört rozet tek rozete | `rca-screen` 1/15 |
| Temsil edilemeyen filtre sessizce düşüyor | `rca-screen` 3/15 |
| Kapsam kapısı açık | `EvidenceBundleScopeTests` 5/8 |

`data-status` bilinçli: rozet metni bir gün değişse bile ayrım DOM'da yaşıyor.
Tanınmayan bir durum da **tanınmadığını söyleyerek** görünüyor — sunucunun
altıncı bir durum eklemesi ekranda sessizce "veri yok"a düşmemeli.

## 2 · Kapattığım açık — saklanan paketin okuma kapsamı

**Kanıt toplama kapsam altında koşuyor, ama paket `id` ile okunuyor ve belge
getirmede filtrelenecek bir `WHERE` yok.** A grubunun kapsamıyla toplanmış bir
paketi B grubundan biri `GET /v1/rca/{id}` ile isteyebilirdi; kanıt özetleri ham
log gövdesi taşıdığı için bedeli doğrudan veri sızıntısı.

T36 ucu bilerek yazmamıştı (*"sahibi T37"*), dolayısıyla açık kimsenin hatası
değil **sahipsizdi**.

Kural `BundleScope.IsReadableBy`'da:

| Durum | Sonuç | Neden |
| --- | --- | --- |
| Paket kapsamı ⊆ okuyan | ✅ | |
| Kısmi kesişim | ❌ | Paket satır bazında ayıklanamaz; "biraz göster" diye bir şey yok |
| Sistem kapsamlı paket, sınırlı okuyucu | ❌ | Aksi hâlde en geniş paket en kolay okunan olurdu |
| Boş kapsam + sistem değil | ❌ | Tutarsız kayıt "kısıt yok" diye okunmamalı |

Okuyamayan **404** alıyor, 403 değil: 403 paketin var olduğunu doğrular ve
*"şu pencerede RCA koşulmuş"* tek başına bir sızıntı.

## 3 · Drilldown — daha önce bir kez ödenmiş bedel

Ticket: *"şu imza ilk kez göründü" satırı, o imzayı arayan sorguyu doğru zaman
aralığıyla açıyor.*

Arama ekranının parametre kümesi kanıt sağlayıcılarının ürettiği her filtreyi
karşılamıyor — `signature_hash`'in kutusu yok, olumsuzlamanın da.

**Sessizce düşürmek en kötü seçenek:** kullanıcı kanıt satırının
gösterdiğinden **daha geniş** bir kümeye bakar ve baktığı kümenin o satırın
kümesi olduğunu sanır. Alarm bağlantısında aynı sorun `eksik` parametresiyle
çözülmüştü; **ikinci bir kopya yazılmadı**, aynı mekanizma kullanıldı ve
bulguda görünür bir rozet var — tıkladıktan sonra öğrenmek geç.

**Desteklenmeyen operatör eşitliğe çevrilmiyor.** Çevirmek, dolu ve inandırıcı
ama **yanlış** bir küme göstermek olurdu; bildirilen bir boşluktan beter.

## 4 · Baseline — önseçili değer yok

Koordinatörün süpürmesi kararı doğruladı: dirsek kuyruk biçimine göre **yedi
kat** kayıyor (`zipf 2.0 → 7g`, `zipf 1.4 → 1g`), yani *seçilebilir bir taban
yok*. Uca varsayılan koymak, T35'in uydurmayı reddettiği sayıyı uydurmak ve
ekranda **ölçülmüş gibi** göstermek olurdu.

Bedeli kullanıcıya düşüyor ve ekran bunu **söylüyor**: taban açıkça seçiliyor,
`— seçin —` ile başlıyor, ve altındaki not neden bir varsayılan olmadığını
yazıyor.

## 5 · T38'e devredilenler — altın kümeyi kuran için

Tablo artık **T38'in `golden_reviews`'ü** (T37'nin `evidence_reviews`'ü
silindi). Ekranın dayandığı kararlar:

1. **Üzerine yazmıyor, ekliyor.** Aynı paketi iki kişi inceleyebilir ve
   incelemenin **değişmesi** kalite ölçümü için bir veri; F4 *"insanlar
   birbiriyle ne kadar anlaşıyor"* sorusunu bunlardan soracak. Ekran "son söz
   ne" sorduğu için ilkini alıyor.
2. **İnceleyen token'dan geliyor**, gövdeden değil — aksi hâlde herkes
   başkasının adına oy yazabilirdi.
3. **Dört karar**: `correct` · `incomplete` · `wrong` · `unknown`.
   `unknown` bir kaçış kapısı değil bir **ölçüm**: seçenek olmasaydı gerçekten
   bilmeyen kişi rastgele birini seçer ve altın küme sessizce gürültüyle
   dolardı. Doğruluk oranının paydasına girmiyor.
4. **`ActualRootCause` zorunlu değil** ve **boşluğu bilgi taşıyor**: `wrong`
   olup burası boşsa inceleyen yanlışı görmüş ama doğrusunu bilmiyor demektir.
5. **`contradicting_evidence` ayrı bir soru** ve karara **bağlı değil**
   (RCA riski #5). Ekranda karar düğmelerinin yanında duruyor, varsayılanı
   `unknown`. Gerekçesi §5.1.

### 5.1 · Çelişen kanıt neden karara bağlanmadı

Alt soru yapmak cazipti ve belirli bir yönde başarısız oluyor: tiyatronun
tehlikeli hâli, raporun **bütün olarak doğru** olduğu ve o bölümü yine de
doldurmuş olduğu hâl. Soruyu olumsuz karara bağlamak, ölçümün **var olma
sebebi olan durumu hiç örneklememesi** demek — kendi en kötü durumunu göremeyen
şey ölçüm değil.

İkinci bir zorunlu tık da ters yönden yanlış: inceleme yorgunluğu canlı bir
risk ve altın küme, hiç yapılmayan incelemelerden kimsenin doldurmadığı bir
boyuttan kazandığından fazlasını kaybeder. Seçilen orta yol: **tek tık
korunuyor, boyut yine de her incelemede soruluyor.**

Varsayılan `unknown`, `not_present` değil — bugün doğru olan bir varsayılan
yarın sessizce yanlış olur ve kimse geri dönüp sormaz.

### 5.2 · Tel adları dil sınırının iki yanında çivili

Ekran bu dizgileri **elle** yazıyor; TypeScript C# enum'unu okuyamıyor, yani
derleyicinin kovaladığı bir bağ yok. `RcaReviewWireTests` sunucu tarafına,
`rca-screen.test.tsx` ekran tarafına çivi çakıyor — **ve C# tarafı enum'un
eleman sayısını da sabitliyor**: bugünkü değerleri çivilemek, beşinci bir değer
eklendiğinde yeşil kalırdı ve o değer hiçbir ekrana ulaşmadan yaşardı.

**T38'in asıl işi bu tablo değil, ona ulaşan yol.** RCA belgesinin 2. riski —
inceleme yorgunluğu — bugün açık: ekrandaki düğmeler çalışıyor ama kimse o
ekrana gitmek zorunda değil. Karşı önlem plandan aynen alınmalı: *alarm tetikli
RCA'larda inceleme, alarmı kapatma akışının zorunlu parçası.*

## 6 · Bilerek yapılmayanlar

| Ne | Neden |
| --- | --- |
| PDF export | Kapsam dışı (koordinatör kararı). Markdown var ve testli; PDF gelirse **aynı metinden** üretilmeli |
| Kuyruk, kota, debounce | Dört tetikleyiciyle birlikte **F4** |
| LLM yorumu | **F4** — yeri ayrıldı: özetin altı, bulguların üstü, kanıtın **yanı** |
| Alarm kapatma akışına bağlama | **T38** |
| `POST /v1/rca` için `Author` yetkisi | `Read` seçildi: koşu kullanıcının zaten görebildiği veriyi okuyor ve kapsam kapısından geçiyor |

## 7 · Açık kalan

- **Uçların hiçbiri canlı yığına karşı koşulmadı.** Birim testleri projeksiyonu
  ve kapsam kuralını tutuyor, ama `POST /v1/rca` gerçek ClickHouse + Postgres
  ile hiç çalışmadı. Faz sonu doğrulamasının listesinde olmalı.
- **Ekran gerçek bir tarayıcıda açılmadı.** `renderToStaticMarkup` ile
  sınandı; T28'in denetimi ve ekran görüntüsü bekçileri ayrı iş.
- Göçler **uygulanmadı** (`AddActualRootCauseToGoldenReview`) — Postgres'e
  karşı koşulması koordinatörde.
