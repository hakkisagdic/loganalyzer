---
title: Sayı kapsamın yerine geçmez
category: concepts
tags: [test, surec, kavram, bizigo]
aliases: [payda seçimi, test sayısı yanılgısı, kapsam oranı]
relationships:
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: related_to
  - target: "[[concepts/olcum-kirmizi-yanamayan-sayi]]"
    type: related_to
  - target: "[[skills/olcum-protokolu-sonuctan-once]]"
    type: extends
sources:
  - docs/epic/t27-kapanis-taramasi/index.md
  - docs/epic/t27-ad-govde-kesfi/index.md
  - docs/epic/t30-sigma-olcumu/index.md
  - docs/epic/t39-alan-kapsami/index.md
  - docs/epic/t02-kararlar/index.md
source_digest: "sha256-12/v1 docs/epic/t02-kararlar/index.md=b3cf84aad7ba docs/epic/t27-ad-govde-kesfi/index.md=e14d05d8d833 docs/epic/t27-kapanis-taramasi/index.md=fc23cd892d11 docs/epic/t30-sigma-olcumu/index.md=c3b32df8f602 docs/epic/t39-alan-kapsami/index.md=2c77e0540320"
summary: Test sayısı kaç kararın sınandığını söylemez; oranın paydası hangi soruya cevap verdiğini belirler. Bu depoda aynı ölçüm üç farklı paydayla üç farklı karara çıktı.
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

# Sayı kapsamın yerine geçmez

İki farklı belge, iki farklı ölçüm türü, aynı sonuç: **bir sayı, kapsamın yerine
geçtiği anda yanlış karar üretiyor.** Biri test sayısında, öbürü bir oranın
paydasında.

## Birinci hâl — koşan test sayısı

T27'nin kapanış taraması UI paketini saydı ve üç ayrı sayı buldu:

| Ölçü | Sayı |
| --- | --- |
| Varsayılan koşumda **koşan** test | **324** |
| Kaynakta **yazılı** `it(` / `test(` satırı | **270** |
| Fark | **54** — tablo/döngü genişlemesi |

En keskin örnek ekran görüntüsü paketi: **18 koşan testin tamamı tek bir
`it.each`'ten** geliyor (9 sahne × 2 tema) ve gövdesindeki tek iddia sayfanın
arka planının saydam olmaması — yani *"boyandı"*. Yerleşimi, hizayı, taşmayı
sınayan hiçbir şey yok.

Belgenin sonucu: *"299 UI testi" (ya da bugün 324) bir kapsam ölçüsü **değil**.
Kapsam ölçüsü, kaç ayrı kararın sınandığı — ve o sayı daha küçük.*

Aynı taramanın alarm tarafı aynı şekli veriyor: `AlertingTests`'in altı testinin
**ikisi** T27'nin kriteri, ikisi çapraz doğrulama, ikisi başka bir ticket'ın
kendi kriteri. *"Alarm tarafında altı entegrasyon testi var"* cümlesi doğru ama
T27 için iki şey söylüyor. (`docs/epic/t27-kapanis-taramasi/index.md` §3.5)

### Bu, elle liste kalıbının akrabası

Ölçülen bedel bir kez daha görülmüş: ekran görüntüsü harness'ının ilk koşumunda
hücreler ortalanmış, rozetler düz metin olmuş, kırpma çalışmamıştı — **ve test
geçti**, çünkü sayfanın boyandığını kontrol ediyordu. Yeşilliği hiçbir şey ifade
etmiyordu; [[concepts/elle-tutulan-liste-bekciyi-korlestirir]] ile aynı sonuç,
farklı mekanizma.

T02'nin `ScopeNegativeTests`'i bu ayrımın üçüncü örneği ve **iki belge farklı
sayı veriyor**: T02 on iki testin on iki okuma yolunu tek tek denediğini yazıyor,
T27 aynı paket için *"on iki test sekiz yolu kapatıyordu"* diyor. İkisinin hangi
koşulda ölçüldüğü karşılaştırılmadı. ^[ambiguous]
(`docs/epic/t02-kararlar/index.md` açıkta kalanlar #7,
`docs/epic/t27-kapanis-taramasi/index.md` §3.5.2)

## İkinci hâl — oranın paydası

T30'un Sigma kapsam ölçümü aynı verinin üstünden **üç farklı oran** üretti ve
üçü de doğru:

| Payda | Oran | Hangi soru |
| --- | --- | --- |
| 24 — örneklemin tamamı | %25 | belgenin ifadesiyle *hiçbir şey*; iki sebebi tek sayıda topluyor |
| 21 — bloke edilenler hariç | %29 | **eşleme kalitesi**: eşleyebildiklerimizin ne kadarı tutuyor |
| 14 — deseni altın örneklerde olanlar | **≈%43** | **kapsam**: eşlemenin gerçek kapsamı |

Ve fark kozmetik değil: karar tablosu üç dallı, `%25` `< %40` dalına düşüyor
(*tek vendor*), `%43` ise `%40–%70` dalına (*firewall + network_connection*).
**İki farklı kapsam kararı.**

Belge kendi hatasını da kaydediyor: *"Bu belge aynı tuzağa üçüncü kez düşmedi
ama ilk iki koşumda düştü: `%25` koordinatöre 'kapsam sayısı' diye verildi ve
öyle okundu."* (`docs/epic/t30-sigma-olcumu/index.md`)

### Paydadan ne düşülür, ne düşülmez — ayrı bir karar

T30 bunu gerekçesiyle yazıyor ve gerekçe *"ölçebildik mi"* ekseninde:

| Kutu | Paydadan düşülüyor mu | Neden |
| --- | --- | --- |
| `no_data` — vendor verisi yüklenmemiş | **evet** | fixture'ın eksikliği; ölçemedik |
| `absent` — aranan dizge örneklerde hiç yok | **evet** (sonradan) | örneklemin eksikliği; ölçemedik |
| `blocked` — o alan bu şemada hiç yok | **hayır** | ürünün sınırı; *eşleşmedi* demek doğru |

`no_data` için sayı da verilmiş: 24 kuralın 6'sının vendor'ı yüklü değilse payda
24 iken oran %38, doğru payda 18 iken **%50** — ve bu iki sayı karar tablosunda
**iki farklı dal**.

### T39 aynı paydayı bir daha oynattı

T39'un alan kapsamı ölçümü `absent` kutusunun bir **üst sınır** olduğunu
gösterdi: `fortigate_user_auth_fail` kuralı `status: failure` arıyor, ham
FortiGate satırı `status="failed"` yazıyor — ama `catalog/mappings/
auth_outcome.yaml` ingest sırasında zaten `failed → failure` çeviriyor. Kural
baştan doğruydu. Payda **14 → 15** oldu ve oran yeniden hesaplanmak zorunda
kaldı. (`docs/epic/t39-alan-kapsami/index.md`)

Belgenin kararı bu yüzden sayıyı **çivilememek**: *"Dalın %40–%70 olarak kalması
muhtemel ama payda kesinleşmeden sayı yazılmamalı."*

## Üçüncü hâl — belgenin kendi sayısı

T27'nin keşif belgesi bir kenar notu bırakıyor ve bu dilimdeki en dar kural o:

> Bir belgenin sayısı **kendi yayımladığı komutla üretilemiyorsa** o da bir
> sürüklenmedir.

Somut: aynı ölçüm Python betiğinde **56**, belgenin yayımladığı `grep` ile
**53** veriyor. İkisi de doğru — desenlerin katılığı farklı — ama ikisi de
yazılmış ve kullanılacak sayı olarak *yeniden üretilebilen* seçilmiş.
(`docs/epic/t27-ad-govde-kesfi/index.md`)

Aynı belge precision/recall dengesini de sayıyla veriyor: nicel iddia taşıyan ad
filtresi 885 testin 56'sına bakıyor, ikinci koşulla 14'e iniyor, ve bunların
içinde *gerçekten ilginç* olan **1**. %7 isabet, bir kapı için yetersiz.

T39 de aynı disiplini uyguluyor: iki bağımsız eksende ölçtüğü için
([[concepts/olcum-capraz-eksen]]) iki tablosundaki anahtar sayılarının **aynı
şeyi saymadığını** uyarı olarak basıyor — katalog sütunu yalnızca OCSF kolonuna
inmemiş anahtarları, ClickHouse sütunu `unmapped`'teki bütün anahtarları sayıyor.

## Uygulanabilir kural

1. Bir oran yayımlarken **paydayı da yayımla** ve hangi soruyu cevapladığını yaz.
2. Paydadan düşülen her kutunun gerekçesi *"ölçemedik"* olmalı; *"eşleşmedi"*
   olan kutu düşülmez.
3. Test sayısı bildirirken **kaç ayrı kararın** sınandığını da bildir; tablo
   genişlemesi ile ayrı iddia farklı şeyler.
4. Belgeye yazdığın sayıyı üreten komutu da yaz; üretilemiyorsa sayı sürükleniyor.

## Kaynaklar

- `docs/epic/t27-kapanis-taramasi/index.md` — §3.5.1, §3.5.2
- `docs/epic/t27-ad-govde-kesfi/index.md` — "Kullanılabilir yan ürün" ve sayı farkı notu
- `docs/epic/t30-sigma-olcumu/index.md` — protokol, "Payda düzeltildi", ön kontrol
- `docs/epic/t39-alan-kapsami/index.md` — "Sapmanın niceliği"
- `docs/epic/t02-kararlar/index.md` — açıkta kalanlar #7
