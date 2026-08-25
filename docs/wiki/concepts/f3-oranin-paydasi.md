---
title: Bir oran paydasıyla birlikte bir sayıdır
category: concepts
tags: [test, log-analiz, kavram, bizigo]
aliases: [payda hatası, kapsam oranı, match_ratio]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[concepts/f3-bosluk-tek-cins-degildir]]"
    type: extends
  - target: "[[skills/f3-eslesmeyen-kural-teshisi]]"
    type: related_to
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
sources:
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t32-derleme-tasarimi/index.md
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/f3-yol-haritasi/index.md
source_digest: "sha256-12/v1 docs/epic/f3-yol-haritasi/index.md=473574f06a4f docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t32-derleme-tasarimi/index.md=457cb125d0d3 docs/epic/t39-alan-kapsami/index.md=7d06bfcf6e0c"
summary: Sigma kapsam oranı beş koşumda %0, %8, %25, %29 ve %43 çıktı; hiçbiri yanlış hesaplanmadı, hepsi farklı payda kullandı. İki payda arasındaki fark bir karar dalını değiştiriyor.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.85
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Bir oran paydasıyla birlikte bir sayıdır

F3'ün Sigma kolu aynı soruya beş farklı sayı verdi: **%0 · %8 · %25 · %29 ·
%43**. Hiçbiri hesap hatası değil — hepsi farklı bir payda kullanıyor, ve
paydanın hangisi olduğu **soruyu değiştiriyor**.

Bu, [[concepts/sessiz-yanlis-davranis]] sınıfının ölçüm aracındaki hâli: sayı
üretiliyor, kimse hata görmüyor, ve karar yanlış dala giriyor.

## Beş sayının seyri

| Koşum | Sayı | Neden geçersiz / ne ölçüyor |
| --- | --- | --- |
| 1 | **%0** | Tabloda önceki turdan kalma tek-vendor'lu **sentetik** veri vardı. Ön kontrol *"tablo boş mu"* diye sordu, cevap hayırdı, geçirdi |
| 2 | **%8** | Altın örnekler yüklendi ama ön kontrol **kendi kendini kapatmıştı** (`probes and not any(...)` — boş sonda listesi kapıyı hep `False` yapıyor) |
| 3 | **%25** | Payda 24 = örneklemin tamamı — bekçinin bilerek düşürdüğü 3 kural da içinde |
| 3′ | **%29** | Payda 21 = bloke olanlar hariç |
| 3″ | **%43** | Payda 14 = deseni altın örneklerde **var olan** kurallar |

Sonuncusu bile kesin değil: `docs/epic/t39-alan-kapsami/index.md` bir kuralı
`absent` kutusundan çıkardı ve payda **15** oldu.

## Ayrımın kendisi

`%25` ile `%29` **iki farklı soru**, ve ikisi de doğru:

| Soru | Payda | Sayı |
| --- | --- | --- |
| **Kapsam** — kataloğun ne kadarına hizmet edebiliyoruz? | 24 (bloke olanlar dahil) | %25 |
| **Eşleme kalitesi** — eşleyebildiklerimizin ne kadarı tutuyor? | 21 (bloke olanlar hariç) | %29 |

Bloke edilen kural bir **eşleme** kusuru değil (o alan bizde hiç yok) ama bir
**kapsam** kusuru: DNS kuralı koşmuyorsa DNS kapsamı iddia edilemez.

## Paydadan ne düşülür, ne düşülmez

Kural [[concepts/f3-bosluk-tek-cins-degildir]] ile aynı köke bağlı — hangi
boşluğun hangi cins olduğu paydayı belirliyor:

| Kutu | Anlamı | Paydada? |
| --- | --- | --- |
| `no_data` | O vendor'ın verisi hiç yüklenmemiş — **fixture eksikliği** | ❌ düşülür |
| `absent` | Kuralın aradığı desen örneklemde hiç yok — **korpus dar** | ❌ düşülür |
| `blocked` | Bu şemada **hiç ölçülemez** — ürünün sınırı | ✅ kalır |

`no_data` ile `blocked` ayrımı belgede bilinçli olarak vurgulanmış: ikisi de
"ölçemedik" demiyor. Birincisi fixture'ın eksikliği, ikincisi ürünün sınırı.

`absent`'in düşülmesi ise sonradan bulundu: `no_data` düşülüyordu, **`absent`
de düşülmeliydi** ve aynı gerekçeyle.

## Bedeli kozmetik değil — bir dal

Kapsam kararı bir tabloya bağlı ve tablo eşiklerle çalışıyor:

| `match_ratio` | Seçilen kapsam |
| --- | --- |
| ≥ %70 | dört vendor, dört kategori |
| %40 – %70 | yalnızca `firewall` + `network_connection` |
| < %40 | tek vendor (FortiGate) |

`%25` **`< %40`** dalına, `%43` **`%40–%70`** dalına düşüyor. **İki farklı
kapsam.** Yani payda hatası bir yuvarlama farkı değil, ürünün F3'te hangi
kuralları koşturacağı kararı.

Ve belge kendi hatasını da yazıyor: bu tuzağa **ilk iki koşumda düşüldü** —
`%25` koordinatöre "kapsam sayısı" diye verildi ve öyle okundu.

## `absent` bir üst sınır — çünkü metin ekseni sistematik olarak yanılıyor

Paydadan düşülen `absent` kutusunun her elemanı örneklem boşluğu değil.
`fortigate_user_auth_fail` bunu gösterdi: kural `status: 'failure'` arıyor, ham
FortiGate satırı `status="failed"` yazıyor. Metin ekseni *"yok"* dedi, kural
`failed`'a çevrildi — ve **düzeltme kuralı bozdu**: `auth_outcome.yaml` ingest
sırasında `failed → failure` çeviriyor, yani kolonda duran değer zaten
`failure`'dı.

Genel kural: **bir eşleme tablosu cihazın sözcüğünü normalleştiriyorsa, kuralın
aradığı normalleştirilmiş değer ham satırda hiç geçmez ve `absent` görünür.**
Teşhis yordamı: [[skills/f3-eslesmeyen-kural-teshisi]].

Kalan 9 `absent` kuralın kaçının aynı sebeple orada olduğu **ölçülmedi** — ve
`catalog/mappings/` altında birden çok sözlük var. Bu yüzden kapsam oranı
**çivilenmiyor**: dalın `%40–%70` olarak kalması muhtemel ama payda
kesinleşmeden sayı yazılmamalı. ^[ambiguous]

## Aynı hatanın ikinci biçimi: ölçek

Payda kadar önemli ikinci soru: **maliyet neyle büyüyor.** T30'un ölçekleme
uyarısı, 269 kuralın maliyetini örneklemden çarparak bulmayı yasaklıyor:

> Eşleme maliyeti kural sayısıyla değil **ayrık alan sayısıyla** büyüyor. Yüz
> kural aynı on alanı kullanıyorsa maliyet on alanlıktır.

Doğru ölçekleme üç adım: örneklemden **alan başına** maliyet çıkar, hedef
kategorilerdeki **ayrık alan kümesini** say, çarpımı o küme üzerinden yap.

Kararı geçersiz kılacak bulgu da yazılı: alan başına maliyet kural başına
maliyetle **birlikte** büyüyorsa varsayım yanlıştır ve tablo düzeltilmez,
**yeniden yazılır**.

## Ölçüm aracının kendi bekçisi

Sıfırı sonuç sanmamak için ön kontrol yazıldı ve iki kez kendi dersini verdi:

- *"Boş değil"* ile *"doğru veri"* aynı şey değil. Düzeltilmiş hâli bir **yokluk
  kanıtı** değil **varlık kanıtı** arıyor: her vendor'ın altın örnek
  dosyasından türetilen sondalar `raw_data` içinde gerçekten duruyor mu.
- Sonda listesi boşsa kapı **reddediyor** — boş liste cevap değil arıza.

İkinci koşumun kusuru daha tehlikeli sınıftandı: ilki sahte veriyi **kabul**
ediyordu (yanlış pozitif); ikincisi doğru veriyi **reddediyor görünüp yine de
geçiyordu** — yanlış negatif *ve* atlanan kapı bir arada.

## Kaynaklar

- `docs/epic/t30-sigma-olcumu/index.md` — dört sayının protokolü, üç koşum, karar tablosu
- `docs/epic/t32-derleme-tasarimi/index.md` — "Kapsam %25 değil %43 — payda hatası"
- `docs/epic/t39-alan-kapsami/index.md` — `absent` düzeltmesi, payda 15
- `docs/epic/f3-yol-haritasi/index.md` — §2 açık kapsam kararı, §8 sayıların bağlayıcı olmaması
