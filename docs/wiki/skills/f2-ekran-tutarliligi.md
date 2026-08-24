---
title: Ekran tutarlılığı — temel başta, denetim sonda
category: skills
tags: [arayuz, test, yordam, bizigo]
aliases: [T13 jetonları, T28 denetimi, dört durum, çok dilli gövde]
relationships:
  - target: "[[concepts/f2-bff-deseni]]"
    type: related_to
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: uses
  - target: "[[concepts/f2-olculen-kisit-ekrani-tasarlar]]"
    type: related_to
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/index.md
  - docs/epic/tickets-f2/nextjs-iskelet-bff/index.md
  - docs/epic/tickets-f2/ui-ux-denetimi/index.md
  - docs/ekran-goruntuleri/BENIOKU.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/ekran-goruntuleri/BENIOKU.md=cf326ea2a6f4 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/index.md=5e6346095323 docs/epic/tickets-f2/nextjs-iskelet-bff/index.md=c49270b469fb docs/epic/tickets-f2/ui-ux-denetimi/index.md=0901dadc890e"
summary: Görsel tutarlılık F2'de kasten iki yere bölündü — jetonlar T13'te kuruluyor, denetim T28'de yapılıyor. Denetim toparlama işine dönüşüyorsa temel eksik yapılmış demektir.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:45:00Z
updated: 2026-08-24T17:45:00Z
---

# Ekran tutarlılığı — temel başta, denetim sonda

F2'nin bitiş şartlarından biri ekranların **birlikte bir ürün gibi**
görünmesi. Teknik plan bunu bir rötuş saymayı açıkça reddediyor:

> Her ekran kendi düğmesini çizerse sonda toparlanmıyor.

Bu yüzden iş **kasten ikiye bölündü** ve iki uç ticket'a verildi.

## Bölünme

| Nerede | Ne |
| --- | --- |
| **T13** (ilk ticket) | Renk / tipografi ölçeği / boşluk / köşe yarıçapı / gölge jetonları, açık-koyu tema, ortak bileşenler: tablo, boş durum, hata durumu, yükleniyor, form alanı |
| **T28** (son ticket) | Tutarlılık denetimi, dört durum kapsaması, erişilebilirlik, duyarlılık, kanıt |

Ve aralarındaki tek cümlelik bekçi:

> **T28 bir toparlama işine dönüşüyorsa T13 eksik yapılmış demektir.**

Yani T28 bir *yapma* değil bir *doğrulama* ticket'ı. Ölçütü de bu: denetimde
çıkan iş miktarı, temelin ne kadar sağlam kurulduğunun ölçüsü. ^[inferred]

## Bu üründe özel risk: çok dilli gövdeler

Log gövdeleri Türkçe, Arapça ve Çince geliyor ve F1'in arşivinde **gerçek
örnekleri var**. T28 bunu estetik değil kullanılabilirlik sorunu sayıyor —
hücre taşarsa ekran kullanılamaz hâle geliyor.

- **Uzun satır:** 500+ karakterlik tek satır tabloyu yatay kaydırmaya
  sokmamalı; kırpma ve genişletme davranışı belirli olmalı.
- **Arapça (RTL):** gövde sağdan sola, arayüz soldan sağa. `dir="auto"` yeterli
  mi — **ölçülmeli**, varsayılmamalı.
- **CJK:** boşluksuz metinde `word-break` yanlışsa hücre tek kelime gibi
  davranıp düzeni patlatıyor.
- **Türkçe `İ`/`ı`:** F1'de derleme zamanında zorlanıyor (CA1304/CA1311); UI
  karşılığı `toLocaleUpperCase` kullanımının gözden geçirilmesi.
- Sayı ve tarih biçimleri kullanıcının yereline göre.

Aynı kısıt arama ekranının eşiğinde de karşımıza çıkıyor ve orada **uzunluk
sorunu** olduğu ölçülmüştü: [[concepts/f2-olculen-kisit-ekrani-tasarlar]].

## Dört durum — ve her birinin gerekçesi

| Durum | Neden önemli |
| --- | --- |
| Boş | Yeni kurulumda **her** ekran boş açılıyor; ilk izlenim burası |
| Yükleniyor | ClickHouse sorgusu saniyeler sürebiliyor |
| Hata | API `{ error, hint }` veriyor — **`hint` gösteriliyor mu** |
| Çok veri | 1M satırlık ortamda tablo, filtre listesi, sayfalama |

Hata satırı bu deponun tarzına özgü: sorun "hata ekranı var mı" değil,
**API'nin verdiği ipucunun kullanıcıya ulaşıp ulaşmadığı**. Aynı refleks
T16'da da var — ham bulunamadığında API'nin ipucu gösteriliyor, sessiz boş
ekran değil.

## Bekçiler kümesini jetonlardan alıyor

Kapanış §4'te `ui-consistency` ve `contrast` bekçileri kümesini **jetonlardan
türetiyor** — elle yazılmış bir bileşen listesinden değil. Bu, aynı belgede beş
kez kör çıkan kalıbın tersine gitmek demek:
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

T28 sevk edildiğinde **yedi bulgu** çıktı (dördü denetleyenin kendi
ekranlarında), hepsi düzeltildi ve **altı bekçiyle** sabitlendi; altısının da
kırmızı yanabildiği ölçüldü.

## Bekçinin göremediği sınıf: yerleşim

18 ekran görüntüsü alındı (9 sahne × açık/koyu tema) ve **iki bulguyu daha**
ortaya çıkardılar. İkisi de bekçilerin göremeyeceği sınıftan, çünkü bozulan şey
HTML ya da kural değil **yerleşim**di
(`docs/ekran-goruntuleri/BENIOKU.md`):

| Bulgu | Neden test göremezdi |
| --- | --- |
| `DataTable` gövde hücresi tablo hücresi olmaktan çıkıyordu; uzun gövdenin son satırı tablonun alt kenarından **yarım** taşıyordu | HTML çıktısı doğru, kural doğru; bozulan **yerleşim** |
| Önem sütunu 7rem'di, "belirtilmemiş" kelime ortasından kırılıyordu | Metin HTML'de tam; kırılma tarayıcıda oluyor |

Ve bir kademe daha sinsisi: ilk koşumda sahneler **ortak bileşen CSS'i olmadan**
alınmıştı — hücreler ortalanmış, rozetler düz metin çıkmıştı. **Test yeşildi**,
çünkü sayfanın boyanıp boyanmadığına bakıyordu, doğru göründüğüne değil.
Görüntülere bakılmasaydı fark edilmezdi. Bu, [[concepts/sessiz-yanlis-davranis]]
sınıfının görsel katmandaki karşılığı.

## Kapsamı: ne kanıtlıyor, ne kanıtlamıyor

Sunucu ayağa kaldırılmadı; sahneler **bileşen düzeyinde**, gerçek jetonlar ve
gerçek bileşen CSS'iyle.

- **Kanıtlıyor:** çok dilli gövdelerin kırpılması ve hizalanması, rozet
  kontrastı, 500 satırlık tablonun düzeni bozup bozmadığı.
- **Kanıtlamıyor:** Next yönlendirmesi, kimlik akışı, düzen birleşimi. Onlar
  için sunucu + sahte Keycloak + sahte API gerekiyordu; bunlar T27'nin uçtan uca
  akışlarına bırakıldı.

Sınırın **yazılı** olması yordamın kendisi kadar önemli: kapsamı yazılmayan bir
kanıt, kanıtlamadığı şey için de kanıt sanılıyor. ^[inferred] Uzun ömürlü
prosesten kaçınma gerekçesi çalışma protokolünden geliyor:
[[skills/paralel-ajan-koordinasyonu]].

## Yordamın kontrol listesi

- [ ] Renk/boşluk **yalnızca** jetonlardan mı geliyor; tek kullanımlık hex var mı?
- [ ] Aynı işi yapan bileşenler aynı mı (tablo, filtre çubuğu, boş/hata/yükleniyor, onay diyaloğu)?
- [ ] Yedi ekranın her biri dört durumda, açık ve koyu temada görüntülendi mi?
- [ ] Aynı tablo Türkçe, Arapça ve Çince uzun satırlarda bozulmuyor mu?
- [ ] Kontrast (WCAG AA) ve klavyeyle tam gezinme geçiyor mu?
- [ ] Bulunan her sorun ya düzeltildi ya **gerekçesiyle** kayda geçti mi?

## Not

Grafik/histogram F2 kapsamı dışında. Eklendiği gün `dataviz` yönergesi devreye
girmeli — renk paleti ve grafik seçimi kendi başına bir disiplin.

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — "Görsel tutarlılık iki yere bölündü"
- `docs/epic/tickets-f2/index.md` — bitti tanımının 7. maddesi
- `docs/epic/tickets-f2/nextjs-iskelet-bff/index.md` — T13 tasarım temeli
- `docs/epic/tickets-f2/ui-ux-denetimi/index.md` — T28 kapsam, çok dilli risk, sevk durumu
- `docs/ekran-goruntuleri/BENIOKU.md` — görüntülerin kanıtladığı ve kanıtlamadığı
- `docs/epic/f2-kapanis/index.md` — §4 `ui-consistency` / `contrast` bekçileri
