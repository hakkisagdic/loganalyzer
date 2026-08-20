---
title: "T20 — Katalog yönetim ekranı"
kind: ticket
status: 1
---

# T20 — Katalog yönetim ekranı

**Bağımlılık:** T19 · **Sonraki:** T27

## Amaç

Kataloğun bütününü görmek ve yönetmek: ne yayında, kim değiştirdi, hangi
sürümden nereye gelindi.

## Kapsam

### İçinde

- Parser listesi: vendor, ürün, sürüm, durum, sahip, son değişiklik.
- **İnceleme kuyruğu:** yayın bekleyen taslaklar, fark görünümü (önceki sürüme
karşı), onay/geri gönderme.
- Sürüm geçmişi ve **tek adımda geri alma**.
- Kapsam ölçüsü: her parser'ın altın örneklerinde `ok/partial/failed` oranı.
F1'de bu 86/1/0 idi ve kataloğun sağlığının tek sayısal göstergesi.
- Katalog genelinde `GROK003` sayacı — sıfırdan farklıysa bir parser geri
izlemeye düşmüş demek.

### Dışında

- Parser yazma/düzenleme — T19.

## Kabul kriterleri

- İnceleme kuyruğundaki fark görünümü YAML'ı satır satır karşılaştırıyor.
- Geri alma sonrası ingest kesintisiz sürüyor ve katalog eski sürümü koşuyor.
- Kapsam oranı düşen bir parser listede görünür biçimde işaretleniyor.
- `GROK003 > 0` ekranda uyarı üretiyor — sessizce sayı olarak durmuyor.

## Notlar

F1'de GROK003'ü 21'den 0'a indirmek dört ayrı daraltma gerektirdi ve son ikisi
bağlama özeldi (`ASA_IP`, `NGINX_NUM`). Bu sayının görünür olması, kazanımın
sessizce kaybedilmemesi için.
