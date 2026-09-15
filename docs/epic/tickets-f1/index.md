---
title: "F1 — Kapanış sonrası ölçüm ticket'ları"
kind: story
status: 0
---

# F1 kapanış sonrası ticket'lar

F1 **kapandı**. Bu dizin onu geri almıyor; kapanmış fazın **ölçülmemiş
iddialarını** ölçen işleri taşıyor.

Neden ayrı bir dizin: F1'in kendi ticket'ları (`tickets/`) fazı **kurdu**.
Buradakiler faz kapandıktan sonra, kabul kriterlerinin taranmasıyla doğdu — yani
yeni yetenek eklemiyorlar, **var olan iddiaları ölçüyorlar**. İkisini aynı
tabloya koymak *"F1 yeniden açıldı"* diye okunurdu.

Envanter: [F1'in ölçülmemiş kalemleri](../f1-olculmemis-kalemler/index.md) — altı
kalem, her biri için bugün ne ölçülüyor / ne ölçülmüyor / ölçmek neyi gerektiriyor.
Bu tablo o kalemlerin **ticket'a dönmüş** olanlarını taşıyor; hepsi ticket
olmadı, çünkü bazılarının doğru kapanışı bir metin düzeltmesi ya da bir
değişmez, ayrı bir iş değil.

## Yol haritası

| # | Ticket | Özü | Bağımlılık |
| --- | --- | --- | --- |
| F1-D1 | [Dayanıklılık kriterinin ölçümü](dayaniklilik-olcumu/index.md) | `kill -9` **taklit** ediliyor (test kendi yorumunda yazıyor); *"RustFS durdurulur, ingest devam eder"* yarısını ölçen hiçbir şey yok. Ölçümün **protokolü** yazıldı, koşum koordinatörde | — |

## Ticket'a DÖNMEYEN kalemler ve sebepleri

Envanterin altı kaleminden beşi burada yok, ve her birinin sebebi ayrı:

| Kalem | Neden ticket değil |
| --- | --- |
| Kriter 1 — *"her olay"* | Doğru kapanış bir test değil bir **değişmez**: ham referansı olmayan bir olayın yazılamaması. Örneklemi büyütmek evrensel iddiayı kanıtlamıyor, yani ölçüm işi değil tasarım işi |
| Kriter 6 — çok dilli **arama** | Üç ayrı iddia (Türkçe `İ`/`ı`, Arapça RTL, CJK kelime sınırı) ve doğru kapanış *"kriteri daralt"* da olabilir. Karar verilmeden ticket açmak, kararı ticket'ın içine gömerdi |
| Kriter 3 — **hacim** yorumu | Kapasite ölçümü; B01–B05 ailesine ait, F1'e değil |
| Kriter 5 — arşiv bütünlüğü | M19'da **kapatıldı**: bekçi yazıldı (`F2FlowTests`), kriter metni düzeltildi. Kalan tek şey koşum |
| Kapsam kriteri | Düzeltildi: kriter ürün yüzeylerine çekildi, replay ve CLI'ın kapsam dışı olması gerekçeleriyle yazıldı |
