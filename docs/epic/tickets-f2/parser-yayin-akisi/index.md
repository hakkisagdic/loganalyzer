---
title: "T18 — Parser taslak deposu ve yayın akışı"
kind: ticket
status: 2
---

# T18 — Parser taslak deposu ve yayın akışı

**Bağımlılık:** — · **Sonraki:** T19
**Yöneten karar:** K33

## Amaç

Parser'ın üründe yazılıp yayınlanabilmesi. Editör (T19) bu olmadan yalnızca bir
deneme kutusu.

## Kapsam

### İçinde

- Kontrol düzleminde taslak tabloları: içerik, sahip, durum, sürüm, yorum.
- Durum makinesi: **taslak → incelemede → yayında**, ve **geri alındı**.
- Yayın öncesi **zorunlu kapılar**:
  - `parser lint` (şema + ReDoS) temiz — F1'in `GROK003 = 0` değişmezi yeni
parser girdiği gün kırılmamalı.
  - Gömülü `tests` bloğu boş olamaz (F1'de şema düzeyinde zaten zorunlu) ve
hepsi geçmeli.
- **Atomik yayın:** yeni katalog tamamen kurulup derlenene kadar eskisi yerinde
kalıyor, sonra tek referans değişimi. Bozuk bir katkı çalışan sistemi bozamıyor —
`ParserCatalog` bunu zaten yapıyor, eksik olan taslaktan besleme.
- Sürümleme ve **tek adımda geri alma**.
- Rol ayrımı: taslağı herkes yazar, yayını `author`/`admin` yapar.
- Katalog kaynağı artık ikili: repodaki dosyalar **ve** yayınlanmış taslaklar.
Çakışma kuralı açıkça tanımlanmalı.

### Dışında

- Editör arayüzü — T19.
- Yayın adımının bir PR üretmesi — bu akış üründe kalıyor (K33).

## Kabul kriterleri

- Lint'ten geçmeyen taslak yayınlanamıyor; kullanıcı hangi kuralın kırıldığını
görüyor.
- Yayın sırasında derleme hatası çıkarsa **çalışan katalog değişmiyor** ve ingest
kesintisiz sürüyor.
- Geri alma önceki sürümü aynı atomik yolla geri getiriyor.
- `author` olmayan kullanıcı yayın ucunu çağırdığında 403 alıyor.
- Aynı `parser_id` için repo dosyası ile yayınlanmış taslak birlikte varsa hangi
kazanır — testle sabitlenmiş.

## Notlar

F1'in `GROK003 = 0` sonucu zorlukla elde edildi (21 → 0, dört ayrı daraltma).
Yayın kapısı bu değişmezin bekçisi; olmadan katalog ilk katkıda geri izlemeye
düşer ve kimse fark etmez.

Katalog kaynağının ikiye çıkması bu ticket'ın en sinsi kısmı: "repoda ne var"
sorusu artık tek başına cevap değil.
