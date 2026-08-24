---
title: Kapsam tek kapıdan geçer
category: concepts
tags: [guvenlik, mimari, kavram, bizigo]
aliases: [IScopedQuery, kapsam ayrışması, owner_group kapısı]
relationships:
  - target: "[[concepts/f2-bff-deseni]]"
    type: related_to
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[concepts/f2-sessizlik-alarmi]]"
    type: related_to
  - target: "[[concepts/f2-degisiklik-beslemesi]]"
    type: related_to
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/log-arama-ekrani/index.md
  - docs/epic/tickets-f2/olay-detayi/index.md
  - docs/epic/tickets-f2/envanter-ekrani/index.md
  - docs/epic/tickets-f2/alarm-motoru/index.md
  - docs/epic/tickets-f2/change-webhook/index.md
  - docs/epic/tickets-f2/change-connector/index.md
  - docs/epic/tickets-f2/f2-dogrulamasi/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/alarm-motoru/index.md=6e126dd480f4 docs/epic/tickets-f2/change-connector/index.md=7155ed5a4924 docs/epic/tickets-f2/change-webhook/index.md=5e50834cbe21 docs/epic/tickets-f2/envanter-ekrani/index.md=89e5d842af30 docs/epic/tickets-f2/f2-dogrulamasi/index.md=87ed12f0cd0e docs/epic/tickets-f2/log-arama-ekrani/index.md=e4425034ae89 docs/epic/tickets-f2/olay-detayi/index.md=ac57c40a224b"
summary: F2'nin yedi ekranı ve üç alt sistemi kapsamı kendi başına uygulamıyor; hepsi IScopedQuery'nin tek kapısından geçiyor. Tekrarlayan kusur şekli her zaman filtrenin ikinci bir kopyası.
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

# Kapsam tek kapıdan geçer

F2'de kapsam (`owner_group`) bir ekran özelliği değil **mimari kısıt**. Teknik
plan alarm motorunu anlatırken tek cümleyle çiviliyor: kural sahibinin
kapsamıyla koşuyor, çünkü *"`IScopedQuery` zaten tek kapı"*
(`docs/epic/f2-teknik-plan/index.md`).

Bu sayfanın konusu o kapının F2 boyunca **kaç ayrı yerde tekrar tekrar
seçildiği** ve her seferinde alternatifin neden reddedildiği.

## Aynı karar, sekiz yerde

| Nerede | Kapının hâli |
| --- | --- |
| Arama (T15) | İki gruptaki kullanıcı aynı sorguda farklı sonuç görüyor; başka grubun olayı **hiçbir filtre kombinasyonuyla** görünmüyor |
| Olay detayı (T16) | Yeni yüzey `IScopedQuery.GetEventViewAsync`; ham baytlar da `GET /v1/events/{id}/raw` üzerinden aynı kapıdan geçiyor |
| Envanter (T17) | Filtre önce uç katmanında **elle** uygulanıyordu, sonra `IScopedQuery.SearchSourcesAsync`'e taşındı (F1 düzeltmesi) |
| Alarm (T21) | Kural sahibinin kapsamıyla değerlendiriliyor; bir ekibin kuralı başka ekibin verisini sayamıyor |
| Değişiklik (T24) | `IScopedQuery.WriteChangeAsync` zaten zorluyor — **ekran onu atlamamalı** |
| Connector (T25) | Connector yalnızca sahibinin kapsamındaki kaynaklara bağlanabiliyor |
| Etkinlik (T17) | `ScopeNegativeTests.Son_gorulme_baska_grubun_kaynagini_sizdirmiyor` |
| Doğrulama (T27) | `analyst.core` ile `analyst.edge` her ekranda farklı veri görüyor; hiçbir URL ya da doğrudan kimlik girişi bunu delmiyor |

Sekiz satırın ortak dersi tek bir cümle: **kapsamı yeniden uygulamak yerine tek
kapıya taşımak.** Tekrarlayan kusur şekli de bunun tersi — filtrenin ikinci bir
kopyası. ^[inferred]

## 403 değil 404

Kapsam dışı bir olay kimliği doğrudan adres çubuğuna yazıldığında ekran **404**
gösteriyor, "var ama göremezsin" değil. Gerekçe T14 ve T16'da aynı: 403 *"böyle
bir olay var"* bilgisini sızdırırdı. Yani kapsam yalnızca veriyi değil
**verinin varlığını** da gizliyor.

## Bugün no-op olan kontrol yine de eklendi

T17'de `POST /v1/sources` ve `POST /v1/sources/csv` kapsamı **hiç sormuyordu**.
Bugün ikisi de yalnızca `admin` rolüne açık ve admin sınırsız kapsamlı — yani
kontrol pratikte hiçbir şey yapmıyor. Yine de eklendi:

> Rol tablosu bir gün grup yöneticisi tanırsa, kontrol olmadan o kullanıcı başka
> bir ekibin cihazını kendi grubuna taşıyabilirdi — ve bu, o ekibin verisini
> görmek demek.

Kırmanın bedava olduğu anda kapatılan bir delik. Aynı disiplin CSV'de de
geçerli: **aynı kaynağın iki kez geçmesi reddediliyor**, çünkü son satırın
sessizce kazanması demek `owner_group`'un — yani kapsamın kendisinin — sessizce
değişmesi demek. Bu örnek [[concepts/sessiz-yanlis-davranis]] sayfasında
kayıtlı; F2 onu bir ekran davranışına çevirdi: CSV **ya hep ya hiç**, ve
kullanıcı hangi satırın reddedildiğini görüyor.

## Kapsam ekranın önbelleğini de bağlıyor

T27 sonrasında bekçi API'de ve ekranda ayrı ayrı duruyor. Ekran ayağının
eklediği yeni kural: **sunucuda kapsamlı veri çizen sayfa önbelleklenemiyor**
(`docs/epic/f2-kapanis/index.md` §6 tablosu). Önbellek, tek kapıdan geçen bir
sorgunun sonucunu kapının dışına taşıyan sessiz bir yol açardı. ^[inferred]

## Kapsam bir performans kısıtı da

Kapsam kapısı `owner_group`'u zaten ekliyor, ve F1'in keyset ölçümünde sabit
süreli sayfalama tam olarak `owner_group` + `source_id` önekiyle geliyor. Yani
güvenlik kapısı ile sayfalama maliyeti aynı anahtarın üstünde duruyor —
ayrıntısı: [[concepts/f2-olculen-kisit-ekrani-tasarlar]].

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — "kural değerlendirmesi kapsam altında", doğrulama listesi
- `docs/epic/tickets-f2/log-arama-ekrani/index.md` — T15 kapsam ayrışması kriteri
- `docs/epic/tickets-f2/olay-detayi/index.md` — T16 404 kararı, `GetEventViewAsync`
- `docs/epic/tickets-f2/envanter-ekrani/index.md` — T17 `SearchSourcesAsync`, iki yazma ucu, CSV
- `docs/epic/tickets-f2/alarm-motoru/index.md` — T21 kapsam altında koşum
- `docs/epic/tickets-f2/change-webhook/index.md` — T24 `WriteChangeAsync`
- `docs/epic/tickets-f2/change-connector/index.md` — T25 connector kapsamı
- `docs/epic/tickets-f2/f2-dogrulamasi/index.md` — T27 kapsam ayrışması akışı
- `docs/epic/f2-kapanis/index.md` — §6, ekran ayağının eklediği önbellek kuralı
