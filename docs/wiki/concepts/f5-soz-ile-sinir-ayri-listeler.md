---
title: Söz ile sınır aynı listede duramaz
category: concepts
tags: [mimari, kavram, surec, bizigo]
aliases: [kalıcı muafiyet, out_of_scope, kapsam kararı ekranı, F5 muafiyet]
relationships:
  - target: "[[concepts/f3-bosluk-tek-cins-degildir]]"
    type: extends
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: related_to
  - target: "[[concepts/olcum-kirmizi-yanamayan-sayi]]"
    type: related_to
sources:
  - docs/epic/f5-kapsam-karari/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=d1f9862fea36 docs/epic/f5-kapsam-karari/index.md=9c4afd982807"
summary: Ertelenmiş bir eksik ile kalıcı bir kapsam sınırı aynı değerde toplanırsa ekran, verilmiş bir karardan sonra da söz vermeye devam eder — ve yanlışlığı hiçbir yerde kırmızı yanmaz. F5 kapsam kararının ürettiği mekanizma.
provenance:
  extracted: 0.88
  inferred: 0.12
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-09-05
tier: core
created: 2026-09-05T16:30:00Z
updated: 2026-09-05T16:30:00Z
---

# Söz ile sınır aynı listede duramaz

[[concepts/f3-bosluk-tek-cins-degildir]]'in bir üst kattaki hâli. Orada ayrılan
şey **bir koşunun sonucunun cinsi**; burada ayrılan şey **bir eksikliğin
kaderi**.

## Olgu

F5 kapsam kararı öncesinde üç kanıt türünün — metrik, trace, topoloji —
sağlayıcısı yoktu ve üçü de tek bir değerle raporlanıyordu:
`not_registered`. Ekran o değeri şöyle çeviriyordu:

> **sağlayıcı yok** — *"Bu kanıt türü için sağlayıcı yok (F5). Bu türe hiç
> bakılmadı."*

Cümle **doğruydu**. Sorun cümlenin içindeki iki karakterdi: *"(F5)"*. O, bir
durum bildirimi değil bir **söz**: *bu gelecek*.

Karar verildiğinde söz yalanlandı. Topoloji karşılandı; metrik ve trace
**kalıcı olarak** kapsam dışına alındı. Ekran aynı metni yazmaya devam etseydi,
verilmiş bir karardan sonra hâlâ bekletiyor olacaktı — ve **yanlışlığı hiçbir
yerde kırmızı yanmazdı**: derleme temiz, testler yeşil, tek kaybeden raporu
okuyan kişi.

## Kural

> Bir gün kapanacak olan ile hiç kapanmayacak olan aynı listede duramaz. İkisi
> tek listedeyken *"liste boşaldı mı"* sorusunun cevabı asla evet olamaz.

Emsali `ProducesContractTests`'in `Pending` / `Exempt` ayrımı ve aynı depoda
daha önce ödenmiş: bekleyen sözleşmeler ticket atfıyla bir listede durur,
muafiyetler **ayrı** bir listede ve sayıları sabittir.

## Mekanizma — not değil, kod

Ayrımı bir yorumla ya da yalnızca ekran metniyle kurmak yetmiyor: tel üzerinde
aynı değer kalırsa iki olgu yine tek şeye iniyor.

| Katman | Karşılık |
| --- | --- |
| Küme | `EvidenceKinds.Exempt = { Metric, Trace }` |
| Çivi | `EvidenceKinds.ExpectedExemptCount = 2` — muafiyet eklemek **iki** bilinçli hareket |
| Durum | `EvidenceStatus.OutOfScope` (7), `NotRegistered`'dan **ayrı** |
| Tel | `out_of_scope` — eşleme `switch`'i bilinmeyen durumda **fırlatıyor**, yani unutulamıyor |
| Ekran | *"kapsam dışı — bu ürün bu kanıt türüne bakmıyor; verilmiş bir kapsam kararı, eksik bir parça değil"* |

`NotRegistered` **silinmedi**. Bugün hiçbir tür orada değil, ama enum'a yeni bir
tür eklenmesi mümkün ve o gün sessizce atlanmamalı — değerin varlığı o günün
sigortası.

## Neden enum değeri silinmiyor

Kapsam dışına alınan tür `EvidenceKind`'dan **çıkarılmadı**. Kanıt paketleri
saklanıyor (T36) ve bir enum değerini silmek, o değerle yazılmış geçmiş
paketleri okunamaz yapardı. Kapsam kararı **bugünden sonrası** için verilir;
geçmişi yeniden yazmaz.

## Sınırı olan bir "var" da yazılmalı

Aynı kararın diğer yarısı: topoloji *"karşılanıyor"* ama envanterdeki düz
öznitelikler üzerinden — **ilişki grafiği yok**, iki kademe yukarıdaki ortak ata
hesaplanmıyor. "Karşılandı" demek yeterli olsaydı, adı *topoloji* olan bir tür
okuyana bir grafik vaat ederdi.

Sınır bu yüzden üç yerde birden duruyor: türün belgesinde, sağlayıcının
`Detail` metninde — yani **her kanıt diliminde** — ve kapsam kararı
belgesinde. Yalnızca kod yorumunda kalsaydı raporu okuyan onu hiç görmezdi.
