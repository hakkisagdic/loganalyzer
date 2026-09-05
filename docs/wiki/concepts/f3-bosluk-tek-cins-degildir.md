---
title: Boşluk tek cins değildir
category: concepts
tags: [test, mimari, kavram, bizigo]
aliases: [yokluğun cinsleri, boş sonuç ayrımı, NeverFed]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
  - target: "[[concepts/f3-kanit-once-akil-sonra]]"
    type: related_to
  - target: "[[skills/f3-sigma-derleme-kapilari]]"
    type: related_to
sources:
  - docs/epic/t34-kanit-sozlesmesi/index.md
  - docs/epic/t36-devir-notu/index.md
  - docs/epic/t37-rapor-ekrani/index.md
  - docs/epic/t32-derleme-tasarimi/index.md
  - docs/epic/t32-t33-acik-sorular/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/tickets-f3/kural-yonetimi/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=dd011df5a833 docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t32-derleme-tasarimi/index.md=ca8f23c25262 docs/epic/t32-t33-acik-sorular/index.md=131d289ee01f docs/epic/t34-kanit-sozlesmesi/index.md=8cb4e8b028b3 docs/epic/t36-devir-notu/index.md=5027982dbb85 docs/epic/t37-rapor-ekrani/index.md=53dc34e77027 docs/epic/tickets-f3/kural-yonetimi/index.md=1555ee581f73"
summary: F3'ün en çok tekrarlanan kararı — "bir şey yok" diyen farklı olgular aynı boş kutuya düşerse okuyucu iyimser yanılır ve hiçbir hata mesajı bunu bozmaz. Faz boyunca en az yedi kez ayrı ayrı kuruldu.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.84
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Boşluk tek cins değildir

[[concepts/sessiz-yanlis-davranis]] sınıfının F3'teki baskın biçimi. Kural tek
cümle:

> **Farklı sebeplerle boş kalan iki şey aynı kutuya düşerse, okuyan iyimser
> yanılır ve hiçbir hata mesajı bunu bozmaz.**

T32 tasarım belgesi bunu bir kez fark edip adını koydu — aynı ayrımın *dördüncü*
kuruluşu olduğunu yazarak (`docs/epic/t32-derleme-tasarimi/index.md`, `none`
beyanı bölümü). Belgeler yan yana konduğunda sayı daha da yüksek: F3 boyunca
**birbirinden bağımsız en az yedi yerde** aynı karar verildi.

## Aynı kararın yedi kuruluşu

| Katman | Ayrılan iki şey | Kaynak |
| --- | --- | --- |
| **Kanıt sağlayıcı** | `Empty` (baktık, yok) ↔ `NeverFed` (besleme hiç bağlı değil) | `docs/epic/t34-kanit-sozlesmesi/index.md` §1 |
| **Kanıt toplayıcı** | `Unavailable`/`Failed` ↔ `NotRegistered` (bu türe hiç bakılmadı) | aynı belge |
| **Rapor / ekran / export** | Dört durumun tek "veri yok" kutusuna indirgenmemesi | `docs/epic/t36-devir-notu/index.md` §0, `docs/epic/t37-rapor-ekrani/index.md` §1 |
| **Zaman güvenilirliği** | `Measured=false` (ölçülemedi) ↔ `Unreliable=0` (ölçüldü, temiz) | `docs/epic/t36-devir-notu/index.md` §6 |
| **Kural durumu** | `pasif` (kullanıcı istemedi) ↔ `gated` (biz yapamadık) | `docs/epic/t32-t33-acik-sorular/index.md`, `docs/epic/tickets-f3/kural-yonetimi/index.md` |
| **Derleme durumu** | `gated` (derlendi, koşmuyor) ↔ `failed` (derlenemedi = build kırık) | `docs/epic/t32-derleme-tasarimi/index.md` §4 |
| **Beyan** | `none.kind = invariant` (yanlış pozitif doğdu) ↔ `corpus_gap` (korpus genişledi) | `docs/epic/t32-derleme-tasarimi/index.md` §3 |
| **Ölçüm paydası** | `no_data` (veri yüklenmemiş) ↔ `blocked` (bu şemada hiç ölçülemez) ↔ `absent` (desen örneklemde yok) | `docs/epic/t30-sigma-olcumu/index.md` |
| **Depoda yokluk** | "örnek dosyada yok" ↔ **"örnek dosyada var, veritabanında yok"** (TTL sildi) | `docs/epic/t32-derleme-tasarimi/index.md` §3, FS kök neden analizi |
| **Ölçüm ortamı** | yük ortalaması `None` (bakmadık) ↔ okunmuş düşük değer (sessizdi) | `docs/epic/t32-derleme-tasarimi/index.md` §6 |

Sonuncusu ayrımın **ölçüm aracının kendi üstverisine** uygulanması: bir süre
ölçümünün yanında yük kaydı yoksa, o koşumun sessiz bir makinede alındığı
*gösterilemiyor* — ama kayıt atlanırsa okuyan kişi sessiz varsayıyor. Araç bu
yüzden alanı `None` yazıp raporda adıyla söylüyor.

## Neden her biri pahalı

Ayrımın kaybolduğu her satırın somut bir bedeli yazılmış:

- **`NeverFed` → "değişiklik olmadı".** Change beslemesi hiç bağlanmamışken
  rapor bunu bir **bulgu** gibi gösterirse, kullanıcı RCA'nın en güçlü
  sinyalinin yokluğunu bir sonuç sanır ve kök nedeni başka yerde arar. T36 devir
  notu bunu dört durumun **en pahalısı** diye işaretliyor.
- **`gated` → "pasif".** Kullanıcı kapalı bir kuralı açmayı dener, açılmaz ve
  sebebini de göremez. Daha kötüsü ölçekte: `compiled=24, runs=14` — derlenenlerin
  **%40'ı**. Görünmediğinde kullanıcının modeli *"SigmaHQ'da X için kural var,
  biz Sigma koşturuyoruz, demek ki X'i yakalıyoruz"* oluyor. T32 buna doğrudan
  ad koyuyor: **sahip olmadığın tespit kapsamına sahip olduğunu sanmak.** Yanlış
  çalışan bir alarm fark edilir; **hiç var olmayan bir alarm fark edilmez.**
- **`invariant` → `corpus_gap`.** İkisi kırmızı yandığında **zıt haber** verir:
  biri yanlış pozitif doğduğunu, diğeri korpusun genişlediğini söyler. Üstelik
  sayıları da ters yönde beklenir — biri sabit kalmalı, diğeri azalmalı.
- **`blocked` → `absent`.** İkisi de "ölçemedik" demiyor: biri fixture'ın
  eksikliği, diğeri **ürünün sınırı**. Paydaya etkisi bir karar dalını
  değiştiriyor ([[concepts/f3-oranin-paydasi]]).

## Ayrımı yaşatan mekanizmalar

Bir ayrımın yazıda durması yetmiyor; F3 üç ayrı mekanizmayla çiviledi:

1. **Ayrımı üreten taraf ayrımı uydurmuyor.** `NotRegistered`'ı sağlayıcı değil
   toplayıcı üretiyor — F5 türleri için boş bir sağlayıcı kaydetmek onları
   *"var ama sonuç yok"* gibi gösterirdi (`t34-kanit-sozlesmesi`).
2. **Ayrım üç ayrı yerde sınanıyor.** Tel yüzeyi, export, ekran — üçü ayrı
   sorular, ve dördüncü bir test **ikisinin ayrışmadığını** tutuyor
   (`t37-rapor-ekrani` §1). Ekranda `data-status` bilinçli: rozet metni değişse
   bile ayrım DOM'da yaşıyor. Tanınmayan bir durum da **tanınmadığını
   söyleyerek** görünüyor.
3. **Boş liste ≠ boş bölüm.** "Bakılmayanlar" listesi boşsa bu *"her şeye
   bakıldı"* demek ve gösterilmeye değer bir bilgi; bölümün sessizce kaybolması
   yanlış olur.

## Kökeni ve kardeşi

Desen F3'te doğmadı. `CLAUDE.md` §8 aynı şeyi bir faz önce yazmıştı:

> "Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz. İkisi tek
> listedeyken "liste boşaldı mı" sorusunun cevabı asla evet olamaz.

`Pending` ile `Exempt`'in ayrı durması, `Exempt` sayısının `ExpectedExemptCount`
ile sabitlenmesi — muafiyet eklemek **iki ayrı bilinçli hareket** gerektirsin
diye. T32 bu deseni `gated` tarafına birebir taşıdı: `remedy`'ye `upstream`
değeri eklendi ve sayı ikiye bölündü (`gated_closeable` / `gated_upstream`).
`upstream` hiçbir sınıflandırıcı tarafından **kendiliğinden** atanmıyor;
muafiyet gibi, bilinçli bir hareketle konuluyor.

`unknown`'ın kapanabilirler tarafında durması da bilinçli: *"kapanamaz"* ile
*"kapanır mı bilmiyoruz"* aynı şey değil, ve bilinmeyeni muafiyete yazmak işi
listeden gizlerdi.

Aynı fikrin bekçi katmanındaki kardeşi:
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]] — orada kapı **bakmadığı**
şeyi görmüyor, burada okuyucu **bakılmadığını** göremiyor.

## Arayüz bu ayrımı yok etmekte iyi

T37'nin cümlesi bu sayfanın en pratik uyarısı:

> Arayüz, yalnızca sözcüklerde var olan bir ayrımı yok etmekte alışılmadık
> derecede iyi.

Beş durumun beşi de **doğal olarak boş bir kutu** gibi çizilir. Çizildiği an
rapor, bakmadığı bir şeye bakmış gibi görünür. Bu yüzden ayrımın son sınavı
sözleşmede değil **ekranda ve export'ta**.

## Sekizincisi ayrımın kendisine bir sınır koyuyor

Yedi kuruluşun hepsi **kararı** ayırıyor: "hangi anlamda yok?" Sekizincisi farklı
bir şey söylüyor — **kaydın kendisi kaybolabilir.**

`events` tablosunda `TTL toDateTime(ts) + INTERVAL 90 DAY` var ve vendor örnek
dosyaları 2015–2022 tarihleri taşıyor. ClickHouse süresi dolmuş satırı parçayı
oluştururken atıyor, **ama istemciye "yazdım" diyor.** Yani bir satır örnek
dosyada var olabilir ve veritabanında olmayabilir.

Kapı 3 için sonucu: bir `at_least_one` beyanı kuralla **hiç ilgisi olmayan** bir
sebeple düşer, bir `corpus_gap` beyanı **yanlış sebeple** geçer. İkisi de
ayrımın kendisini bozuyor — çünkü ayrım "yokluk hangi cinsten" diye soruyor ve
bu cins **soruyu soran tarafın göremediği** bir yerden geliyor.

**Bugün risk değil, ölçüldü:** `count() WHERE ts < now() - INTERVAL 90 DAY → 0`;
altın satırların `min(ts)`'si 17 gün önce, çünkü `GoldenSamplePlan` satırları bir
`Anchor` etrafına yayıyor ve dosyanın kendi tarihini taşımıyor.

**Ama sınır yapısal değil, yükleyicinin davranışına bağlı** — ve simülatörler
dosyanın kendi tarihini kullanma yönüne gidebilir. Ön kontrol vendor başına satır
sayısına bakıyor, yani **toplu** bir süpürmeyi görür, **kısmi** bir düşmeyi
görmez. Yedi ayrımı kuran mekanizmaların hiçbiri bu sekizincisini yakalamıyor.

## Açık soru

Ayrımın kaç kez daha kurulacağı — her yeni katman kendi `Empty`/`NeverFed`
çiftini yeniden keşfediyor gibi görünüyor. Ortak bir tip ya da kural adı var mı,
yoksa her katmanın kendi sözlüğünde mi kalması doğru? Belgeler bu soruyu
sormuyor. ^[inferred]

## Kaynaklar

- `docs/epic/t34-kanit-sozlesmesi/index.md` — boşluğun dört cinsi (tablo altı değer)
- `docs/epic/t36-devir-notu/index.md` — §0 çivilenmiş değişmez, §6 `WindowTrust`
- `docs/epic/t37-rapor-ekrani/index.md` — §1 ayrımın üç yerde sınanması
- `docs/epic/t32-derleme-tasarimi/index.md` — `gated`/`failed`, `none.kind`, `remedy`
- `docs/epic/t32-t33-acik-sorular/index.md` — `pasif` ↔ `gated`, `Pending`/`Exempt` atfı
- `docs/epic/t30-sigma-olcumu/index.md` — `no_data` / `blocked` / `absent`
- `docs/epic/tickets-f3/kural-yonetimi/index.md` — üçlü durumun ürün tarafı
- `CLAUDE.md` §8 — desenin kökeni
