---
title: "T28 denetimi — bulgular ve bekçiler"
kind: review
---

# T28 denetimi — yedi bulgu, altı bekçi

Denetim `5f657f6` üzerinde yapıldı. Her bulgu ya düzeltildi ya gerekçesiyle
kayda geçti; **her düzeltmenin yanında onu tutan bir bekçi var** — bir görüş
bir sonraki turda kaybolur, bekçi kalır.

Denetleyen ajan ekranların üçünü (T15, T16, T17) kendisi yazdı. Telafi olarak
kurallar **ekran adı geçmeden** yazıldı: kural herkese aynı uygulanınca kimin
yazdığı sorusu düşüyor. Yedi bulgunun **dördü** denetleyenin kendi kodunda
çıktı.

## 1 · Zaman damgası üç ayrı biçimde — biri **yerel saat**

En ciddi bulgu ve kozmetik değil.

| Nerede | Biçim | Örnek |
| --- | --- | --- |
| olay ekranları | UTC, saniyeli, `Z` ekli | `2026-08-16 12:30:00Z` |
| değişiklik tablosu | UTC, **dakika** hassasiyeti, `Z` yok | `2026-08-16 12:30` |
| alarm ekranları | **yerel saat**, `tr-TR` | `16.08.2026 15:30:00` |

Bir alarm tetiklenmesini log satırıyla eşleştiren kullanıcı, **saat farkı
kadar** sapmış iki zaman görüyor ve hiçbir yerde bunun yazmıyor. İstanbul'da üç
saat. Log analizi yapan bir üründe bu doğrudan yanlış sonuç üretiyor.

**Düzeltildi:** `ui/src/lib/ui/time.ts` — tek biçim, UTC ve `Z` ile açıkça
işaretli. Üç çağrı yeri de oraya bağlandı.

**Bekçi:** yinelenen dışa açım kontrolü (aşağıda) + `changes-screen.test.tsx`
biçimi sabitliyor.

## 2 · Sunucu bileşeni ekranlarının yükleniyor durumu **yok**

`LoadingState` dokuz dosyada kullanılıyordu ve hiçbiri sunucu bileşeni değildi.
`/`, `/olaylar`, `/olaylar/{id}` ve `/kaynaklar` HTML gelene kadar **hiçbir geri
bildirim vermiyordu**: tarayıcı önceki sayfada bekliyor, ClickHouse sorgusu
saniyeler sürebiliyor ve kullanıcı bir şeyin çalışıp çalışmadığını
anlayamıyordu. Ticket'ın "yükleniyor" maddesi tam bu ekranları vuruyordu.

**Düzeltildi:** dört `loading.tsx` (Suspense sınırı).

**Bekçi:** her `page.tsx` ya kendi/üst klasöründe `loading.tsx` ya bölümünde
`LoadingState` taşımalı. Kök sınırı **yalnızca kökün kendi rotasını** kapsıyor
sayılıyor — Next açısından iç içe rotaları da kapsıyor ama onu geçerli saymak,
bir ekranın kendi sınırını unutmuş olmasını görünmez kılardı.

## 3 · Kontrast: dört çift WCAG AA altında

Ticket kontrastı açıkça istiyor. "Denetlendi" bir görüş; hesaplandı.

| Çift | Tema | Önce | Sonra |
| --- | --- | --- | --- |
| uyarı rozeti metni | açık | **2.07:1** | 4.84:1 |
| başarı rozeti metni | açık | **3.15:1** | 4.79:1 |
| hata metni | açık | **4.41:1** | 5.91:1 |
| tehlike düğmesi | koyu | **3.76:1** | 4.83:1 |

Sonuncusunun sebebi öğretici: `--danger` koyu temada okunabilirlik için
**açılıyor** (`red-500`) ve düğme o açık kırmızının üstünde beyaz metin
taşıyordu. İki farklı iş tek jetona bindirilmişti — yüzey üstünde **metin**
rengi ile **dolgu** rengi. Ayrıldı: `--danger-solid` + `--danger-on`.

**Bekçi:** `contrast.test.ts` — `tokens.css`'i tema başına ayrıştırıyor,
`var()` zincirini çözüyor ve gerçekten yan yana duran 12 çifti iki temada
ölçüyor. Ayrıca `[data-theme="dark"]` ile `@media (prefers-color-scheme: dark)`
bloklarının ayrışmadığını sabitliyor — ayrışırlarsa ürün, işletim sistemi koyu
temadayken düğmeyle geçmiş kullanıcıya göre **başka** görünürdü.

## 4 · `describeError` iki uygulamada, davranışları farklı

İkisi de `ApiError`'ı aynı ele alıyordu ama son dalları ayrışıyordu: biri düz
bir `Error`'ın mesajını koruyor, öbürü atıp genel bir cümle koyuyordu. **Aynı
hata, hangi ekranda olduğunuza göre farklı metinle** çıkıyordu.

**Düzeltildi:** `lib/alerts/errors.ts` silindi, yedi çağrı yeri
`lib/api/errors.ts`'e bağlandı.

## 5 · Dört durumun sırası kırılgandı

`screenState(rows, error)` önce `rows === null` bakıyordu, yani
`screenState(null, "hata")` → `"loading"`. İlk yüklemesinde düşen bir ekran
**sonsuza kadar iskelet** gösterip hatayı hiç söylemezdi. Çağıranlar bunu
`catch` içinde `setRows([])` yazarak telafi ediyordu — yani doğruluk, herkesin
hatırlaması gereken bir kurala bağlıydı.

**Düzeltildi:** hata önce. Fonksiyon değişiklik akışına özgü bir modülden
(`lib/changes/connector.ts`) ortak kite taşındı — orada durduğu için diğer
ekranlar onu bulamamıştı.

**Bekçi:** `screenState` yalnızca `lib/ui/screen-state.ts`'te tanımlı olabilir
ve hiçbir ekran kendi `ScreenState` tipini yazamaz.

## 6 · Tek ham renk

`ui.module.css` içinde `.buttonDanger { color: #ffffff }` — T13'ten kalma ve
ağaçtaki tek jeton ihlali. Kabul kriteri "tek kullanımlık renk kalmamış" diyor.

**Düzeltildi** ve bekçiye bağlandı: `tokens.css` dışında ham renk yok.

## 7 · `toUpperCase` — kural düz yasak olamaz

İki kullanım var, ikisi de HTTP yöntemi ve tipi `"get"|"post"|…`, yani ASCII
olduğu **kanıtlanabilir**. Türkçe `İ`/`ı` tehlikesi yok.

Bulgu, kullanımın kendisi değil kuralın şekli: dar ve **gerekçeli** bir muafiyet
taşımalı. `ProducesContractTests`'teki `ExpectedExemptCount` kalıbı uygulandı —
muafiyet listesini büyütmek ayrı ve görünür bir karar.

## Bekçilerin hepsi kırmızı yanabiliyor

Her biri, koruduğu hata geri konularak ölçüldü; sonra geri alındı.

| Bekçi | Nasıl kırmızı yandı |
| --- | --- |
| yükleniyor sınırı | bir `loading.tsx` silindi |
| ham renk | bir CSS modülüne `#ff0000` eklendi |
| Türkçe kasa | muaf olmayan bir dosyaya `.toUpperCase()` eklendi |
| yinelenen yardımcı | aynı ad iki modülde dışa açıldı |
| ham tablo | `DataTable` dışında `<table>` yazıldı |
| kontrast | `--danger-solid` açık kırmızıya çevrildi |

Bekçinin kendisi de bir kez yanlış pozitif verdi: ilk hâli **belge
yorumlarındaki** örnek `<table>` etiketlerini ihlal saydı. Yorumlar artık
taranmadan çıkarılıyor. Bir bekçinin yanlış pozitifi, kırmızı yanmamasından
farklı bir tehlike — insanlar onu susturmayı öğreniyor.

## İki şey bulgu değil çıktı

- **`px` kullanımları meşru:** hepsi `1px` kenarlık, `2px`/`3px` vurgu çizgisi
ya da yorum. Jetonlar kıl payı çizgileri kapsamıyor.
- **`AppShell` ikinci kez kurulmamış:** "iki kabuk" adayı ölçülünce düzelmiş
çıktı. Ölçmeden teste bağlansaydı, var olmayan bir sorunu sabitleyen ve bir gün
gerçek bir değişikliği engelleyecek bir test kalırdı.

## Kapsanmayan — gerekçesiyle

**Ekran görüntüleri (dört durum × açık/koyu tema).** Ticket'ın kabul kriteri ve
düşürülmedi; Playwright kurulumu (~300 MB) ve Next sunucusunun ayağa kalkması
gerekiyor. Koordinatörle **ayrı bir adım** olarak kararlaştırıldı: önce
bekçiler, sonra `check` sonucuna bakılarak görüntüler.

**"Çok veri" gerçek ortamda.** 1M satırlık ölçüm koordinatörde; bekçiler için
sentetik veri yeterli, çünkü ölçülen şey ekranın davranışı, ClickHouse'un hızı
değil.

**İki farklı veri çekme mimarisi.** Ekranların bir kısmı sunucu bileşeni, bir
kısmı istemcide `fetch`. Bunu tekleştirmek **yeniden tasarım** olurdu ve ticket
onu kapsam dışı bırakıyor. Denetimin istediği, ikisinin de dört durumu tanımlı
olması — artık öyle.
