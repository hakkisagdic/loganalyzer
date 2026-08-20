---
kind: spec
title: "T35 — Beş deterministik korelasyon: SQL toplar, C# karar verir"
---

# T35 — Beş deterministik korelasyon

RCA'nın **LLM'siz** yarısı. Beş sinyal, `t35-korelasyonlar` dalında.

T34'ün sözleşmesi bugün sınandı ve geçti: **beş sağlayıcı eklendi,
`EvidenceCollector` tek satır değişmedi.**

## 1 · Sınır: SQL toplar, C# karar verir

Bu ticket'ın taşıyıcı tasarım kararı.

| Nerede | Ne |
| --- | --- |
| **ClickHouse** (`CorrelationReader`) | Kümeleme, sayım, anti-join — milyonlarca satır |
| **C#** (`CorrelationMath`) | Poisson/z-score, lift, eşik, sıralama |

Sebep tek: **yanlış hesaplanmış bir z-score hiçbir yerde hata vermez.** Sorgu
koşar, rapor üretilir, hipotezler sıralanır ve sıralama sessizce yanlış olur.
İstatistik SQL'in içine gömülseydi yalnızca canlı ClickHouse'la sınanabilirdi;
dışarıda olduğu için elle hesaplanmış değerlere karşı sabitlenebiliyor.

## 2 · Beş sinyal

| Sinyal | Sağlayıcı | Dayanağı | Boş sonucun anlamı |
| --- | --- | --- | --- |
| İlk-görülen imza | `logs.first-seen` | `signature_hash` anti-join (T29) | "Pencerede beliren her şey daha önce de görülmüş" |
| Hacim sapması | `logs.volume` | Poisson z-score, gerçek sayılar (T29) | "Anlamlı sapma yok" |
| Sessizlik | `logs.silence` | `GetSourceActivityAsync` × 2 pencere | "Susan kaynak yok" |
| Ortak öznitelik | `logs.attribute-lift` | Alan değeri lift'i | "Paylaşılan belirgin değer yok" |
| Yayılma sırası | `logs.propagation` | İlk bozulma anı + `time_source` | "Bozulma sayılan olay yok" |

Beşi de boş sonucu **`Empty`** olarak adlandırıyor — T34'ün ayrımı: baktık ve
bir şey yok, bu bir kanıt.

### İkisi T29 olmadan yazılamıyordu

- **İlk-görülen:** `template_id` başarılı olayların %1'inde doluydu ve bir
  imzanın **ilk** görülüşünde tanım gereği boştu. Yani "yeni bir şey oldu" diyen
  tam o satırda kimlik yoktu.
- **Hacim sapması:** sayılar gerçeğin %1'iydi; Poisson bunun üstüne kurulamaz.
  **Örnekleme düzeltmesi yok, çünkü örnekleme yok.**

### Sessizlik: üçüncü kopya yazılmadı

`GetSourceActivityAsync` T21'de tam bu amaçla tek yere kondu. Sağlayıcı onu iki
pencere için iki kez çağırıyor. Alarm motorunun **değerlendiricisi**
çağrılmıyor: o farklı bir soyutlama düzeyi (kural eşiği, susturma, zamanlama);
RCA'nın istediği ham olgu eşiksiz.

## 3 · Üç sessiz tuzak — üçü de kapatıldı ve teste sabitlendi

**1 · λ = 0 → sonsuz z-score.** Tabanda hiç görülmemiş imza için `√0 = 0` ve
bölme sonsuza gider. Naif uygulama `Infinity`/`NaN` üretir, o da sıralamada
sessizce en tepeye ya da en dibe düşer. Çözüm: hacim sapması o imza hakkında
**susuyor** — "tabanda hiç yoktu" zaten ilk-görülen sinyalinin konusu ve orada
çok daha iyi anlatılıyor.

**2 · Ham sayı ≠ lift.** Pencerede toplam hacim iki katına çıktıysa **her**
değerin ham sayısı artar. Ham sayıya bakan bir uygulama "her değer öne çıktı"
derdi. Lift oranların oranı olduğu için bu durumda 1,0 kalıyor — ve bir test
tam olarak bunu sabitliyor.

**3 · `signature_hash = 0` sahte imza.** `0` "imza yok" demek (16 KB maskeleme
sınırını aşan satırlar, T29). Elenmezse hepsi tek bir sahte imzada toplanır ve o
küme her pencerede "ilk kez görüldü" gibi davranır. İki sorguda da `!= 0`.

Ayrıca **küçük sayı koruması**: beklenen 0,2 iken gözlenen 2 olması z ≈ 4 verir
ve eşiği geçer, ama söylediği bir şey yoktur.

## 4 · `time_source` dürüstlüğü

Yayılma sinyalinin **tamamı sıralama**, ve zamanı `parsed` olmayan bir olayın
gerçek zamanı dakikalarca önce olabilir. Sorgu `time_source != 'parsed'` olan
olayları **sayıyor**, sağlayıcı hem dilim özetinde hem satır yükünde
**söylüyor**.

Sıralamayı sunup zamanın güvenilmez olduğunu söylememek, ölçülmemiş bir
kesinlik iddia etmek olurdu. Bir bekçi de tersini tutuyor: zamanların hepsi
güvenilirse uyarı **yok** — her raporda duran bir uyarı hiçbir şey söylemez.

## 5 · Ölçülmemiş sayılar — hepsi yazılı

Bunların hiçbiri ölçülmedi ve **ölçülmüş gibi durmamalı**:

| Sayı | Değer | Nerede |
| --- | --- | --- |
| Baseline uzunluğu | **varsayılan yok** | `CorrelationWindow` — ölçüm aracı hazır |
| z-score eşiği | 3,0 | `VolumeDeviationProvider.MinZScore` |
| En az pencere sayımı | 5 | `VolumeDeviationProvider`, `AttributeLiftProvider` |
| Lift eşiği | 2,0× | `AttributeLiftProvider.MinLift` |
| Sessizlik düzenlilik eşiği | pencere başına 5 olay | `SilenceProvider.MinExpectedPerWindow` |
| Bozulma önem eşiği | severity ≤ 3 (error) | `PropagationProvider.SeverityAtOrBelow` |
| Değişiklik geriye bakışı | 30 dk | `ChangeFeedProvider.Lead` (T34) |

Hepsi `init` — ölçüm sonrası tek yerden değişiyor.

### Baseline ölçüm aracı

`BaselineWindowMeasurement` (entegrasyon, `BIZIGO_BASELINE_SWEEP=1`). Taban
uzunluğunu 1 saatten 30 güne süpürüyor ve her biri için "ilk-görülen" oranını
raporluyor.

**Ölçtüğü şey duvar saati değil, veri.** Mutlak sayı yerine **oran**:
penceredeki ayrı imzaların yüzde kaçı yeni. Farklı hacimli veri kümeleri
arasında karşılaştırılabilir ve makinenin hızından bağımsız.

```bash
BIZIGO_BASELINE_SWEEP=1 dotnet test tests/Bizigo.IntegrationTests -c Release \
  --filter FullyQualifiedName~BaselineWindowMeasurement -l "console;verbosity=detailed"
```

Aranan şey eğrinin **düzleştiği** nokta: ondan sonrası gürültüyü azaltmıyor,
yalnızca sorguyu pahalılaştırıyor. Oran hiç düşmüyorsa taban yeterince geçmiş
içermiyor demektir. **İki koşum kaydedilmeli** — tek koşum bir günün karakterini
ölçer.

Araç hiçbir eşik iddia etmiyor: bu bir ölçüm, bekçi değil.

## 6 · Bekçiler — kırmızı yanabildiği ölçüldü

Dördü de hata geri konularak sınandı, sonra geri alındı.

| Kırılan | Kırmızı yanan |
| --- | --- |
| λ=0 koruması kalkıyor (sonsuz z-score) | 27'de 2 |
| Lift ham sayı karşılaştırmasına dönüyor | 27'de 3 |
| Güvenilmez zaman uyarısı kalkıyor | 17'de 1 |
| Sessizlik düzenlilik eşiği kalkıyor | 17'de 1 |

Ölçülen durum: 18 proje **0 uyarı**, **732** birim testi geçti / 3 atlandı
(705'ti).

Bir kusur da testi yazarken çıktı ve öğreticiydi: hacim sapmasının beklenen
değerini elle hesaplarken tabanı 7 tam gün saymıştım, oysa örtüşmeyi engelleyen
30 dakikalık boşluk yüzünden 10.050 dakika. Kod doğruydu, elle hesap yanlıştı —
testin sayıyı sabitlemesinin sebebi tam olarak bu.

## 7 · Yazıldı, koşturulmadı (faz sonu)

`CorrelationQueryTests` — SQL'in kendisi gerçek ClickHouse'a karşı: anti-join'in
tabanı dışlaması, `signature_hash = 0` elenmesi, **kapsam dışı tabanın kendi
sinyalimi bastırmaması** (sinsi olan bu: bastırsaydı sinyal sessizce kaybolurdu),
`countIf` pencerelerinin doğru bölünmesi, ve izin listesi dışı alanın
reddedilmesi.

## 8 · Sözleşme genişlemesi

`IScopedQuery`'ye dört metot eklendi (`GetFirstSeenSignaturesAsync`,
`GetSignatureVolumeAsync`, `GetAttributeLiftAsync`, `GetPropagationAsync`).
Beşinci sinyal yeni metot **istemedi** — mevcut yüzeyi paylaşıyor.

Dördü de kapsam kapısından geçiyor ve denetim günlüğüne yazıyor
(`rca.first-seen`, `rca.volume`, `rca.attribute-lift`, `rca.propagation`).
Kapsamsız bir korelasyon, bir ekibin başka bir ekibin verisini kanıt olarak
görmesi demek olurdu — üstelik rapor onu doğru veri gibi sunardı.

## 9 · T36'ya devredilenler

- Kanıt paketi deposu ve deterministik rapor.
- Hipotez sıralaması: `Weight` sağlayıcı **içinde** karşılaştırılabilir,
  sağlayıcılar arasında **değil**. Normalleştirme T36'nın kararı.
- Ölçülmemiş yedi sayının gözden geçirilmesi — baseline ölçümünden sonra.
