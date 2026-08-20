---
title: "T28 — UI/UX ve estetik denetimi"
kind: ticket
status: 1
---

# T28 — UI/UX ve estetik denetimi

**Bağımlılık:** T16, T20, T23, T25 · **Sonraki:** —

## Amaç

F2 sonunda ortaya çıkan ekranların **birlikte bir ürün gibi** görünmesi. Tek tek
çalışan yedi ekran, yedi ayrı ürün gibi duruyorsa F2 yarım bitmiş demektir.

<user_quoted_section>Bu ticket kozmetik bir rötuş listesi değil. Aşağıdaki maddelerin çoğukullanılabilirlik sorunu: okunamayan bir tablo, bulunamayan bir düğme vebozulan bir hizalama, ürünü "çirkin" değil kullanışsız yapar.</user_quoted_section>

## Bu üründe özel olan risk: çok dilli gövdeler

Ürün Türkçe, Arapça ve Çince log gövdeleri alıyor — F1'de ölçüldü ve arşivde
gerçek örnekleri var. İngilizceyle düzgün görünen her düzen bu içerikte
sınanmalı:

- **Uzun satır:** 500+ karakterlik tek satırlık gövde tabloyu yatay kaydırmaya
sokmamalı; kırpma ve genişletme davranışı belirli olmalı.
- **Arapça (RTL):** gövde sağdan sola, arayüz soldan sağa. Karışık yönlü metin
hücrede taşmamalı, noktalama yer değiştirmemeli. `dir="auto"` yeterli mi,
ölçülmeli.
- **CJK:** boşluksuz metinde satır kırma. `word-break` davranışı yanlışsa hücre
tek kelime gibi davranıp düzeni patlatıyor.
- **Türkçe:** `İ`/`ı` büyük-küçük dönüşümü arayüzde de yapılmamalı — F1'de bu
kural derleme zamanında zorlanıyor (CA1304/CA1311), UI tarafında karşılığı
`toLocaleUpperCase` kullanımının gözden geçirilmesi.
- Sayı ve tarih biçimleri kullanıcının yereline göre; ondalık ayracı karışmamalı.

## Kapsam

### İçinde

**1. Tutarlılık denetimi** — tüm ekranlar tek tek gezilip karşılaştırılıyor:

- Aynı işi yapan bileşenler aynı görünüyor mu (tablo, filtre çubuğu, boş durum,
hata durumu, yükleniyor, onay diyaloğu).
- Boşluk ve hizalama tek ölçekten mi geliyor.
- Tipografi hiyerarşisi ekranlar arası aynı mı.
- Renk yalnızca jetonlardan mı geliyor; tek kullanımlık hex var mı.

**2. Durum kapsaması** — her ekran için dört durum:

| Durum | Neden önemli |
| --- | --- |
| Boş | Yeni kurulumda **her** ekran boş açılıyor; ilk izlenim burası |
| Yükleniyor | ClickHouse sorgusu saniyeler sürebiliyor; iskelet mi, dönen mi, ne |
| Hata | F1'in API'si `{ error, hint }` veriyor — `hint` gösteriliyor mu |
| Çok veri | 1M satırlık ortamda tablo, filtre listesi, sayfalama |

**3. Erişilebilirlik**

- Kontrast oranı (WCAG AA) açık ve koyu temada.
- Klavye ile tam gezinme; odak halkası her yerde görünür.
- Form alanlarının etiketleri; hata mesajlarının alanla ilişkisi.

**4. Duyarlılık**

- Dizüstü (1440), geniş masaüstü (1920) ve dar (1280) genişlikler. Tablet/telefon
F2'nin hedefi değil ama düzen kırılmamalı.

**5. Kanıt**

- Her ekranın her durumu için ekran görüntüsü, açık ve koyu temada.
- Bulguların önem sırasına göre listesi ve düzeltmeler.

### Dışında

- Yeniden tasarım. Bu ticket mevcut ekranları tutarlı ve okunur hâle getiriyor,
sıfırdan bir görsel dil önermiyor.
- Marka kimliği, logo, illüstrasyon.

## Kabul kriterleri

- Yedi ekranın **her biri** dört durumda (boş, yükleniyor, hata, çok veri) açık
ve koyu temada görüntülenmiş; kanıt olarak ekran görüntüleri var.
- Çok dilli gövde testi: aynı tablo Türkçe, Arapça ve Çince uzun satırlarla
bozulmuyor.
- Kontrast denetimi geçiyor; klavye ile her ekran baştan sona gezilebiliyor.
- Tek kullanımlık renk/boşluk değeri kalmamış — hepsi T13'ün jetonlarından.
- Bulunan sorunlar ya düzeltilmiş ya gerekçesiyle kayda geçmiş.

## Notlar

Denetimin **F2 sonunda** yapılması bilinçli: ekranlar bitmeden tutarlılıktan
bahsedilemez. Ama tasarım temeli T13'te kuruluyor, yani bu ticket bir toparlama
değil doğrulama olmalı. Toparlama işine dönüşüyorsa T13 eksik yapılmış demektir.

Grafik/histogram F2 kapsamı dışında (T15). Eklendiği gün `dataviz` yönergesi
devreye girmeli — renk paleti ve grafik seçimi kendi başına bir disiplin.

## Durum — bekçi dilimi sevk edildi

Bulgular ve düzeltmeler: [T28 denetimi — bulgular ve bekçiler](../../t28-denetim-bulgulari/index.md).

**Yedi bulgu**, dördü denetleyenin kendi ekranlarında; hepsi düzeltildi ve
**altı bekçiyle** sabitlendi. Altısının da kırmızı yanabildiği ölçüldü.

**Ekran görüntüleri henüz alınmadı.** Kabul kriteri düşürülmedi; Playwright
kurulumu ve Next sunucusu gerektirdiği için koordinatörle ayrı bir adım olarak
kararlaştırıldı. Ticket o adım bitince kapanacak.
