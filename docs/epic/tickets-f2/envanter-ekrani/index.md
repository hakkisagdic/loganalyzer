---
title: "T17 — Envanter ekranı"
kind: ticket
status: 2
---

# T17 — Envanter ekranı

**Bağımlılık:** T14 · **Sonraki:** T21

## Amaç

Kaynak envanteri: hangi cihaz hangi gruba ait, ne gönderiyor, ne zamandır
susuyor. T21'in sessizlik alarmı bu ekranın verisine dayanıyor.

## Kapsam

### İçinde

- Kaynak listesi (`GET /v1/sources`): `source_id`, `peer_address`, `owner_group`,
`source_class`, `encoding`.
- Kaynak ekleme/düzenleme ve **CSV yükleme** (`POST /v1/sources/csv`). F1'de CSV
yüklemesi **ya hep ya hiç** — yarım envanter doğrudan kapsam hatasına döner;
ekran bunu kullanıcıya böyle anlatmalı.
- Her kaynak için son görülme zamanı ve son 24 saatteki olay sayısı.
- `_unassigned` kaynakların ayrı ve **dikkat çeken** listesi: envanterde
olmayan bir cihaz veri gönderiyor demek. F1 bunu `attrs` içinde
`bizigo.unassigned_source_key` ile işaretliyor.
- `/v1/health/pipeline` göstergelerinin özeti.

### Dışında

- Cihaza bağlanma, config çekme — T26.

## Kabul kriterleri

- Envanter listesi **kapsamlı**: bir ekip başka ekibin cihaz listesini görmüyor.
F1'de bu bir tasarım düzeltmesiydi — filtre önce uç katmanında elle
uygulanıyordu, sonra `IScopedQuery.SearchSourcesAsync`'e taşındı.
- CSV yüklemesinde tek satır bile kapsam dışıysa **hiçbiri** yazılmıyor ve
kullanıcı hangi satırın reddedildiğini görüyor.
- `_unassigned` kaynak varsa ekran bunu bir uyarı olarak gösteriyor, listenin
dibinde gizlemiyor.

## Notlar

Sessizlik alarmının (T21) ihtiyacı olan "son görülme" bilgisi burada
hesaplanıyor. İki ticket aynı sorguyu iki kez yazmamalı — ortak bir sorgu
yüzeyi çıkarılmalı.

## Sevk edilen

**Rota: `/kaynaklar`.** Ekran sunucu bileşeni; üç isteği paralel atıyor ve
**biri düşerse diğerleri çiziliyor** — envanter listesi gelmezse sağlık özeti,
etkinlik gelmezse liste yine görünüyor ve eksiklik açıkça yazılıyor.

**Son görülme sorgusu yeniden yazılmadı.** Yeni uç `GET /v1/sources/activity`
arkasında T21'in sessizlik alarmıyla ortak olan
`IScopedQuery.GetSourceActivityAsync` duruyor. Ayrı bir uç olmasının sebebi
maliyet: liste kontrol düzlemine, etkinlik ClickHouse'a gidiyor ve T15'in
kaynak filtresi listeyi her açılışta çağırıyor.

**Ekran kendi sessizlik eşiğini tanımlamıyor.** "Ne zamandır susuyor" bir olgu
olarak gösteriliyor (`last_ingested_at`'ten geçen süre), eşik değil. Eşik
T21'in kuralında, kural başına; ikincisini burada tanımlamak, envanterin
"sağlıklı" dediği bir kaynağın alarm üretmesi demek olurdu ve ayrıştıkları
ancak biri şikâyet ettiğinde fark edilirdi.

`last_event_at` değil `last_ingested_at` kullanılıyor: ilki cihazın kendi saati
ve saati şaşmış bir cihaz "gelecekte" görünebiliyor.

### Kabul kriterlerinin karşılığı

| Kriter | Nerede |
| --- | --- |
| Envanter kapsamlı | `SearchSourcesAsync` (K17); etkinlik de kapsamlı — `ScopeNegativeTests.Son_gorulme_baska_grubun_kaynagini_sizdirmiyor` |
| CSV ya hep ya hiç + satır satır ret | `SourceCsvImport.Parse`; 10 birim testi, konteyner gerekmiyor |
| `_unassigned` dikkat çekiyor | Ekranın **en üstünde** kırmızı blok + ayrı tablo |

### Kapsam kontrolü iki yazma ucuna eklendi

`POST /v1/sources` ve `POST /v1/sources/csv` kapsamı **hiç sormuyordu**. Bugün
ikisi de yalnızca `admin` rolüne açık ve admin sınırsız kapsamlı, yani kontrol
pratikte no-op. Yine de eklendi: rol tablosu bir gün grup yöneticisi tanırsa,
kontrol olmadan o kullanıcı başka bir ekibin cihazını kendi grubuna
taşıyabilirdi — ve bu, o ekibin verisini görmek demek.

CSV'de ayrıca **aynı kaynağın iki kez geçmesi** de reddediliyor: son satırın
sessizce kazanması, kullanıcının dosyaya bakarak hangi grubun geçerli olduğunu
anlayamaması demekti.
