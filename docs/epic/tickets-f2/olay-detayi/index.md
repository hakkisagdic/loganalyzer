---
title: "T16 — Olay detayı ve ham görünüm"
kind: ticket
status: 2
---

# T16 — Olay detayı ve ham görünüm

**Bağımlılık:** T15 · **Sonraki:** T27

## Amaç

Tek bir olayın tamamı — ve **ham baytları**. Ham arşivin bütün varlık sebebi bu
ekranda görünür hâle geliyor.

## Kapsam

### İçinde

- Çözümlenmiş alanlar: `core`, OCSF ve OTel görünümleri ayrı sekmelerde.
- `attrs` haritası, `bizigo.tags` ve `bizigo.dispatch_tier` dahil.
- **`time_source` açıkça gösteriliyor** — `parsed` / `observed` / `received`.
`observed` veya `received` ise kullanıcı bunu görmeden zaman üzerine akıl
yürütmemeli.
- `parse_status` ve varsa `issues` listesi (hangi adım, hangi mesaj).
- **Ham baytlar:** `GET /v1/events/{id}/raw` → `raw_b64`. Hem hex hem çözümlenmiş
metin görünümü, `encoding_detected` ile birlikte. İndirme düğmesi baytları
olduğu gibi veriyor.
- Ham bulunamadığında API'nin verdiği ipucu gösteriliyor ("nesne henüz
yüklenmemiş olabilir") — sessiz boş ekran değil.

### Dışında

- Replay tetikleme — F2'de değil, API ucu zaten var.
- İlişkili olay/korelasyon görünümü — F3'ün kanıt paketi işi.

## Kabul kriterleri

- windows-1254 kodlu Türkçe bir satır ekranda **doğru** görünüyor ve indirilen
baytlar cihazın gönderdiğiyle birebir aynı.
- `time_source = observed` olan olayda arayüz bunu belirgin gösteriyor.
- Kapsam dışı bir olay kimliği doğrudan yazıldığında **404** görünüyor — "var ama
göremezsin" değil.
- Ham okuma yetkisi kapsam kapısından geçiyor: başka grubun olayının baytları
indirilemiyor.

## Notlar

F1'de uçtan uca doğrulandı: 103 bayt girdi, 103 bayt çıktı, sha256 birebir.
Ama o yol beş ayrı katmanda kırılmıştı ve hiçbiri kendini belli etmemişti — bu
ekran o zincirin görünür ucu, dolayısıyla bozulduğunda ilk fark edilecek yer.

## Sevk edilen

**Rota: `/olaylar/{id}`**, sekmeler `?sekme=core|ocsf|otel|ham`. Sekmeler
bağlantı — durum adres çubuğunda, istemci tarafı JavaScript yok, bir sekme
paylaşılabiliyor. Ham sekmesi ayrı bir istek yapıyor: ham gövde megabaytlarca
olabiliyor ve her detay açılışında indirmenin anlamı yok.

**OCSF/OTel alan adları türetilmiyor, okunuyor.** `GET /v1/events/{id}` artık
`{ event, ocsf[], otel[] }` dönüyor; `ocsf`/`otel` listeleri ClickHouse
görünümlerinden (`db/clickhouse/0003_ocsf_otel_views.sql`) `SELECT *` ile
geliyor. Eşlemenin API'de ya da TypeScript'te ikinci bir kopyası **yok** —
olsaydı görünüme bir alan eklendiği gün sessizce ayrışırdı. Yeni yüzey:
`IScopedQuery.GetEventViewAsync`, yani kapsam zorlaması yine tek kapıda.

**İndirme ayrı bir rota:** `/olaylar/{id}/ham`. Baytlar API'den base64 gelip
sunucuda çözülüyor ve `application/octet-stream` olarak iniyor; tarayıcıda
yeniden kodlama yok. Yol yine `GET /v1/events/{id}/raw`'dan geçtiği için kapsam
kapısı da aynı.

### `issues` için gereken normalizasyon değişikliği

Ticket "varsa `issues` listesi" istiyordu ama parser'ın şikâyetleri
**ClickHouse'a hiç ulaşmıyordu**: `ParseContext.Issues` ayrıştırma anında vardı
ve normalizasyonda düşüyordu. Yani sebepsiz bir `partial`, detay ekranında
cevaplanamayan bir soru bırakıyordu — F1'in "sessiz bozulma" sınıfından.

`EventNormalizer` artık sorunları `attrs['bizigo.parse_issues']` altına tek
anahtarda yazıyor (etiketlerdeki gerekçenin aynısı: `mapKeys` bloom filtresi
anahtar kümesi üzerinde). Yalnızca sorunlu satırlarda doğuyor. **Geçmiş satırlar**
**boş kalıyor**; ekran bunu "ayrıntı kaydı yok" diye açıkça söylüyor, sessiz boş
liste göstermiyor.
