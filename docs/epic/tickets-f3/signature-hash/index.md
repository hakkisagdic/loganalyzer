---
title: "T29 — signature_hash ve sıcak yol maliyeti"
kind: ticket
status: 2
---

# T29 — `signature_hash` ve sıcak yol maliyeti

**Bağımlılık:** — · **Sonraki:** T34, T35 · **Yöneten karar:** K35

> Sevk edildi — `t29-signature-hash` dalı, birleştirme koordinatörde. Hash
> sözleşmesi (neyin üstünden alınıyor, sözlük sürümüyle ilişkisi), ölçüm
> protokolü ve doğrulama koşusunun sayıları:
> [T29 sıcak yol ölçümü](../../t29-sicak-yol-olcumu/index.md).

## Amaç

RCA'nın en değerli iki sinyalini sidecar'dan **kurtarmak** — ve bunun sıcak yola
maliyetini tahmin etmek yerine **ölçmek**.

## Neden gerekiyor

`template_id` bugün şöyle doluyor:

| Olay | Durum |
| --- | --- |
| Ayrıştırması başarısız | Doluyor — ama imzanın **ilk** görülüşünde boş |
| Ayrıştırması başarılı | Yalnızca **%1** (`SidecarOptions.SampleRate = 0.01`) |

Bu bir hata değil, K14'ün sonucu: sidecar sıcak yolda değil, `TemplateAnnotator`
önbellekte bulamayınca kuyruğa atıp boş dönüyor. Ama sonucu şu: **"yeni bir şey**
**oldu" diyen tam o satırda kimlik yok** — ve RCA belgesi ilk-görülen imzayı "tek
en güçlü sinyal" diye tanımlıyor.

## Kapsam

### İçinde

- `events` tablosuna **`signature_hash`** kolonu (ClickHouse göçü).
- `MaskCatalog.Signature` her olayda çağrılıyor; sonucun hash'i yazılıyor.
Örnekleme **yok**, önbellek **yok**, ilk görülüşte **boş kalmıyor**.
- `EventNormalizer` ve `EventWriter`/`EventReader` kolon listeleri.
- `ReplayDiff` karşılaştırmasına dahil — imza değişmesi replay'in raporlaması
gereken bir fark.
- **Maliyet ölçümü:** sıcak yolun `signature_hash` öncesi ve sonrası ns/olay
maliyeti. `tests/Bizigo.UnitTests/SidecarLiveTests.cs` iskelesi bunun için zaten
var (T12'de yazıldı, `BIZIGO_SIDECAR_LIVE=1` ile koşuyor).
- Uzunluk sınırını aşan satırın `signature_hash`'i boş kalıyor ve bu
**sayılıyor** (`MaskCatalog.SkippedTooLong` zaten var).

### Dışında

- Korelasyonların kendisi — T35.
- `template_id`'nin kaldırılması. **Kalıyor**: insan-okunur kümeleme ve F4'ün
grok taslağı ondan geliyor. İki alan farklı iş yapıyor.

## Kabul kriterleri

- Her olayda `signature_hash` dolu — ayrıştırma durumu ve örneklemeden bağımsız.
- Aynı gövde her zaman aynı hash'i üretiyor; maskelenen alanlar (IP, sayı, UUID)
farklı olsa bile hash aynı kalıyor. Testle sabitlenmiş.
- **Maliyet ölçüldü ve artifact'a yazıldı**: ns/olay, öncesi ve sonrası.
- 16 KB sınırını aşan satırda hash boş ve sayaç artıyor.
- Replay sonrası `signature_hash` değişmişse fark raporunda görünüyor.

## Notlar

Maskeleme F1'de ölçüldü: 12 maskenin 8'i doğrusal motorda, dördü geri izlemede
ama iç içe sınırsız niceleyici yok, ve 16 KB girdi sınırı var. Yani maliyet
bilinen ve sınırlı — **ama bugüne kadar sıcak yolun yalnızca bir kısmında**
**koşuyordu**, o yüzden ölçüm şart.

Ölçüm kötü çıkarsa (örneğin olay başına maliyeti iki katına çıkarıyorsa) K35
yeniden değerlendirilmeli; bu ticket o kararın verisini üretiyor.
