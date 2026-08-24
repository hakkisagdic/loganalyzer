---
title: Yazmama kararı ve kayan sınır
category: concepts
tags: [mimari, log-analiz, kavram, bizigo]
aliases: [kod bütçesi, yazmama kararları, ürünün sınırı]
relationships:
  - target: "[[references/kapsam-feature-parity-matrisi]]"
    type: related_to
  - target: "[[references/kapsam-nerede-kalindi]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: derived_from
sources:
  - docs/epic/pazar-arastirmasi-ve-feature-parity/index.md
  - docs/epic/is-envanteri/index.md
  - docs/epic/fs-simulatorler/index.md
source_digest: "sha256-12/v1 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048 docs/epic/pazar-arastirmasi-ve-feature-parity/index.md=5ce5d91ed46b"
summary: Pazar araştırması bir kod bütçesi çizdi — collector, depo, şema, kural seti ve arayüz yazılmayacaktı. İki kalem tuttu, iki kalem tutmadı; sınırın nerede kaydığı ve neden kaydığı ölçülebilir durumda.
provenance:
  extracted: 0.7
  inferred: 0.3
  ambiguous: 0.0
base_confidence: 0.7
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Yazmama kararı ve kayan sınır

2026-08-14 tarihli pazar araştırması ürünün kapsamını **negatif tanımla**
çizdi: piyasada dört katman var (collector, pipeline, store, analiz/UX) ve
"hiçbir ciddi ürün bu dört katmanı da sıfırdan yazmıyor". Buradan bir **kod
bütçesi** çıktı — `docs/epic/pazar-arastirmasi-ve-feature-parity/index.md` §4:

| Yazmayacağız | Yerine | Gerekçe |
| --- | --- | --- |
| Log toplama/taşıma | OTel Collector + Fluent Bit | 80+ plugin, backpressure çözülmüş |
| Depolama & sorgu motoru | ClickHouse | Elastic'e göre %60–70 ucuz |
| Detection kuralları | SigmaHQ + pySigma/rsigma | Binlerce hazır kural bedava |
| Ortak şema | OTel semconv + OCSF | Şema icat etmenin bedeli yüksek |
| Grok pattern kütüphanesi | Logstash/Elastic setleri | Yüzlerce hazır pattern |
| Agent orkestrasyonu | Microsoft Agent Framework + MCP SDK | GA, LTS, .NET yerel |
| Dashboard (en azından v1) | Grafana / HyperDX | "UI yazmak projeyi 3x büyütür" |

Aynı belge bunu bir açık karara da bağlamıştı (§5, madde 1): *"log analyzer mı
(mevcut store üstünde analiz katmanı), yoksa uçtan uca platform mu?"*

## Sınır nerede tuttu

- **Collector yazılmadı.** `docs/epic/fs-simulatorler/index.md` §1 collector'ı
  "yapılandırılmış" diye anıyor (TCP 5140 / UDP 5141, `iso-8859-1` kodlaması) —
  yani ürünün kendi kodu değil, ayarı.
- **Depo yazılmadı.** ClickHouse hem envanterde hem simülatör belgesinde
  ürünün dışında bir bileşen olarak duruyor.
- **Şema icat edilmedi** ([[concepts/f1-sema-karari-bir-kez-verilir]])**.** Araştırmanın "şema savaşı bitti" bulgusu (ECS →
  OTel semconv; güvenlik tarafında OCSF) ürüne T07 normalizasyon ve T16'nın
  **OCSF/OTel sekmeleri** olarak geçmiş (`docs/epic/is-envanteri/index.md` §1).
- **Drain3 tahmini tuttu.** Araştırma "Python/Go var, **.NET portu yok** → ya
  port edilecek ya sidecar" demişti; depoda bir **sidecar** var — kendi pytest
  paketi, `SidecarLiveTests`, ve simülatör senaryolarından biri doğrudan
  `sidecar-yok`.

## Sınır nerede kaydı

**Arayüz.** Araştırma v1 için Grafana/HyperDX öneriyordu; envanterde dokuz ayrı
arayüz ticket'ı duruyor — T13 Next.js iskeleti ve BFF, T15 arama, T16 olay
detayı, T19 parser editörü, T20 katalog, T23 alarm yönetimi, T25 connector,
T26 config diff, T28 UI/UX denetimi. Kendi arayüzü yazıldı ve yanında bir
sözleşme disiplini (OpenAPI tip üretimi, `api:check` sürüklenme kapısı) doğdu.

**Dayanıklılık katmanı.** Yazmama tablosu backpressure'ı "çözülmüş" sayıp
toplama katmanına devrediyordu. Bugün ürünün kendi WAL'ı, `fsync`'i, ham
arşivi ve dispatcher kademeleri var — simülatör belgesi B yüzeyini tam olarak
"F1'in tamamı: kodlama tespiti, WAL + fsync, ham arşiv + doğrulama, dispatcher
kademeleri, parser motoru, normalizasyon" diye tarif ediyor. Bu katman
araştırmanın OSS'e bıraktığı yerin içinde duruyordu. ^[inferred]

Kaymanın bedeli soyut değil: at-least-once teslim yazıldı ama **tekilleştirme
anahtarı yok** — `EventId` her çözümlemede yeniden üretildiği için aynı
gönderimin iki kaydı birbirine bağlanamıyor (`is-envanteri` §5). Satın alınan
bir bileşende bu soru cevaplanmış gelirdi.

## Açık kararın fiilen verilmiş cevabı

Araştırmanın 1. açık kararı belgede cevapsız duruyor, ama **kod cevaplamış**:
kendi ingest'i, kendi arayüzü, kendi detection hattı ve kendi RCA katmanı olan
bir **uçtan uca platform**. ^[inferred]

Aynı biçimde:

- **Birincil kullanım** (madde 2) güvenlik tarafına yaslanmış: Sigma, OCSF,
  alarm motoru, RCA. Cihaz filosu da bunu söylüyor — FortiGate, Cisco ASA,
  MikroTik, nginx (`fs-simulatorler` §6).
- **"Çok dilli" tanımı** (madde 7) (a) şıkkına oturmuş, yani **mesaj
  gövdesinin dili**: `iso-8859-1` kodlaması, `bozuk-kodlama` senaryosu
  ("Latin-1 baytlar UTF-8 iddiasıyla geliyor"), `bizigo.wire_encoding`. ^[inferred]
- **Multi-tenancy/RBAC** araştırmada `v2` idi; kapsam (`owner_group`,
  `idp_group_mapping`, K17) F1'den beri ürünün omurgasında —
  [[concepts/f1-kapsam-kaynaktan-gelir]].

## Neden bu sayfa kapsam sayfası

Bir "yazmayacağız" listesi, tutulup tutulmadığı **ölçülmediği sürece** bir
niyet beyanıdır. Burada iki kalemin tuttuğu ve iki kalemin kaydığı belgelerden
gösterilebiliyor; kayan yerlerin ikisi de sonradan iş çıkardı (arayüz
sözleşmesi, tekilleştirme kimliği). Kapsam kararının kendisi de bu deponun
[[concepts/sessiz-yanlis-davranis]] alışkanlığına tabi: yazılmış bir gerekçe,
kodda karşılığı aranmadıkça doğru sayılmaz.

## Açık sorular

- Arayüzü kendi yazma kararı bir yerde **gerekçesiyle** kayıtlı mı, yoksa
  ticket akışı içinde kendiliğinden mi oldu? Bu dilimin belgelerinde kararın
  kendisi geçmiyor. ^[ambiguous]
- Agentic katman (F4) henüz başlamadı; Microsoft Agent Framework + MCP tercihi
  hâlâ geçerli mi, ölçülmedi.

## Kaynaklar

- `docs/epic/pazar-arastirmasi-ve-feature-parity/index.md` — §1 pazar haritası,
  §4 mimari iskelet ve yazmama kararları, §5 açık kararlar
- `docs/epic/is-envanteri/index.md` — §1 biten iş, §5 çift yazma penceresi
- `docs/epic/fs-simulatorler/index.md` — §1 ölçülen boşluk, §3 üç yüzey, §6 filo
