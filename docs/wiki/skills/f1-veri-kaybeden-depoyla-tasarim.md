---
title: Veri kaybedebilen bir depoyla tasarım
category: skills
tags: [veri-deposu, test, yordam, bizigo]
aliases: [RustFS beta, manifest koruması, arşiv scrub]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[concepts/f1-ham-sadakat-zinciri]]"
    type: extends
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: part_of
sources:
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/tickets/ham-arsiv/index.md
  - docs/epic/tickets/ham-arsiv-kurtarma/index.md
  - docs/epic/tickets/replay/index.md
  - docs/epic/f1-kapanis/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=ed0e0490c47f docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=61628e5aed41 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/tickets/ham-arsiv-kurtarma/index.md=791799eef7a1 docs/epic/tickets/ham-arsiv/index.md=f7b6e1d7e3a0 docs/epic/tickets/replay/index.md=a6e378b9614b"
summary: Olgunlaşmamış bir nesne deposu (RustFS 1.0-beta) replay'in tek kaynağıyken tasarımın veri kaybını varsayması gerekiyor. Beş koruma, en değerlisi manifest — ve T40'ın gösterdiği şey belgelenmiş bir korumanın mekanizmasız kalabildiği.
provenance:
  extracted: 0.87
  inferred: 0.13
  ambiguous: 0.0
base_confidence: 0.84
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Veri kaybedebilen bir depoyla tasarım

K25 bilinçli olarak rahatsız edici bir karar: ham arşiv replay'in **tek
kaynağı**, ve o arşivin altındaki depo (RustFS) `1.0.0-beta` serisinde —
geliştiricilerin kendi tavsiyesi 1.0 stable öncesi üretimde kullanmamak, dağıtık
mod ve KMS "under testing" (`docs/epic/mimari-kararlar/index.md` §3.5).

Karar değişmedi; değişen tasarım oldu: **RustFS'in veri kaybetmesi ihtimalini
varsayarak** kurulmuş bir arşiv. Bu sayfa o yordamı, F1 planından ticket'lara ve
oradan T40'ın açtığı boşluğa kadar birleştiriyor.

## Beş koruma — hepsi bağımsız çalışıyor

`docs/epic/f1-teknik-plan/index.md` §7.0'daki liste, T04'te uygulanan hâliyle:

| # | Koruma | Ne sağlar |
| --- | --- | --- |
| 1 | **Yalnızca S3 API** (`AWSSDK.S3` + özel endpoint), depoya özel çağrı yok | SeaweedFS / kurumun S3'ü / Garage'a geçiş **config işi**, kod değil |
| 2 | **Dayanıklılık sınırı WAL'dır**, depo değil | Depo kesintisi ingest'i durdurmuyor, veri kaybettirmiyor |
| 3 | Segment, yüklendiği **doğrulandıktan** sonra +48 saat daha tutuluyor | Son 48 saatte kaybolan nesne yerelden geri yüklenebilir |
| 4 | **Manifest Postgres'te** (`object_key, sha256, byte_size, event_count, ts_from, ts_to, verified_at`) | Nesne kaybolursa **ne kaybolduğu bilinir** |
| 5 | **Periyodik scrub** — örneklenen nesneler indirilip sha256 doğrulanıyor | Sessiz bozulma replay anında değil, olduğu gün görülüyor |

Dördüncüsü tek başına en değerlisi ve gerekçesi bir cümlede duruyor: manifest
olmadan *"replay 7 gün yerine 5 gün döndü"* durumu **fark edilmez**; manifest'le
bu bir hata mesajı olur.

Replay tarafında karşılığı yazılmış: manifest'te olup arşivde bulunmayan nesne
varsa replay **duruyor** ve `409` ile eksik listesini döndürüyor; devam etmek açık
bir bayrak istiyor. Sessizce kısa dönmek, manifest'in var olma sebebini ortadan
kaldırırdı (`docs/epic/tickets/replay/index.md`).

Bu, [[concepts/sessiz-yanlis-davranis]] sınıfına karşı kurulmuş bir savunma:
kaybın kendisi engellenemiyor, **sessizliği** engelleniyor.

## T40 — belgelenmiş koruma, olmayan mekanizma

En öğretici parça burada. Koruma #3 ("kaybolursa yerelden yeniden yüklenir")
belgeye yazılmış, saklama süresi ona göre seçilmiş, kayıp nesnenin geldiği
segmentin adı bile manifest'te tutulmuştu (`RawManifestEntity.WalSegment`, kendi
XML yorumu ikinci işini açıkça söylüyor). Ama:

> **Bağ kurulmuş, veri yerinde, mekanizma yok.** `Missing` yazılıyor ve orada
> bitiyor. Saklama süresi, var olmayan bir mekanizmanın gereksinimine göre
> seçilmiş. — `docs/epic/tickets/ham-arsiv-kurtarma/index.md`

Mekanizma yazılmadan önce kod okunmuş ve iki bulgu çıkmış; ikisi de tasarımı
değiştirdi:

**Bulgu 1 — kayıp tespiti kurtarma kaynağını korumuyordu.** Segment silme kararı
yalnızca `VerifiedAt`'e bakıyordu; `State` sorguya hiç girmiyordu. Yani bir nesne
önce doğrulanıp sonra kaybolduysa, satır silme kararında hâlâ "doğrulanmış ve
süresi dolmuş" görünüyordu — **kaybın tespit edilmiş olması, kurtarma kaynağının
silinmesini engellemiyordu.**

**Bulgu 2 — tespit hızı kurtarma penceresinden habersizdi.** Üç sayı birbirinden
bağımsız duruyordu ve üçünün birlikte anlamı hiçbir yerde yazmıyordu:

| Değer | Kaynak |
| --- | --- |
| Scrub turu | 6 saatte bir |
| Tur başına nesne | 20 |
| Kurtarma penceresi | 48 saat |

Aritmetik: 8 tur × 20 = **160 nesne**, ~64 MB hedef boyutla yaklaşık **10 GB**.
Arşiv bundan büyükse tam tur 48 saatten uzun sürüyor ve kayıp, kurtarma kaynağı
silindikten **sonra** fark ediliyor — yani koruma, mekanizma yazılsa bile belli
bir arşiv boyutundan sonra **aritmetik olarak erişilemez** hâle geliyor.

## Yordam: böyle bir korumayı yazarken

T40'ın uygulanan kapsamı, tekrar kullanılabilir bir kontrol listesi:

1. **Tespit, kurtarma kaynağını kilitlesin.** `Missing` / `ChecksumMismatch`
   durumundaki bir satırın segmenti silinmiyor. Bu, mekanizmanın ön koşulu.
2. **Kurtarma tespitten tetiklensin, ayrı bir zamanlamadan değil.** Gerekçe
   ölçülebilir: kurtarma kendi takvimiyle koşarsa 48 saatlik bütçe *iki bağımsız
   periyot arasında bölünür* — yani kimsenin toplamını tutmadığı bir bütçe.
3. **Elle tetiklenen uç ikincil kalsın.** Birincil olsaydı koruma yine bir insanın
   bakmasına bağlanırdı, ve bu depoda tam o sınıfta bir kalem patladı: CI dört
   birleştirme boyunca kırmızıydı, kimse bakmadı (`CLAUDE.md` §5). Aynı sebeple
   T40'ta elle uç hiç yazılmadı.
4. **Kurtarmanın kendisi arşivi bozmasın.** Yeniden kurulan nesnenin sha256'sı
   manifest'tekine eşit değilse **yazma yok** — manifest'in kaydı doğrudur,
   sapma kurtarmadadır.
5. **Sonsuz döngüyü say.** Bozuk bir S3 yapılandırmasında yeniden yazma sonsuza
   gidebilir; deneme sayısı bir üst sınırda durup **kurtarılamaz** işaretlenmeli.
   `Unrecoverable` bilinçli olarak ayrı bir durum oldu: `Missing`'de kalsaydı
   *"kurtarma sırasını bekliyor"* ile *"denendi, olmadı"* ayırt edilemezdi.
6. **İlişkiyi yapılandırmada görünür kıl.** Tam tarama süresi açılışta hesaplanıp
   loglanıyor; pencereyi aşıyorsa **uyarı**. Bu ticket'ın ürettiği en değerli şey
   muhtemelen kod değil, iki bulgunun birbirine bağlanmış olması.
7. **İkinci kurulum yolu yazma.** `BuildObjects` ayrıldı ve yükleme ile kurtarma
   onu birlikte kullanıyor. İki ayrı kurulum yolu farklı sha256 üretirdi;
   kurtarma "manifest yanlış" diye dururdu ve gerçek sebep başka bir dosyada
   aranırdı — yani ayrışma tam olarak **kurtarmanın yakalayamayacağı** yerde
   ortaya çıkardı (`CLAUDE.md` §9).

Yan etki kayda geçmiş: kurtarma `VerifiedAt`'i tazelediği için saklama saatini
yeniden başlatıyor. Kasıtlı — pencere, kaybın en olası olduğu dönemi kapsamak
için var ve *az önce yazılmış* bir nesne tam olarak o dönemde.

## Bekçiler konteyner istemiyor

T40'ın dokuz bekçisinden yalnızca biri gerçek depo gerektiriyor; kalanı bellek
içi sahte depoyla koşuyor. Bu bilinçli bir tercih ve dayanağı F1 kapanışının
notu: **beş hatanın dördü konteyner gerektirmeden yakalanabiliyordu**
(`docs/epic/f1-kapanis/index.md`). Ajan/koordinatör test bölünmesiyle de
uyumlu — ağır koşum koordinatörde ([[skills/paralel-ajan-koordinasyonu]]).

Bölünmenin ekseni sonradan düzeltilince bu gözlem **sonuç da doğurdu**:
konteyner istemeyen bir bekçiyi ajan artık koşturabiliyor (Docker kapalıyken,
ve yalnızca **geçen** test kanıt sayılarak). Yani "konteyner gerekmiyor" bir
tasarım tercihi olmaktan çıkıp koşum hakkı hâline geldi. ^[inferred]

Bu, aynı cümlenin bu depodaki **üçüncü** anlamı ve en yenisi:
[[concepts/konteyner-gerekmiyor-uc-iddia]].

İlk bekçi ayrıca **kırmızı yanabildiği ölçülerek** yazıldı: bugünkü kodla iki
bekçi düştü, sonra düzeltme kondu (`CLAUDE.md` §6).

## Açık kalan

- Scrub örnekleme oranı ve saklama süresi hâlâ **ölçülmedi**; T40 yalnızca
  ilişkiyi görünür kıldı, doğru sayıları gerçek arşiv boyutuyla seçmek ayrı iş.
- Uçtan uca kurtarma gerçek RustFS'e karşı koşturulmadı.
- İzlenecek kalem: RustFS 1.0 GA çıktığında sürüm yükseltme ve dağıtık modun
  yeniden değerlendirilmesi (`docs/epic/mimari-kararlar/index.md` risk #13).

## Kaynaklar

- `docs/epic/mimari-kararlar/index.md` — K25, §3.5, risk #13
- `docs/epic/f1-teknik-plan/index.md` — §7.0, beş koruma
- `docs/epic/tickets/ham-arsiv/index.md` — T04 uygulaması, kaçış planı sırası
- `docs/epic/tickets/ham-arsiv-kurtarma/index.md` — T40, iki bulgu, dokuz bekçi
- `docs/epic/tickets/replay/index.md` — eksik nesnede `409` davranışı
- `docs/epic/f1-kapanis/index.md` — konteynersiz yakalanabilen hatalar
- `CLAUDE.md` §5, §6, §9
