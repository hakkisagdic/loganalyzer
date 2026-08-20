---
title: "T27 — F2 doğrulaması"
kind: ticket
status: 0
---

# T27 — F2 doğrulaması

**Bağımlılık:** T16, T20, T23, T26 · **Sonraki:** —

## Amaç

F1'in dersini uygulamak: **doğrulanmamış her katman kırıktı ve hiçbiri kendini**
**belli etmedi.** Bu ticket o hatayı tekrarlamamak için var.

<user_quoted_section>Bu ticket, diğer ticket'ların doğrulama sorumluluğunu üstlenmiyor. Herticket kendi kapsam ayrışması testini taşıyor. Burada yapılan, tek tek doğruolan parçaların birlikte doğru olduğunu göstermek.</user_quoted_section>

## Kapsam

### İçinde

Uçtan uca akışlar, tarayıcıdan başlayarak:

1. **Giriş → arama → detay → ham bayt.** `analyst.core` giriyor, kendi grubunun
logunu arıyor, bir olayın ham baytlarını indiriyor. İndirilen baytlar cihazın
gönderdiğiyle birebir aynı.
2. **Parser yaz → dene → yayınla → etkisini gör.** Yeni parser ekranda yazılıyor,
örnek satırla deneniyor, yayınlanıyor, ve sonraki olaylar onunla ayrıştırılıyor.
3. **Cihaz sussun → alarm → bildirim.** Envanterdeki bir kaynak veri göndermeyi
kesiyor, sessizlik alarmı tetikleniyor, kanala ulaşıyor, mesajdaki bağlantı doğru
aramayı açıyor.
4. **Değişiklik kaydı üç kaynaktan da düşüyor** ve ekranda birlikte görünüyor.

Ve iki çapraz kesen doğrulama:

- **Kapsam ayrışması:** `analyst.core` ile `analyst.edge` her ekranda farklı veri
görüyor; hiçbir filtre, URL veya doğrudan kimlik girişi bunu delmiyor.
- **Token sızıntısı yok:** hiçbir tarayıcı yanıtında, çerezde veya
`localStorage`'da erişim token'ı bulunmuyor.

### Dışında

- Yük testi ve performans ölçümü — ayrı bir iş.

## Kabul kriterleri

- Dört akış da otomatik koşuyor ve CI'da geçiyor.
- İki çapraz doğrulama bekçi testi olarak duruyor.
- **F1'den devredilen iki ölçüm kapanıyor:**
  - Replay sırasında canlı ingest bozulmuyor (yük altında sınanmadı).
  - Kuru koşu gerçek çalıştırmayla aynı sonucu veriyor (uçtan uca tek testte
gösterilmedi).
- **`Produces<T>` bekleme listesi (`Pending`) boşalmış olmalı.** T15'te kurulan
bekçi (`tests/Bizigo.UnitTests/ProducesContractTests.cs`) her ürün ucunun yanıt
tipi bildirmesini zorluyor; bildirmeyenler **iki listeden** birinde duruyor.
  - **`Pending`** — "henüz tipsiz, ekran indikçe çıkacak". **Boşalmadan F2
    bitmiş sayılmıyor.** Bir satırı silmek uca `.Produces<T>()` eklemeden
    mümkün değil: test, tipi kazanmış bir ucun listede kalmasını da hata
    sayıyor.
  - **`Exempt`** — hiçbir zaman tüketicisi olmayacak uçlar (collector ingest'i,
    webhook alıcısı, 204 dönen silmeler). Boşalması **beklenmiyor**; sayısı
    `ExpectedExemptCount` ile sabitlenmiş, yani listeyi büyütmek ayrı ve
    görünür bir karar.

  İkisi T17'de ayrıldı. Tek listeyken "boşaldı mı" sorusunun cevabı **asla evet
olamıyordu** ve bu kriter sağlanamaz durumdaydı.

- **Kapı denetlediği kümeyi kendisi bulmalı.** T17'de kapatılan yapısal delik:
bekçi uçları elle yazılmış bir `Map*` listesinden topluyordu ve T21/T22/T24
indiğinde 16 uç ona hiç görünmedi — üç testin üçü de geçti, yeşilliği hiçbir
şey ifade etmiyordu. Artık `IEndpointRouteBuilder` uzantıları **yansımayla**
bulunup çağrılıyor. Burada doğrulanacak olan: yeni bir uç dosyası eklendiğinde
kapı onu kendiliğinden denetliyor mu.

## Notlar

F1'de beş hata art arda çıktı ve her biri bir öncekini düzeltmeden görünmüyordu.
Bu ticket'ın akışları o zinciri baştan sona geçtiği için, benzer bir zincir F2'de
oluşursa ilk burada görünecek.

Bekçilerin değeri kırmızı yanabilmelerinde: her biri, koruduğu hatayı geri koyup
sınanmalı. F1'de bu disiplin uygulandı ve dört bekçinin dördü de gerçekten
kırmızı yandı.
