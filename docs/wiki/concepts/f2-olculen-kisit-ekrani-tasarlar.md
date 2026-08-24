---
title: Ölçülen kısıt ekranı tasarlar
category: concepts
tags: [arayuz, veri-deposu, test, kavram, bizigo]
aliases: [kısa sorgu eşiği, keyset kısıtı, ölçüm ekrana iniyor]
relationships:
  - target: "[[concepts/f2-kapsam-tek-kapi]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[references/f2-kapanis]]"
    type: derived_from
  - target: "[[skills/f2-ekran-tutarliligi]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/log-arama-ekrani/index.md
  - docs/epic/tickets-f2/envanter-ekrani/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/envanter-ekrani/index.md=89e5d842af30 docs/epic/tickets-f2/log-arama-ekrani/index.md=e4425034ae89"
summary: F1'de ölçülen iki sayı (tam metin eşiği ~11 karakter, keyset'in owner_group+source_id kısıtı) arama ekranının tasarımını doğrudan belirledi. Ekran ölçümü gizlemiyor, kullanıcıya sayıyla söylüyor.
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

# Ölçülen kısıt ekranı tasarlar

F2 teknik planı iki F1 ölçümünü ekranın önüne koyup altını çiziyor: **bunlar
tercih değil kısıt.** Kapanış belgesi aynı iki ölçümün fazın sonunda hâlâ
geçerli olduğunu ve *"ikisi de ekranda karşılığını buldu"*ğunu yazıyor. Yani bu
sayfa bir performans notu değil, **ölçümden ekran davranışına giden zincirin**
kaydı.

## Ölçüm 1 — kısa sorgu tabloyu tarıyor

Tam metin indeksi ~10-11 karakterden sonra seçici. Altındaki her sorgu 1M
satırın tamamını okutuyor.

Eşik **alfabeden bağımsız**: `kullanıcı` (9 karakter) hiç atlamıyor,
`用户登录失败，请检查凭据` (12) %71 atlıyor. Yani bu bir Türkçe/CJK sorunu
değil, **uzunluk sorunu** (`docs/epic/tickets-f2/log-arama-ekrani/index.md`).

Ekrana inişi:

- Kutu ya minimum uzunluk dayatacak ya kullanıcıyı **açıkça** uyaracak;
  **sessizce kabul etmek yasak.** Bu yasak doğrudan
  [[concepts/sessiz-yanlis-davranis]] sınıfından türüyor: sessizce kabul edilen
  kısa sorgu, yazılan her kelimede tam tarama demek ve hiçbir belirti üretmiyor.
- Sevk edilen hâl: 11 karakterden kısa sorgu **koşulmuyor**, ekran sebebini
  ölçülen sayılarla söylüyor, ve *"Yine de ara (tam tarama)"* açık bir eylem
  olarak duruyor (`force=1`).
- Uzunluk **kod noktası** sayılıyor, UTF-16 birimi değil — aksi hâlde eşik yine
  alfabeye bağlı olurdu. ^[inferred]

## Ölçüm 2 — kaynak filtresi sayfalamayı sabitliyor

Keyset ancak sıralama anahtarının tam öneki verildiğinde sabit süreli:

| Sorgu şekli | Sayfa 1 | Derin sayfa |
| --- | --- | --- |
| Filtresiz | 377k satır | **1M satır** |
| `owner_group` | 155k | 286k |
| `owner_group` + `source_id` | 57k | **57k** |

Kapsam kapısı `owner_group`'u zaten ekliyor
([[concepts/f2-kapsam-tek-kapi]]); ekranın işi `source_id`'yi **teşvik etmek**.

Sevk edilen hâl bu teşviki iki kademeli yapıyor: kaynak seçilmediğinde ilk
sayfada yönlendirme, **sayfalamaya geçildiğinde daha sert bir uyarı** — çünkü
maliyet farkı derin sayfada doğuyor. Açılır liste kapsamdaki envanterden
(`GET /v1/sources`) besleniyor, yani teşvik ile kapsam aynı veriden geliyor.

## Üçüncü kademe: ölçüm başka ekranların kararını da bağlıyor

- **Alarm bağlantısının kaynak taşıması** bu ölçümden geliyor, kolaylıktan
  değil (`docs/epic/f2-kapanis/index.md` §1). Bildirimden gelen bağlantı
  `source_id` taşımazsa açtığı sorgu derin sayfada 1M satır okur.
- **Envanter listesinin ayrı bir uç olması** da maliyet kararı: liste kontrol
  düzlemine, etkinlik ClickHouse'a gidiyor ve T15'in kaynak filtresi listeyi
  **her açılışta** çağırıyor (`docs/epic/tickets-f2/envanter-ekrani/index.md`).

## Kalıp

Üç adım, üçü de belgede ölçülü hâliyle duruyor:

1. **Ölç** — gerçek veriyle, sayıyla.
2. **Kısıta çevir** — "tercih değil kısıt" cümlesi bunu yapıyor.
3. **Ekranda görünür kıl** — kullanıcıya sebebi sayıyla söyle, ve pahalı yolu
   *kapatma*, **açık onaya** bağla (`force=1`).

Üçüncü adım bu deponun tarzını özetliyor: kısıt kullanıcıdan gizlenmiyor,
kullanıcıya devrediliyor. ^[inferred]

## Açık kalan

T15 sevk edilirken kayıtlı aramalar `localStorage`'a yazıldı. Arama tümüyle
URL'den ibaret olduğu için paylaşılabiliyor, ama kayıtların kendisi **cihaza
bağlı** ve **T21'in alarm kuralları bunlara bağlanamıyor**: sunucuda duran bir
kural tarayıcıdaki bir girdiye referans veremez. T21'in kural modeli ise
"kaydedilmiş arama + tip + parametreler" diyor — iki belge burada ayrışıyor ve
kuralın bugün hangi arama tanımına dayandığı belgelerden **çıkarılamıyor**.
^[ambiguous] Bağlam: [[concepts/f2-sessizlik-alarmi]].

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — "Arayüzü bağlayan iki ölçüm"
- `docs/epic/tickets-f2/log-arama-ekrani/index.md` — T15 kabul kriterleri, "Sevk edilen", açık kalan madde
- `docs/epic/tickets-f2/envanter-ekrani/index.md` — T17 ayrı uç gerekçesi
- `docs/epic/f2-kapanis/index.md` — §1 "Arayüzü bağlayan iki F1 ölçümü hâlâ geçerli"
