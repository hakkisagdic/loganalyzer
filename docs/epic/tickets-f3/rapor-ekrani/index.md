---
title: "T37 — Rapor ekranı ve export"
kind: ticket
status: 2
---

# T37 — Rapor ekranı ve export

**Bağımlılık:** T36 · **Sonraki:** T38

## Amaç

Kanıt paketini insanın okuyabileceği hâle getirmek — ve dışarı çıkarabilmek.

## Kapsam

### İçinde

- Rapor ekranı: beş korelasyonun çıktısı, kanıt kaynakları, zaman penceresi.
- Her bulgudan **ilgili aramaya bağlantı** — "şu imza ilk kez göründü" satırı,
o imzayı arayan sorguyu doğru zaman aralığıyla açıyor.
- Kapsam dışı uyarısı ve zaman güvenilmezliği uyarısı görünür yerde.
- Export: PDF ya da Markdown. Olay sonrası paylaşılan şey rapor, ekran değil.
- **İnceleme düğmeleri:** "doğru muydu?" — T38'in altın kümesini besleyecek.

### Dışında

- Altın küme akışı ve alarm kapatmayla bağlama — T38.
- LLM yorumu bölümü — F4'te aynı ekrana eklenecek; yeri şimdiden ayrılmalı.

## Kabul kriterleri

- Rapor F2'nin tasarım temeliyle tutarlı; T28'in denetimine giren ekranlardan
biri.
- Boş/yükleniyor/hata/çok-veri durumları tanımlı — F2'nin dört durum kuralı
burada da geçerli.
- Export edilen rapor **kendi kendine yeten** bir belge: bağlantılar çalışmasa
bile içerik anlaşılıyor.
- Kapsam dışı ve zaman uyarıları export'ta da var — ekranı okuyan görüyor ama
PDF'i okuyan görmüyorsa uyarı işe yaramaz.

## Notlar

F4'ün LLM yorumu bu ekrana eklenecek. Yer şimdiden ayrılmalı ki o zaman ekran
yeniden tasarlanmasın — ve yorum geldiğinde **kanıtın yerine geçmemeli**, yanına
gelmeli. Kullanıcı her zaman ham kanıta bakabilmeli.
