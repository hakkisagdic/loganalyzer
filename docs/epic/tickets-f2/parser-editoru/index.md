---
title: "T19 — Parser editörü ve canlı test"
kind: ticket
status: 2
---

# T19 — Parser editörü ve canlı test

**Bağımlılık:** T14, T18 · **Sonraki:** T20

## Amaç

Parser yazmayı repoda dosya düzenlemekten çıkarmak. Kataloğun büyümesi bu ekrana
bağlı — F1 kasten dört vendor'da durdu.

## Kapsam

### İçinde

- YAML editörü: sözdizimi vurgulama, şema tamamlama, satır içi hata gösterimi.
- **Canlı test:** `POST /v1/parsers/try` ile örnek satır üzerinde anında sonuç —
çözülen alanlar, `core`/OCSF/OTel türetmeleri, `parse_status`, `issues`.
- Dispatcher modunda deneme: hangi **kademe** karar verdi. Envanter bağı yerine
literal filtreye düşen satır, parser doğru olsa bile envanterin eksik olduğunu
söylüyor — bu bilgi ekranda görünmeli.
- Gömülü `tests` bloğunu editörden yazıp koşturma.
- Ham arşivden gerçek bir satır çekip örnek olarak kullanma — uydurma örnekle
yazılan parser üretimde çuvallıyor.
- Taslak kaydetme ve inceleme isteme (T18'in akışı).

### Dışında

- Katalog listesi ve sürüm geçmişi — T20.
- Parser üretimi (LLM) — F4'ün format keşfi senaryosu.

## Kabul kriterleri

- Yeni bir vendor parser'ı **yalnızca ekrandan** yazılıp denenip yayına
gönderilebiliyor; repoya dosya koymak gerekmiyor.
- Deneme sonucu `timed_out` alanını gösteriyor: sıfırdan farklıysa sonuç "uymadı"
değil "ölçülemedi" demek ve ikisini karıştırmak sağlıklı bir parser'ı karantinaya
sokar.
- Şema hatası satır numarasıyla gösteriliyor.
- `try` ucu `author` rolü istiyor (F1'de öyle yazıldı) — okuyucu bu ekranı
göremiyor.

## Notlar

`POST /v1/parsers/try` F1'de yazıldı ve tam bu ekran için tasarlandı: hiçbir şey
yazmıyor, dispatcher kademesini döndürüyor.

T08'in ertelediği iki format eksiği burada acıtacak: `map` dallanamıyor
(`extends:` ile çözülecek) ve `expect` "alan yok" diyemiyor. Editör bunları
görünür kılacağı için F2'de çözülmeleri doğal.
