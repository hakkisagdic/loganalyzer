---
title: "T02 — Depolama şemaları ve kapsam kapısı"
kind: ticket
status: 2
---

<user_quoted_section>Durum: kod tamam, derleme 0 uyarı/0 hata, birim testleri 27/27.Entegrasyon testleri yazıldı ama Docker engeli nedeniyle koşturulamadı(görev #12). Docker açılınca StorageSchemaTests (10 test) koşacak.
Uygulama sırasında verilen kararlar:
attrs → Map(LowCardinality(String), String) seçildi (JSON değil).Gerekçe: F1'de ayrıştırılan alanlar zaten string; tipli sıcak alanlar corekolonlarında. mapKeys üzerinde bloom filter indeksi var. JSON kararı gerçekkardinalite verisiyle yeniden değerlendirilecek.Tam metin indeksi: text(tokenizer = sparseGrams(3, 20, 5)). preprocessoryok — lowerUTF8() Türkçe İ/ı'da bayt uzunluğu değiştiği için hatalıolabiliyor ve skip index'te bu yanlış negatif demek. Arama şimdilikbüyük/küçük harf duyarlı; duyarsız arama kararı ölçümle verilecek.ClickHouseBulkCopy 1.3.0'da kullanımdan kalkmış → ClickHouseClient.InsertBinaryAsync.Ayrıca tek uzun ömürlü istemci (ClickHouseContext): bağlantı başınaHttpClient soket tüketirdi.Kontrol düzleminde snake_case (EFCore.NamingConventions) — elle yazılanSQL ve psql oturumları okunabilir kalsın.</user_quoted_section>

# T02 — Depolama şemaları ve kapsam kapısı

**Bağımlılık:** T01 · **Sonraki:** T03, T06, T09
**Yöneten belgeler:** [F1 §6, §8, §10.2](../../f1-teknik-plan/index.md) ·
[K17, K23](../../mimari-kararlar/index.md)

## Amaç

İki depolama düzlemi ve **kapsam ayrımının tek kapısı**. Bu ticket F1'in en geri
alınamaz kararını taşıyor: `ORDER BY` bir kez seçilir, sonra tablo yeniden
yazılmadan değişmez.

## Kapsam

### İçinde

1. **ClickHouse `events` tablosu** — [F1 §6.1](../../f1-teknik-plan/index.md)'deki
DDL birebir. `ORDER BY (owner_group, source_id, ts)`,
`PARTITION BY toYYYYMMDD(ts)`, `idx_ts minmax`, `idx_attr_keys bloom_filter`,
`idx_body text(tokenizer = 'sparseGrams')`.
2. **ClickHouse `change_events` tablosu** — [F1 §6.3](../../f1-teknik-plan/index.md).
RCA F3'te ama tablo şimdi açılıyor: geçmiş birikmezse özellik boş doğar.
3. **Bulk writer** — `ClickHouse.Driver` binary bulk insert. Toplu yazım eşiği
10k satır / 2 sn (hangisi önce). Yeniden deneme + kısmi başarısızlık davranışı
tanımlı.
4. **Postgres kontrol düzlemi** (EF Core): `sources`, `source_groups`,
`idp_group_mapping`, `parsers`, `raw_manifest`, `audit_log`.
5. **`IScopedQuery` — tek kapı.** Her sorgu yolu (REST, CLI, replay okuma, F3 kanıt
toplama, F4 MCP) buradan geçer; `owner_group IN (...)` filtresi burada enjekte
edilir. Kapsam listesi kullanıcının claim'lerinden `idp_group_mapping` üzerinden
çözülür.
6. **Mimari testler (NetArchTest)** — `Bizigo.Storage.ClickHouse` dışındaki hiçbir
tip `ClickHouse.Driver`'a referans veremez. Bu kural olmadan kapsam ayrımı ilk
aceleci PR'da delinir.

### Dışında

Veri yazımı (T03), sorgu uçları (T10), kimlik doğrulama (T09 — bu ticket'ta kapsam
listesi test için elle verilir).

## Kabul kriterleri

Migration runner iki tabloyu da kuruyor; tekrar çalıştırmak güvenli (idempotent)1M satır bulk insert ölçülüyor, süre CI çıktısında raporlanıyorsparseGrams indeksiyle Türkçe/Arapça/Çince gövdede alt dizi araması sonuç veriyorIScopedQuery dışında ClickHouse'a giden bir kod yolu derlemeyi kırıyorNegatif test: B grubunun verisi, A grubunun kapsamıyla hiçbir yoldan dönmüyor

## Notlar

- **`ORDER BY` gerekçesi** [F1 §6.2](../../f1-teknik-plan/index.md)'de üç adayla
tabloya bağlandı. Değiştirilecekse **bu ticket'ta** değiştirilir, sonra değil.
- `_unassigned` grubu: eşleşmeyen kaynak reddedilmez, bu gruba düşer ve sağlık
uyarısı üretir ([F1 §8](../../f1-teknik-plan/index.md)). Veri kaybı, eksik
envanterden kötüdür.
- `raw_manifest` tablosu burada açılıyor ama T04 dolduruyor.
