---
kind: spec
title: "T04 — Ham arşiv: kararlar ve açıkta kalanlar"
---

# T04 — Ham arşiv, manifest ve scrub

> ⚠️ **Bu belge geriye dönük yazıldı.** Kaynağı kod, commit geçmişi ve F1
> kapanışı. Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan
> gerekçeler kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen
> alternatifler kayıtta yok.

**Yöneten kararlar:** K25, risk #13 · **Ticket:** `tickets/ham-arsiv`

## Ne yaptı

Replay'in tek kaynağını kurdu — ve onu **object storage'ın veri kaybetmesini
varsayarak** kurdu. Ticket'ın kendi ifadesiyle: RustFS 1.0-beta ve
geliştiricilerinin üretim tavsiyesi yok.

Dört parça: S3 yazıcısı, Postgres'teki manifest, doğrulayan yükleyici ve
periyodik scrub.

## Kodda görünen kararlar

### Yalnızca S3 API — RustFS'e özel çağrı yok

`IRawObjectStore` `AWSSDK.S3` üzerinden konuşuyor ve özel uç noktayla
yapılandırılıyor. Bunun bedeli bir soyutlama katmanı; karşılığı, RustFS'in
taşınabilir olması. Ticket bunu açıkça istiyordu ve kod ona uyuyor.

### `owner_group` nesne anahtarının **içinde**

Anahtar deseni: `raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/{source_class}/{ulid}`.

Grup yolun içinde olduğu için ham okuma, nesneyi **indirmeden önce** kapsam
kararı verebiliyor (`AccessScope.Allows`). Alternatif — grubu manifest'ten
sorgulamak — her okumada ikinci bir tur demekti, ve daha önemlisi kapsam
kontrolünü indirmeden sonraya bırakma riskini taşıyordu.

### Manifest bu ticket'ın en değerli parçası

Ticket'ın kendi cümlesi: *"manifest olmadan 'replay 7 gün yerine 5 gün döndü'*
*fark edilmez."*

Manifest o sessiz kısalmayı bir **hata mesajına** çeviriyor: T11'in replay ucu
manifest'te olup arşivde bulunmayan nesneleri sayıyor ve 409 dönüyor.

### Doğrulanmadan segment silinmiyor

`verified_at` null iken WAL segmenti silinemiyor. Yükleyici nesneyi yazdıktan
sonra **geri okuyup** sha256'sını doğruluyor; ancak o zaman damga düşüyor.

Bunun üstüne 48 saatlik bekleme var: doğrulanmış segment bile hemen
silinmiyor. Gerekçe risk #13 — object storage o pencerede veri kaybederse
yerelden yeniden yüklenebilsin.

### Dayanıklılık sınırı bilerek WAL'da

F1'in ifadesi: object storage veri kaybederse en kötü senaryo *"eski arşivin
bir kısmı"*, *"yeni veri"* değil. Ack, ham batch yerel WAL'a yazılıp fsync
edildikten **sonra** veriliyor.

### `raw_ref` offset değil **ön ek** taşıyor

Bu, F1 kapanışının ayrıca yazdığı bir karar. Ingest boru hattı ile arşiv
yükleyici bilerek bağımsız çalışıyor, dolayısıyla olay satırı yazılırken nesne
henüz yok ve offset bilinemez.

Ön ek yazma anında hesaplanabiliyor ve manifest sorgusunun anahtarıyla
örtüşüyor. Bedeli: tek kaydı okumak için nesnenin açılması.

## Bugün duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `RawArchiveTests` (entegrasyon) | Yazma → geri okuma → sha256; scrub'ın uyuşmazlığı yakaladığı; doğrulanmamış segmentin silinmediği |
| `RawEventLocatorTests` (entegrasyon) | Mikrosaniye/milisaniye kırpılması ve payın dar kalması |
| `ApiSurfaceTests` | API katmanının `IRawObjectStore`'a **doğrudan erişemediği** — ham okuma kapsam kapısından geçmek zorunda |

Üçüncüsü mimari bir bekçi ve bu ticket'ın kapsam vaadini koruyan şey o:
nesne deposuna doğrudan erişen bir uç, kapsam kontrolünü atlayabilirdi.

## F1'de kırılan ve burada düzelen şey

F1'in uçtan uca ilk denemesinde beş hata çıktı ve **beşincisi** bu ticket'ın
alanındaydı:

> Manifest mikrosaniye, ClickHouse `DateTime64(3)`. Kırpılan `ts` daima
> `ts_from`'dan küçük → tek olaylı nesne **hiç bulunamıyor**.

Belirtisi yanıltıcıydı: 404 *"henüz yüklenmemiş olabilir"* diyerek yanlış yere
yönlendiriyordu. `RawEventLocatorTests` şimdi kırpılmayı ve payın dar kalmasını
sabitliyor.

F1 kapanışının notu: bu beş hatanın **dördü konteyner gerektirmeden**
yakalanabilirdi — baştan beri dosyada okunabilir birer sözleşme ihlaliydiler.

## Açıkta kalanlar

| # | Ne | Durum |
| --- | --- | --- |
| 1 | **Lifecycle / tiering** | Ticket'ta açıkça "v2" |
| 2 | **Scrub örnekleme oranı** | `ScrubSampleSize = 20` yapılandırmada; bu sayının **neden 20 olduğu kayıtta yok**. Kaç nesneden kaçının örneklendiği ve o oranın kaybı ne kadar sürede yakalayacağı ölçülmemiş |
| 3 | **48 saatlik pencere ölçülmedi** | `SegmentRetention = 48:00:00`. Risk #13'ün gerekçesi yazılı ama sürenin **neden 48 olduğu kayıtta yok** — RustFS'in kayıp fark etme süresine karşı ölçülmüş bir sayı değil |
| 4 | **Kayıp nesnenin yerelden geri yüklenmesi** | Politika bu; WAL segmenti 48 saat duruyor. Ama *"nesne kayboldu, segmentten yeniden yükle"* yolunu koşturan bir kod ya da test **yok**. Manifest kaybı **görüyor** (`RawObjectState.Missing`), kurtarma elle |

Dördüncüsü en dikkat çekeni: koruma **tasarlanmış** ve saklama süresi ona göre
seçilmiş, ama kurtarmayı yapan şey yazılmamış. Bugünkü hâlde manifest kaybı
raporluyor ve gerisi operatörde.
