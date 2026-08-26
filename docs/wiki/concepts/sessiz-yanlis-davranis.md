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
source_digest: "sha256-12/v1 CLAUDE.md=dd011df5a833 docs/epic/f2-kapanis/index.md=c701d88f78fd"
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
- **Bu sayfanın kendisi de o sınıfa girebiliyor.** Bir vault sayfası kaynağından
  ayrıldığında hiçbir şey bozulmuyor — sayfa hâlâ okunuyor, hâlâ ikna edici,
  ve **ölçülmüş gibi okunuyor**. `CLAUDE.md` §11'in vault kuralı bu yüzden
  yazıldı: aynı kırmızı üç ayrı turda main'i kırdı ve her seferinde farklı bir
  ajanın belgesi yüzünden. Sorun dikkatte değil, kuralın yazılı olmamasındaydı.

## Bir tasarım belgesinde "zaten" — iki kez ölçüldü

Sınıfın **planlama katmanındaki** hâli, ve iki bağımsız örneği var:

| İddia | Gerçek |
| --- | --- |
| *"detection motoru `AlertRaised` yayınlar"* | `AlertRaised` adında bir olay, tip ya da yayın **kodda hiç yok**; ad yalnızca o belgede geçiyordu. Bağlanma noktası bir **tablo satırı** (`alert_triggers`) |
| *"`status` alanı **zaten** `queued / gathering / …` taşıyor"* | `rca_report` diye bir tablo, varlık ya da statü kümesi **kodda hiç yok**; tek geçtiği yer o belge |

İkisi de bir **arıza** değil — öngörülen bir şema, bugünkü şemanın diliyle
yazılmış. Ama sonucu planlama katmanında ölçülüyor: *"zaten var"* diye okunan
bir bileşen, faza **sıfır maliyetle** girer. `AlertRaised` bir olay veri yolunu
bedava gösteriyordu; `rca_report` bir tabloyu.

Ayırıcı tek kelime: **"zaten".** Doğrulanmadan okunduğunda bir maliyet iddiası
taşıyor, ve tasarım belgeleri o kelimeyi öngörü ile envanter arasında ayrım
yapmadan kullanıyor. ^[inferred]

Karşılığı da mekanik: bir tasarım belgesindeki *"zaten"*, o şeyin kodda
**arandığı** anlamına gelmeli. Aranmadıysa cümle *"gerekecek"* diye yazılır —
aynı bilgi, farklı maliyet iddiası.

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

## Başka bir kural buna yaslanıyor — ve bunu söylüyor

`CLAUDE.md` §2, ajanın hangi testi koşturabileceğini bir mekanizmaya bağlıyor:
Docker kapalıyken koşan ve **geçen** bir test, konteyner istemediğini
kanıtlamış sayılıyor.

O çıkarım tek başına ayakta durmuyor. Konteyner yokluğunu görüp `Skip` yerine
**erken `return`** ile çıkan bir test de *"geçti"* diye raporlanır — yani
beyansız atlama serbest olsaydı mekanizma sessizce yanlış bir güvence
üretirdi. §2 bu yüzden dayanağını **açıkça yazıyor**: çıkarım, bu sayfanın
tarif ettiği sınıfın yasak olmasına bağlı.

Genel hâli: **bir mekanizmanın dayandığı varsayım yazılmazsa, varsayım
değiştiğinde kimse mekanizmaya bakmıyor.** Sessiz yanlış davranışın kural
katmanındaki karşılığı bu. ^[inferred]

## Açık sorular

- Bu sınıfı *aramak* için tekrarlanabilir bir yordam var mı, yoksa her seferinde
  okuma dikkatine mi bağlı? F2 kapanışı altı örneğin de okuma sırasında
  bulunduğunu söylüyor, ama sistematik bir tarama yordamı tarif etmiyor. ^[inferred]

## Kaynaklar

- `CLAUDE.md` §7 — "Bu depoda 'hata' ne demek"
- [[references/f2-kapanis]] — §2, yanlış çıkan altı iddia
