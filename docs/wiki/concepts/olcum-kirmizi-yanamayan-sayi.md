---
title: Kırmızı yanamayan sayı
category: concepts
tags: [test, surec, kavram, bizigo]
aliases: [eşiksiz ölçüm, sayaç-eşik-kapı merdiveni]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: extends
  - target: "[[concepts/olcum-sayi-kapsam-degil]]"
    type: related_to
  - target: "[[skills/olcum-bekciyi-kirmizi-yakmak]]"
    type: related_to
  - target: "[[concepts/olcum-gerekcesiz-sabit]]"
    type: related_to
sources:
  - docs/epic/t02-kararlar/index.md
  - docs/epic/t05-kararlar/index.md
  - docs/epic/t06-kararlar/index.md
  - docs/epic/t07-kararlar/index.md
  - docs/epic/t11-kararlar/index.md
  - docs/epic/t12-kararlar/index.md
  - docs/epic/t08-motor-geri-beslemesi/index.md
  - docs/epic/t27-ad-govde-kesfi/index.md
  - docs/epic/t29-sicak-yol-olcumu/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=d1f9862fea36 docs/epic/t02-kararlar/index.md=b3cf84aad7ba docs/epic/t05-kararlar/index.md=fa28e6db349e docs/epic/t06-kararlar/index.md=3a34fdcba4c4 docs/epic/t07-kararlar/index.md=f945b43c4277 docs/epic/t08-motor-geri-beslemesi/index.md=5ce87e36f831 docs/epic/t11-kararlar/index.md=65a7f0937a84 docs/epic/t12-kararlar/index.md=454710cef295 docs/epic/t27-ad-govde-kesfi/index.md=e14d05d8d833 docs/epic/t29-sicak-yol-olcumu/index.md=f5c754a8042f"
summary: Ölçülen ama hiçbir eşikle karşılaştırılmayan sayı, ölçülmemiş sayıdan yalnızca biraz iyi. Sayaç, eşik ve kapı üç ayrı karardır ve her biri ayrı ayrı verilmek zorunda.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.8
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:31:44Z
updated: 2026-08-24T17:31:44Z
---

# Kırmızı yanamayan sayı

Cümle T02'nin açık kalem listesinden geliyor ve bu dilimdeki belgelerin
yarısında karşılığı var:

> Ölçülen ama **kırmızı yanamayan** bir sayı, ölçülmemiş sayıdan yalnızca biraz
> iyi.

[[concepts/sessiz-yanlis-davranis]]'ın ölçüm katmanındaki hâli. Orada bir
davranış belirti üretmeden yanlıştı; burada bir **sayı** üretiliyor, kaydediliyor,
kimse ona bakmıyor ve sonuç aynı: değişim sessizce geçiyor.

## Merdivenin dört basamağı

Belgeler bu basamakları tek tek geçmiş ve **her basamak ayrı bir karar** olarak
alınmış:

| Basamak | Ne var | Ne eksik | Nerede geçti |
| --- | --- | --- | --- |
| 0 · sayı yok | — | her şey | — |
| 1 · sayı var, kimse okumuyor | `TestOutputHelper`'a yazılan hız | karşılaştırma | T02 #4, T07 §4.3 |
| 2 · sayaç var | `DroppedQueueFull`, `BoundMisses` | eşik | T12 §2, T06 §2 |
| 3 · eşik var | `BoundRatioTarget = 0.95` | — | T06 §2 |
| 4 · kapı var | `parser coverage` `failed > 0` ise CI kırılıyor | — | T08 raporu #10 |

### Basamak 1 — iki belge aynı kusuru yazmış

**T02, toplu yazım hızı.** Kabul kriteri 1M satır diyor, test 100k koşuyor ve
gerekçesini yorumuna yazıyor ("CI'da tam 1M pahalı"). Sayı çıktıya yazılıyor ama
**hiçbir eşikle karşılaştırılmıyor**; tek `Assert` satır sayısında. Belgenin
kendi sonucu: *hız iki katına yavaşlasa test yeşil kalır.*
(`docs/epic/t02-kararlar/index.md`, açıkta kalanlar #4)

**T07, türetme maliyeti.** Kabul kriteri iki şey istiyordu — ölçüm **ve** rapor.
`Turetme_maliyeti_olculuyor` ölçümü yapıyor, sayıyı test çıktısına yazıyor,
kalıcı bir kayda geçmiyor. Belgenin ifadesi: *"kabul kriterinin istediği rapor
edilmiş sayı kayıtta yok."* (`docs/epic/t07-kararlar/index.md` §4.3)

T11 aynı şeklin ürün tarafındaki örneğini taşıyor: replay raporundaki
`OkToFailed` sıfırdan büyükse yeni parser bir **gerileme** getirmiş demek — sayı
raporda duruyor ve *"onu okuyan bir ekran yok."*
(`docs/epic/t11-kararlar/index.md`, F3'e not)

Bu, `CLAUDE.md` §5'in "bir kapının kırmızı yanması ile o kırmızının okunması ayrı
olaylardır" maddesinin ölçüm tarafındaki karşılığı. ^[inferred]

### Basamak 2 → 3 — T06 merdiveni açıkça çıkmış

T06 sayaç ile eşiğin farklı şeyler olduğunu **yazılı olarak** ayırıyor.
`bound_ratio` bir sayı olarak duruyordu ve kabul kriteri "düşüyorsa uyarı
üretir" diyordu; belgenin cümlesi şu: *kimse bakmazsa sayı hiçbir şey
söylemiyor.*

Çözüm hedefi koda gömmek oldu (`BoundRatioTarget = 0.95`) ve yanıt üç alanı
birden taşıyor: oran, hedef ve `bound_ratio_healthy`. Karşılaştırmayı **sunucu**
yapıyor — belgeye göre bilinçli, çünkü ekran kendi eşiğini tutsaydı iki yerde
iki farklı "sağlıklı" tanımı olurdu. (`docs/epic/t06-kararlar/index.md` §2)

T12 bir alt basamağın gerekçesini veriyor: kuyruk `DropWrite` ile kurulmadı,
çünkü *"o sessizce düşürüyor ve düşen sayılamıyor; sayılamayan bir düşüş,
olmayan bir düşüş gibi görünür."* `DroppedQueueFull` ile `DroppedCircuitOpen`
ayrı ayrı sayılıyor — iki farklı arıza, tek sayaçta toplansalar hangisinin
olduğu bilinemezdi. (`docs/epic/t12-kararlar/index.md` §2)

## Her sayı kapıya çıkmak zorunda değil — ve bunun da gerekçesi yazılıyor

T29'un sıcak yol ölçümü bilerek **bekçi değil**: *"Test hiçbir eşik iddia
etmiyor: bekçi değil, ölçüm."* Ama eşiksiz kalmıyor — eşik ticket'ta, ölçümden
**önce** yazılmış: olay başına maliyeti iki katına çıkarıyorsa K35 yeniden
değerlendirilecek. Ölçülen `C/B = 1,46×` o eşiğin altında.
(`docs/epic/t29-sicak-yol-olcumu/index.md` §3)

Aynı ayrım T07'de de var: 10× eşiği *"bir performans hedefi değil, bir alarm."*

Ters yönün bedeli de ölçülmüş. T27'nin keşfi bir kapıyı **sevk etmemeye** karar
verdi: %7 isabetle çalışan bir kapı gürültü üretir, gürültü muafiyet doğurur,
muafiyet listesi büyür ve büyüyen muafiyet listesi bekçiyi kör eder — bu depoda
beş kez ödenmiş bedel, bkz. [[concepts/elle-tutulan-liste-bekciyi-korlestirir]].
(`docs/epic/t27-ad-govde-kesfi/index.md`)

## Bir de vakumda geçen iddia sınıfı var

T05'in `GrokPropertyTests`'i kendi iddiasının **boş geçmesini** engellemek için
üç ayrı sayaç taşıyor: `compiled > 100`, `rejected > 100`, `backtracking > 0`.
Gerekçe belgede yazılı — üreteç bir gün yalnızca doğrusal ifadeler üretmeye
başlarsa "doğrusal olmayan ifade sebebini söylüyor" iddiası her durumda doğru
olur ve **hiçbir şey sınamaz**. (`docs/epic/t05-kararlar/index.md` §2)

Bu, kırmızı yanamayan sayının kardeşi: kırmızı yanamayan **iddia**.

## Uygulanabilir kural

Bir sayı üretirken üç soruyu birlikte cevapla:

1. Bu sayı hangi eşiğe karşı okunacak? Eşik yoksa bu basamak 1'dir, ilan et.
2. Eşiği **kim** uyguluyor? İki taraf ayrı uygularsa iki farklı tanım doğar (T06).
3. Eşik bir kapı mı, bir alarm mı? İkisi farklı ve karıştırılırsa kapı susturulur.

Ve eşiğin kendisi yeni bir soru açıyor: eşik nereden geldi?
[[concepts/olcum-gerekcesiz-sabit]] bu dilimde en sık tekrarlanan boşluk.

## Açık sorular

- T29'un eşiği ticket'ta, T06'nınki kodda, T07'ninki test yorumunda duruyor.
  Eşiğin **nerede** durması gerektiğine dair yazılı bir kural bu belgelerde yok.
  ^[ambiguous]

## Kaynaklar

- `docs/epic/t02-kararlar/index.md` — açıkta kalanlar #4 ve altındaki cümle
- `docs/epic/t06-kararlar/index.md` — §2, "Sayaç yetmedi, eşik de kondu"
- `docs/epic/t07-kararlar/index.md` — §4.3
- `docs/epic/t11-kararlar/index.md` — "F3'e not"
- `docs/epic/t12-kararlar/index.md` — §2
- `docs/epic/t29-sicak-yol-olcumu/index.md` — §3, "Karar için okunuşu"
- `CLAUDE.md` §5, §6
