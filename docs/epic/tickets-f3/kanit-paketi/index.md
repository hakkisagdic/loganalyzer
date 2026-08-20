---
title: "T36 — Kanıt paketi ve deterministik rapor"
kind: ticket
status: 0
---

# T36 — Kanıt paketi ve deterministik rapor

**Bağımlılık:** T35 · **Sonraki:** T37 · **Yöneten karar:** K22

## Amaç

Kanıtı **saklamak** ve LLM'siz bir rapora dönüştürmek. K22'nin ayrımı burada
somutlaşıyor: kanıt F3'te, akıl F4'te.

## Kapsam

### İçinde

- `evidence_bundle` deposu: hangi olay/pencere, hangi sağlayıcılar, hangi
korelasyonlar, ham bulgular.
- Paket **yeniden kullanılabilir**: aynı paket üzerinde farklı modeller
karşılaştırılabilsin (F4'ün ihtiyacı). Bu, "yerel model yeterli mi?" sorusunu
ölçülebilir hâle getiriyor.
- Deterministik rapor: beş korelasyonun çıktısı, insan okunur biçimde. **LLM**
**yok.**
- **Kapsam dışı dürüstlüğü:** rapor, kapsam dışında kaç ilişkili olay olduğunu
söylüyor, içeriğini değil:
  <user_quoted_section>"Kapsamınız dışında 342 ilişkili olay var — tam analiz için X grubununsahibiyle görüşün."</user_quoted_section>
- **Zaman dürüstlüğü:** penceresinde `time_source != parsed` olan olay varsa
rapor bunu söylüyor.
- "Sağlayıcı yok" ile "veri yok" ayrımı rapora yansıyor.

### Dışında

- Ekran ve export — T37.
- LLM yorumu — F4.

## Kabul kriterleri

- Model kapalıyken rapor **okunabiliyor ve işe yarıyor**. Bu, K22'nin tek
sınavı.
- Aynı girdiyle aynı paket üretiliyor — deterministik.
- Kapsam dışı sayım içerik sızdırmıyor; testle sabitlenmiş.
- Change beslemesi boşsa rapor "değişiklik verisi yok" diyor, "değişiklik yok"
demiyor. İkisi farklı ve karıştırmak yanlış güven üretir.

## Notlar

Paketin saklanması F4 için de kritik: model değiştiğinde eski paketler üzerinde
yeniden koşup karşılaştırma yapılabiliyor. Saklanmazsa her karşılaştırma
kanıtı yeniden toplamak zorunda kalır ve o sırada veri değişmiş olur.
