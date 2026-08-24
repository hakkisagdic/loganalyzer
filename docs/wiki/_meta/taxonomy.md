---
title: Tag Taxonomy
---

# Tag Taxonomy — bizigo-loganalyzer vault'u

`tag-taxonomy` skill'inin okuduğu kanonik etiket sözlüğü. Sayfa yazarken
**önce burası okunur**; listede olmayan bir etiket uydurulmaz.

## Kurallar

- Sayfa başına **en fazla 5** etiket.
- Küçük harf, tireli (`kebab-case`).
- Dar yerine geniş etiketi tercih et.
- `visibility/` grubu ayrıdır: 5 sınırına dahil değildir, alias eşlemesine tabi
  değildir, sayfa başına yalnızca bir tane.

## Domain (konu alanı)

| Etiket | Ne için |
| --- | --- |
| `log-analiz` | Log alma, ayrıştırma, normalizasyon, arama |
| `mimari` | Katman, sınır, bileşen kararları |
| `guvenlik` | Kimlik, kapsam, sır yönetimi, maskeleme |
| `veri-deposu` | ClickHouse, bölüm, replay, şema |
| `arayuz` | Next.js/BFF, ekranlar, OpenAPI sözleşmesi |
| `surec` | Çalışma protokolü, koordinasyon, faz yönetimi |
| `test` | Bekçiler, CI kapıları, ölçüm |
| `arac` | Depo dışı makine araçları (graphify, rtk, obsidian-wiki) |

## Type (sayfanın türü)

| Etiket | Ne için |
| --- | --- |
| `kavram` | Bir fikir ya da zihinsel model (`concepts/`) |
| `varlik` | Kişi, kurum, araç, servis (`entities/`) |
| `yordam` | Nasıl yapılır bilgisi (`skills/`) |
| `kaynak-ozeti` | Tek bir belgenin özeti (`references/`) |
| `sentez` | Birden çok kaynağı kesen analiz (`synthesis/`) |
| `proje` | Proje genel bakışı (`projects/`) |

## Project

| Etiket | Ne için |
| --- | --- |
| `bizigo` | bizigo-loganalyzer'a özgü bilgi |

## Alias eşlemeleri

| Alias | → Kanonik |
| --- | --- |
| `clickhouse` | `veri-deposu` |
| `keycloak` | `guvenlik` |
| `nextjs`, `next-js` | `arayuz` |
| `ci`, `bekci` | `test` |
| `process`, `protokol` | `surec` |

## Yeni etiket eklerken

2+ sayfada geçiyorsa buraya ekle; tek sayfadaysa en yakın kanonik etiketi kullan.
