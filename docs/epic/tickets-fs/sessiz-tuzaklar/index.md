---
title: "T57 — Çift yapılandırma anahtarı ve penceresiz idempotency"
kind: ticket
status: 2
---

# T57 — İki sessiz tuzak

İkisi de başka ajanlar tarafından ölçüldü (M06, M05), ikisi de §7'nin sınıfı,
ve ikisine de sahipleri **bilerek dokunmadı** (§9).

## Kalem 1 · `appsettings.json`'da `"Rca"` iki kez

`src/Bizigo.Api/appsettings.json` satır 76 (`Quota` + `Schedules`) ve satır 111
(`Model`). JSON yapılandırma sağlayıcısı **nesne** çakışmasını sessizce
birleştiriyor, **yaprak** çakışmasında ise fırlatıyor.

Bugün çalışıyordu. Sessizliğin iki bedeli vardı:

1. İki bloğa aynı adda bir **yaprak** eklendiği gün uygulama **açılışta**
ölürdü, ve sebebi dosyada görünmezdi.
2. Dosyayı okuyan insan birleşmeyi **görmüyordu** — iki blok otuz beş satır
arayla duruyordu.

### Şart karşılandı: etkin değerler birebir aynı

Birleştirme "temizlik" olarak yapılamazdı; sessizce bir ayar değiştirebilirdi.
Ölçüm: yapılandırma birleştirmeden **önce** ve **sonra** gerçek
`ConfigurationBuilder` ile yüklendi ve bütün anahtar/değer çiftleri
karşılaştırıldı.

> **77 etkin anahtar, önce ve sonra birebir aynı.**

(Toplam 81 çiftin dördü `//` yorum anahtarı; karşılaştırma yorumları dışarıda
bırakıyor, çünkü birleştirme sırasında bir yorum satırı **eklendi**.)

### Arandı: başka dosyada var mı — **yok**

Depodaki bütün `appsettings*.json` tarandı (derleme çıktıları hariç):
**iki dosya**, `appsettings.json` ve `appsettings.Development.json`. Tekrar
eden anahtar yalnızca birincisinde ve yalnızca `Rca`. Ticket'ın saydığı
`Cli`/`Simulators` yapılandırmaları **yok**.

### Karar: bekçi yazıldı, ve gerekçesi ölçüldü

Ticket *"mekanikleştirilebilir mi yoksa bir kerelik düzeltme mi"* diye
soruyordu. Ölçüldü: sınıf **tamamen mekanik** — kardeş düzeyde tekrar eden bir
anahtar ayrıştırıcı tarafından görülüyor, yargı gerektirmiyor, yanlış pozitifi
yok. On satırlık bir tarama bütün dosyaları kapsıyor.

Tek seferlik düzeltme aynı hatayı bir sonraki `appsettings` için açık
bırakırdı. Bekçi `ConfigurationFileTests` ve **her düzeyde** tarıyor: iç içe
bir nesnedeki tekrar da aynı sessiz birleşmeyi üretiyor.

Bekçinin kendi bekçisi de var — tarama gerçekten dosya buluyor mu. `MemberData`
boşalsaydı teori **hiç koşmadan** yeşil sayılırdı.

## Kalem 2 · Idempotency araması penceresiz — **ve öyle kalması gerekiyor**

`RcaAdmission.AdmitAsync` anahtarı zaman sınırı olmadan arıyor: bir anahtar bir
kez kullanıldıysa **sonsuza kadar** aynı koşumu döndürüyor.

Ticket kararı iki yönlü bırakmıştı: *pencere koy* ya da *davranışı yazılı hâle
getir*. **Ölçüm üçüncü bir cevap verdi.**

### Aramaya pencere koymak **etkisiz** olurdu

`ControlPlaneDbContext`, `idempotency_key` üzerinde **filtreli TEKİL indeks**
kuruyor. Yani:

1. Pencere "süresi dolmuş" bir anahtarı atlar,
2. `AdmitAsync` yeni satırı eklemeye çalışır,
3. `INSERT` **tekil indekse çarpar**,
4. `DbUpdateException` yakalanır — bu yol zaten var, yarış için yazılmış —
5. `ExistingByKeyAsync` **eski koşumu** bulur,
6. çağıran **yine eski koşumu** alır, `Existing: true`.

Gözlenebilir davranış **değişmez**; yalnızca başarısız bir yazma eklenir. Yani
ticket'ın birinci şıkkı, bu deponun en çok bedel ödediği sınıfa girecekti:
**düzeltme gibi görünen, ölçüldüğünde hiçbir şey olan bir değişiklik.**

Davranışı gerçekten değiştirmek **indeksin tanımını** değiştiren bir göç ister —
örneğin `(anahtar, pencere kovası)` üzerinde tekillik. Bu ticket'ın kapsamında
değil ve **hiçbir ölçülmüş tüketici onu istemiyor**.

### Ölçüldü: bugün buna dayanan tüketici

Ticket *"REST'te anahtarı kim gönderiyor"* diye sormuştu. Cevap:

> Anahtarı **istemci** üretiyor — `POST /v1/rca` üzerindeki `Idempotency-Key`
> başlığı. **Ürün hiçbir yerde kendi üretmiyor.**

HTTP idempotency sözleşmesine uyan bir istemci mantıksal işlem başına yeni
anahtar üretiyor, yani kalıcılık onu **hiç etkilemiyor**. Anahtarın aylar sonra
tekrar etmesi istemci tarafında bir kusur olurdu, ve o hâlde bile ürünün
verdiği cevap — *"aynı anahtar aynı raporu döndürür"* — sözleşmeye uygun.

### Ama **türetilmiş** anahtarlar sözleşmenin dışında

İçerikten türetilen bir anahtar (`{konu}:{sha256(kimlik + pencere)}` gibi)
tanım gereği **tekrar ediyor**: aynı alarmın aynı penceresi aynı anahtarı
veriyor. Kalıcı arama o pencere için ikinci bir RCA'yı **sonsuza kadar**
bastırır.

Bu bir kusur değil, **sözleşmenin sonucu** — ve türetimi yapan tarafın bilmesi
gereken şey. Sözleşme artık `RcaAdmissionRequest.IdempotencyKey`'in belgesinde
ve iki yol açıkça yazılı: pencereyi anahtara **kat**, ya da anahtarsız gel
(debounce ve kotaya tabi olmak, ki çoğu zaman istenen odur).

### Karar bir bekçiye bağlandı

*"Yazılı hâle getirmek"* tek başına yeterli değildi: yazı, kod değiştiğinde
sessizce yanlış olur. `RcaAdmissionStoreTests.Idempotency_anahtarinin_suresi_dolmuyor`
aynı anahtarı **altı ay ileri kurulmuş bir saatle** ikinci kez sunuyor ve aynı
koşumun döndüğünü, ikinci satır yazılmadığını sınıyor.

Zaman denklemden çıkarılmıyor, **ölçülüyor** — ama duvar saatiyle değil,
`TimeProvider` taşınarak. Testin geçme sebebi altı aylık farkın **hiçbir şeyi
değiştirmemesi**.

**Koşturulmadı** (§2 — konteyner).

## Ölçülen bekçiler

| Kusur | Sonuç |
| --- | --- |
| `"Rca"` yine iki kez | `ConfigurationFileTests` ✅ kırmızı |
| Idempotency kalıcılığı | konteyner gerektiriyor — **yazıldı, koşturulmadı** |

Kırmızı ölçümünün ilk denemesi **koşmadı**: geri alma betiği bir çapayı
bulamayıp çöktü, test **temiz dosya** üzerinde koştu ve yeşil geldi. O yeşil
raporlanmadı — §6'nın *"yeşil bir sonuç, ölçümün yapılmadığı anlamına da
gelebiliyor"* maddesi. İkinci denemede kusur uygulandı, **dosyada olduğu iddia
edildi**, ve bekçi kırmızı yandı.
