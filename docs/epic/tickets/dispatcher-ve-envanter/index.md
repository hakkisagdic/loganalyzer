---
title: "T06 — Dispatcher, envanter ve owner_group ataması"
kind: ticket
status: 2
---

# T06 — Dispatcher, envanter ve `owner_group` ataması

**Bağımlılık:** T02, T05 · **Sonraki:** T08, T11
**Yöneten belgeler:** [F1 §4.2, §8](../../f1-teknik-plan/index.md) · [K17](../../mimari-kararlar/index.md)

## Amaç

Gelen satırın hangi parser'a gideceğine karar vermek ve olayın **kapsamını**
belirlemek. Yüzlerce parser varken her satıra hepsini denemek olanaksız.

## Kapsam

### İçinde

1. **Dört kademeli dispatcher** ([F1 §4.2](../../f1-teknik-plan/index.md))
  1. **Envanter bağı** — `source_id → parser_id` tanımlıysa doğrudan o parser.
Hedef: üretim trafiğinin **>%95'i** buradan geçsin.
  2. **Literal ön filtre** — tüm parser'ların `match.contains` literalleri tek bir
**Aho-Corasick** otomatına derlenir; satır **bir kez** taranır.
  3. **Aday denemesi** — kalanlar `specificity` sırasıyla denenir, ilk `ok` kazanır.
  4. **Düşüş** — hiçbiri tutmazsa `parse_status=failed`; ham arşive + sidecar
keşif kuyruğuna.
2. **`bound_ratio` sağlık metriği** — kademe 1'den geçen oran. Düşüyorsa envanter
bakımsız kalmış demektir; bu bir uyarı üretir.
3. **Kaynak envanteri** — Postgres `sources`: kimlik (syslog peer IP / hostname /
cihaz etiketi), `vendor`, `product`, `encoding`, `parser_id` bağı, `owner_group`.
CSV + API ile yükleme.
4. **`owner_group` ataması** — grup olayın kendisinden değil **kaynağından** gelir.
Eşleşmeyen kaynak `_unassigned`'a düşer; **reddedilmez**. Yalnızca yöneticiye
görünür ve sağlık uyarısı üretir.
5. **Parser kataloğu yükleme** — YAML dosyalarının diskten/veritabanından yüklenmesi,
sıcak yeniden yükleme (hot reload), sürüm çakışması davranışı.

### Dışında

Vendor parser içerikleri (T08), NetBox entegrasyonu (F2), parser yayın onay akışı
(F2/F4).

## Kabul kriterleri

Envanterde bağlı kaynak doğrudan doğru parser'a gidiyor (regex denemesi yok)Hiçbiri tutmayan satır failed oluyor ve ham arşive düşüyor — kaybolmuyor_unassigned kaynak veri kaybettirmiyor, uyarı üretiyorbound_ratio sağlık ucunda görünüyorParser sıcak yeniden yükleme çalışıyor, koşan boru hattını bozmuyor

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| Literal ön filtre | `src/Bizigo.Parsing/Dispatch/AhoCorasick.cs` |
| Katalog + sıcak yükleme | `Dispatch/ParserCatalog.cs` — atomik anlık görüntü değişimi |
| Dört kademe | `Dispatch/Dispatcher.cs` |
| Sağlık sayaçları | `Dispatch/DispatchStats.cs` — `bound_ratio` dahil |
| Parse adımı | `src/Bizigo.Ingest/Pipeline/ParsingSink.cs` (geçişin yerine) |
| Katalog/envanter tazeleme | `src/Bizigo.Ingest/Pipeline/CatalogRefreshService.cs` |

**Üç tasarım kararı:**

1. **Anlık görüntü satırın başında alınıyor.** Sıcak yeniden yükleme tam o sırada
olursa satır tutarlı tek bir katalogla işlenir — yarı yüklü ara durum yok. Ayrıca
bütün dosyalar bozuksa katalog **değiştirilmiyor**: hatalı bir dağıtım, çalışan
boru hattını parser'sız bırakamaz.
2. **Literali olmayan parser her satırda aday.** Ön filtre onu eleyemez; listeye
konmazsa sessizce hiç denenmezdi. Bu, "ön filtre bir güvenlik ağı, zorunluluk
değil" ilkesinin kod karşılığı.
3. **Bağlı parser tutmazsa aday taramasına düşülüyor** ama ayrı sayaçla
(`bound_misses`). Cihaz yazılımı değişmiş olabilir; satır kaybedilmiyor ama
envanterin bakıma ihtiyacı olduğu görünür kalıyor.

**Kapsam dışı bırakıldı:** CSV/API ile envanter yükleme uçları T10'a taşındı —
`sources` tablosu ve çözümleyici hazır, eksik olan yalnızca HTTP yüzeyi.

**Doğrulama:** çözüm 0 uyarı; birim testleri **214/214** (21'i yeni: dispatcher
kademeleri, `bound_ratio`, sıcak yeniden yükleme, otomat).

## Notlar

- Dispatcher'ın sırası **performans için değil doğruluk için** bu şekilde: envanter
bağı hem en hızlı hem en doğru yol. Literal filtre yalnızca envanteri eksik olan
kaynaklar için bir güvenlik ağı.
- `specificity` hesabı basit tutulur: `match.contains` literal sayısı + uzunluğu.
Fazla akıllı bir sıralama bakımı zorlaştırır.
