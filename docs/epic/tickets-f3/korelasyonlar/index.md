---
title: "T35 — Beş deterministik korelasyon"
kind: ticket
status: 0
---

# T35 — Beş deterministik korelasyon

**Bağımlılık:** T29, T34 · **Sonraki:** T36

## Amaç

RCA'nın **LLM'siz** yarısı. Saf SQL, tekrarlanabilir, model kapalıyken de
çalışıyor.

## Beş sinyal

| Sinyal | Ne yapar | Dayanağı |
| --- | --- | --- |
| **İlk-görülen imza** | Baseline'da olmayan, pencerede beliren `signature_hash` | T29 |
| **Hacim sapması** | İmza başına baseline'a göre oran (Poisson/z-score) | T29 |
| **Sessizlik** | Baseline'da düzenli gönderip pencerede susan kaynaklar | F2'nin sessizlik alarmıyla **aynı sorgu yüzeyi** |
| **Ortak öznitelik (lift)** | Etkilenen olayların paylaştığı alan değeri | `core` + `attrs` |
| **Yayılma sırası** | Kaynak başına ilk bozulma anı, sıralı | `ts` + `time_source` |

## Kapsam

### İçinde

- Beş sorgunun tamamı, kapsam altında.
- Baseline penceresi ve olay penceresi parametrik; varsayılanlar ölçülerek
seçilecek, tahminle değil.
- **`time_source` dürüstlüğü:** yayılma sırası `ts`'e dayanıyor ve `observed`
zamanlı bir olayın gerçek zamanı dakikalarca önce olabilir. Sonuç, penceresinde
güvenilmez zamanlı olay varsa bunu **taşımalı**.
- Sessizlik sorgusu F2'de yazılan yüzeyi **kullanıyor**, üçüncü bir kopya
yazılmıyor.

### Dışında

- Kanıt paketi deposu ve rapor — T36.
- LLM yorumu — F4.

## Kabul kriterleri

- Beş sinyal de gerçek veriyle çalışıyor; her biri için altın örnek var.
- İlk-görülen imza, **başarıyla ayrıştırılmış** olaylarda da çalışıyor —
T29'dan önce bu imkânsızdı (%1 örnekleme).
- Hacim sapması gerçek sayılar üzerinde; örnekleme düzeltmesi **yok**, çünkü
örnekleme yok.
- Kapsam dışı veri hiçbir sinyalde görünmüyor; kapsam dışı sayım ayrı dönüyor.
- Penceresinde `time_source != parsed` olan olay varsa çıktı bunu bildiriyor.

## Notlar

Üç sinyal (`sessizlik`, `lift`, `yayılma`) `signature_hash`'ten bağımsız; ikisi
T29 olmadan çalışmıyordu. F3 planındaki bulgunun tamamı bu ticket'ı besliyor.

Baseline penceresinin uzunluğu bir tahmin olmamalı: çok kısa seçilirse her yeni
şey "ilk-görülen" olur, çok uzun seçilirse gerçek yenilik gürültüde kaybolur.
Gerçek veriyle ölçülüp gerekçesiyle yazılmalı.
