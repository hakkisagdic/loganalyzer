---
title: Sessiz yanlış davranış
category: concepts
tags: [surec, test, kavram, bizigo]
aliases: [sessiz hata, belirtisiz kusur]
relationships:
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: extends
  - target: "[[references/f2-kapanis]]"
    type: derived_from
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: related_to
sources: [CLAUDE.md, docs/epic/f2-kapanis/index.md]
summary: Bu depoda en pahalı hata sınıfı; hata, sayaç ve belirti üretmeden yanlış sonuç veren davranış. Ölçülmeyen şey çalışıyor sayılmaz.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
---

# Sessiz yanlış davranış

Bu depoda "hata" kelimesinin karşılığı istisna fırlatan kod değil. En pahalı
sınıf, **hata da sayaç da belirti de üretmeden yanlış sonuç veren** davranış.
`CLAUDE.md` §7'nin tamamı bu sınıfı tarif ediyor ve F1'in çıkardığı ders olarak
kaydedilmiş.

## Kayda geçmiş örnekler

Hepsi gerçek olay; `CLAUDE.md` §7'de tek tek sayılı duruyor:

- Yanıttaki imlecin adı istekten farklıydı → ekran aldığı imleci geri
  gönderemiyordu → **yarım imleç sessizce ilk sayfayı tekrarlıyordu**.
- CSV'de aynı kaynak iki kez geçince son satır sessizce kazanıyordu; kazanan şey
  `owner_group`, yani **kapsamın kendisi**.
- Fark görünümü NFC normalize etmiyordu; ingest zaten NFC'ye çeviriyor, yani
  ekran **boru hattının sildiği bir farkı** raporlayacaktı.
- ASA config'inde sır maskelenmiş metinden çıkıyor ama **bölüm adının içinde**
  kalmaya devam ediyordu.
- `REPLACE PARTITION` atomik diye replay'in canlı ingest'i bozmadığı
  varsayılmıştı; okuma ile değiştirme arasındaki pencerede yazılan satırlar
  sessizce siliniyor.

## Ortak şekil

F2 kapanışı (`docs/epic/f2-kapanis/index.md` §2) altı kusuru inceleyip hepsinin
aynı şekle sahip olduğunu ölçtü:

> Belgelenmiş bir iddianın kodda karşılığı yok, ve sonuç sessizce yanlış.

**Hiçbiri koşturarak bulunmadı.** Altısı da okurken ya da o yorumun doğru
söyleyip söylemediğini soran testi yazarken çıktı.

## Uygulanabilir kural

Yeni bir bekçi yazarken sorulacak soru *"bu davranış doğru mu"* değil,
**"bu yorumun söylediği şey gerçekten oluyor mu"**.

Ve doğrudan sonucu: **bir şey ölçülmediyse çalıştığı varsayılmaz.**

Aynı mantık ölçüm kültürüne de taşınmış — `CLAUDE.md` §6 bekçinin *kırmızı
yanabildiğini* ölçmeyi şart koşuyor, çünkü geçen bir test yalnızca geçtiğini
kanıtlar, kırılabildiğini değil. Yeşil bir bekçinin sessizce atlaması, bu
sayfanın anlattığı sınıfın bekçi katmanındaki karşılığı:
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

## Açık sorular

- Bu sınıfı *aramak* için tekrarlanabilir bir yordam var mı, yoksa her seferinde
  okuma dikkatine mi bağlı? F2 kapanışı altı örneğin de okuma sırasında
  bulunduğunu söylüyor, ama sistematik bir tarama yordamı tarif etmiyor. ^[inferred]

## Kaynaklar

- `CLAUDE.md` §7 — "Bu depoda 'hata' ne demek"
- [[references/f2-kapanis]] — §2, yanlış çıkan altı iddia
