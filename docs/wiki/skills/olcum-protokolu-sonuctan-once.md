---
title: Ölçüm protokolünü sonuçtan önce yaz
category: skills
tags: [test, surec, yordam, bizigo]
aliases: [ön kontrol, geçersizleme kriteri, karar değişkeni]
relationships:
  - target: "[[concepts/olcum-sayi-kapsam-degil]]"
    type: uses
  - target: "[[concepts/olcum-duvar-saati]]"
    type: uses
  - target: "[[skills/olcum-bekciyi-kirmizi-yakmak]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: related_to
sources:
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/t12-kararlar/index.md
  - docs/epic/t27-kapanis-taramasi/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=d1f9862fea36 docs/epic/t12-kararlar/index.md=454710cef295 docs/epic/t27-kapanis-taramasi/index.md=fc23cd892d11 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t39-alan-kapsami/index.md=7d06bfcf6e0c"
summary: Hangi sayının hangi kararı vereceği, ölçüm koşmadan önce bağlanıyor — sonuç geldikten sonra gerekçe uydurulmasın diye. Ön kontrol, geçersizleme kriteri, payda ve zayıf halka önceden yazılı.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:31:44Z
updated: 2026-08-24T17:31:44Z
---

# Ölçüm protokolünü sonuçtan önce yaz

T30'un tek cümlesi bu yordamın tamamını taşıyor:

> Karar canlı sayılarla verilecek ama **hangi sayının hangi kararı verdiği
> şimdiden bağlı**, ki sonuç geldikten sonra gerekçe uydurulmasın.

Bu dilimde iki ölçüm (T29 sıcak yol, T30 Sigma kapsamı) ve bir keşif aracı (T39
alan kapsamı) aynı iskeleti kullanıyor. Aşağıdakiler o iskeletin parçaları.

## 1 · Karar değişkenini ve dallarını önceden yaz

T30 karar değişkenini tanımlıyor — `match_ratio = matches ÷ measurable` — ve üç
dallı karar tablosunu **ölçümden önce** yayımlıyor: `≥ %70` dört vendor dört
kategori, `%40–%70` yalnızca `firewall + network_connection`, `< %40` tek vendor.

T29'da eşik ticket'ta duruyor: *"olay başına maliyeti iki katına çıkarıyorsa K35
yeniden değerlendirilmeli."* Ölçülen `1,46×` o eşiğin altında ve marjı var.

İki örnekte de eşik ölçümden bağımsız; bu, [[concepts/olcum-kirmizi-yanamayan-sayi]]
merdiveninin 3. basamağını ölçümden **önce** kurmak demek.

## 2 · Kararı geçersiz kılacak bulguları da önceden yaz

T30 üç geçersizleme kontrolü listelemiş ve tablonun dayandığı varsayımı açıkça
söylemiş (*maliyet ayrık alan sayısıyla büyür, kural sayısıyla değil*):

| # | Bulgu | Anlamı |
| --- | --- | --- |
| 1 | `untouched` yüksekse | sorun kapsam değil eşlemenin kendisi |
| 2 | `compiled` yüksek, `runs` düşükse | SQL var olmayan kolonlara gidiyor; kapsamı daraltmak çözmez |
| 3 | alan başına maliyet kural başına maliyetle birlikte büyüyorsa | varsayım yanlış; üç dal da fazla iyimser |

Ve işe yaradı: 2. kontrol **iki koşum boyunca kapsam kararını bloke etti**
(24 kuralın 10'u ClickHouse'un reddettiği SQL üretiyordu), üçüncü koşumda
`compiled == runs` olunca kapandı. 1. kontrol hiç tetiklenmedi.

Belge geçersizleme dilini de sabitliyor: bulgu çıkarsa öneri *düzeltilmeli*
değil **yeniden yazılmalı**.

## 3 · Ön kontrolü geçemezse ölçme

T30'un koşumu ölçmeye başlamadan önce `events_ocsf`'e bakıyor ve geçemezse
**ölçüm hiç yapılmıyor** (çıkış kodu 3). Üç durum ayrı raporlanıyor — sorgu hata
verdi / satır sayısı sıfır / satır var — çünkü üçünün cevabı farklı.

Gerekçe protokolün kendisinde: boş bir görünüme karşı koşulan ölçüm her kural
için `matches=false` üretir ve o tablo *"kapsam düşük"* diye okunur.

### Ön kontrolün iki kez kırıldığı ölçüldü

Bu, yordamın en öğretici kısmı, çünkü **ön kontrolün kendisi iki kez başarısız
oldu** ve iki başarısızlık farklı sınıftan:

| Koşum | Ne oldu | Sınıf |
| --- | --- | --- |
| **Birinci** | Tablodaki 1.000.001 satır altın örnek değildi — önceki turdan kalan tek-vendor'lu sentetik benchmark verisi. Ön kontrol *"tablo boş mu"* diye sordu, cevap hayırdı, geçirdi | **yanlış pozitif** |
| **İkinci** | Ön kontrol dört vendor için de *"altın örnek YOK"* dedi **ve buna rağmen ölçümü yaptı** — `if probes and not any(golden.values())` deseninde boş sonda listesi bekçiyi kapatıyordu | **yanlış negatif + atlanan kapı** |

Birincinin dersi belgede: *"'Boş değil' ile 'doğru veri' aynı şey değil."* Ön
kontrol bir **yokluk kanıtı** yerine **varlık kanıtı** aramaya çevrildi — her
vendor'ın altın örnek dosyasındaki en uzun satırdan türetilen 60 karakterlik bir
sonda `raw_data` içinde gerçekten duruyor mu.

İkincinin dersi daha ağır ve belge doğrudan `CLAUDE.md` §7'ye bağlıyor: *"bir
bekçinin sessizce atlaması, bekçinin kendisinden tehlikelidir"* — `Produces<T>`
kapısının elle yazılmış listeden beslenmesiyle aynı desen, bkz.
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

Düzeltmenin biçimi de kayda değer: **boş liste bir cevap değil, arıza sayılıyor**;
`_repo_root()` bulamayınca sessizce `here.parent`'a çekilmiyor, `None` dönüyor;
sonda sorgusu hatası yutulmuyor; ve reddedince **aranan sondalar basılıyor** ki
operatör elle doğrulayabilsin.

## 4 · Paydayı önceden tanımla

Ayrıntısı [[concepts/olcum-sayi-kapsam-degil]]'de. Protokol tarafındaki kural:
`no_data` ve `absent` paydadan düşülüyor (*ölçemedik*), `blocked` düşülmüyor
(*ürünün sınırı*). T30 bu ayrımı ölçüm aracının kendi testine
(`test_measure.py`) sabitlemiş.

## 5 · Ölçümü ağır bağımlılığa bağlama

T29'un notu kısa ve nedeni yazılı:

> **Docker, canlı sidecar, Python venv gerekmiyor:** ölçülen şey saf CPU işi.
> Ölçümü venv'e bağlamak, hiç koşulmamasının en kolay yolu olurdu.

Bu cümle bir **bağımlılık** iddiası: az bağımlılık taşıyan ölçüm daha sık
koşuluyor. Bir **aşama** ya da **izin** iddiası değil — aynı cümlenin bu depoda
üç ayrı anlamı var ve karıştırılmaları pahalı:
[[concepts/konteyner-gerekmiyor-uc-iddia]]. ^[inferred]

Aynı belge raporu `$TMPDIR/t29-hotpath.log`'a da yazıyor — *xunit konsolu
yutarsa sayı yine de duruyor.*

Karşı örnek aynı dilimde: T12'nin `SidecarLiveTests`'i `BIZIGO_SIDECAR_LIVE=1` +
`sidecar/.venv` istiyor, CI'da ikisi de yok, ve **hiç koşmadı** — T12'nin
taşıyıcı iddiası (*sidecar arızalıyken throughput düşmüyor*) bu yüzden bugün
"mantıklı ama ölçülmemiş" durumda.

## 6 · Zayıf halkayı yaz

T29 kendi ölçümünün en zayıf yerini işaretliyor: B arm'ı üretimde artık yok, o
yüzden ölçümde yeniden kuruluyor (dört satır, belirli bir commit'teki hâliyle
birebir). Ve alternatifin neden daha kötü olduğu da yazılı — iki commit'i iki
ayrı süreçte koşturup karşılaştırmak, bkz. [[concepts/olcum-duvar-saati]].

T30 aynı dürüstlüğü örneklem tarafında gösteriyor: örneklem SigmaHQ'dan
indirilmedi, kendi altın örneklerimize karşı yazıldı — gerekçesi **ve bedeli**
birlikte yazılı: *"269 kuralın maliyetini doğrudan çarparak bulmak yanlış olur."*
Doğru ölçekleme yolu üç adımda tarif edilmiş ve çarpımın **ayrık alan** üzerinden
yapılması gerektiği söylenmiş.

## 7 · Ölçümün neye bakmadığını da yaz

T39'un `fields values` ölçümü **veriye hiç bakmıyor** ve belgeye göre bakmaması
asıl özelliği: örneklemde bir değerin bulunmaması *"bugün yok"*, şemanın onu
üretememesi *"hiçbir zaman olmayacak"* demek — ikisi aynı tabloda aynı görünüyor
ve **verdikleri iş emri zıt**.

Aynı belge bir tabloyu bilerek yazmıyor: eşanlamlı tablosu yok, çünkü *"o tabloyu
yazmak, ölçümün cevaplaması istenen soruyu ölçümün girdisine taşımak olurdu."*

## Kontrol listesi

- [ ] Karar değişkeni ve dalları ölçümden **önce** yazıldı mı?
- [ ] Kararı geçersiz kılacak bulgular listelendi mi?
- [ ] Ön kontrol bir **varlık kanıtı** mı arıyor, yoksa yalnızca yokluk mu?
- [ ] Ön kontrolün boş girdiyle nasıl davrandığı sınandı mı? (T30'da iki kez kırıldı)
- [ ] Payda tanımlı mı; hangi kutu neden düşülüyor?
- [ ] Ölçüm ağır bir bağımlılığa bağlı mı? Bağlıysa koşulmama riski yazıldı mı?
- [ ] Zayıf halka ve ölçümün **bakmadığı** şey belgede duruyor mu?
- [ ] İki koşum kaydedildi mi? (`CLAUDE.md` §6)

## Kaynaklar

- `docs/epic/t30-sigma-olcumu/index.md` — protokol, ön kontrol, birinci/ikinci/üçüncü koşum
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §3 kurulum ve koşturma
- `docs/epic/t39-alan-kapsami/index.md` — "Dördüncü ölçüm", "Kabul edilmiş sınırlar"
- `docs/epic/t12-kararlar/index.md` — §5, koşmayan canlı test
- `docs/epic/t27-kapanis-taramasi/index.md` — §5, koşturulmamış testin yeşil sayılmaması
- `CLAUDE.md` §2, §6
