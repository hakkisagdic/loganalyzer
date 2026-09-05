---
title: bizigo-loganalyzer
category: project
tags: [log-analiz, mimari, proje, bizigo]
aliases: [bizigo, loganalyzer]
relationships:
  - target: "[[references/f2-kapanis]]"
    type: related_to
  - target: "[[skills/paralel-ajan-koordinasyonu]]"
    type: uses
sources: [README.md, CLAUDE.md, docs/epic/f2-kapanis/index.md]
source_digest: "sha256-12/v1 CLAUDE.md=dd011df5a833 README.md=beebdd5b9080 docs/epic/f2-kapanis/index.md=c701d88f78fd"
summary: Plugin tabanlı, çok formatlı ve çok dilli log analiz platformu. F1 (boru hattı) ve F2 (görünürlük) kapandı; F3 ölçüm ağırlıklı faz.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.88
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
---

# bizigo-loganalyzer

Plugin tabanlı, çok formatlı ve çok dilli log analiz platformu. Ağ/altyapı
cihazı logları birincil alan; agentic katmanla proaktif araştırma ve kök neden
analizi.

## Durum

- **F1 (boru hattı) kapandı** — T01 iskelet, T02 depolama/kapsam, T03 ingest
  boru hattı, T04 ham arşiv, T05 parser motoru, T06 dispatcher, T07
  normalizasyon, T08 vendor kataloğu, T09 kimlik, T10 API uçları, T11 replay,
  T12 sidecar.
- **F2 (görünürlük) kapandı** — T13 Next.js iskeleti ve BFF, T14 OpenAPI tip
  üretimi, T15 log arama, T16 olay detayı, T17 kaynak envanteri, T18–T19 parser
  yayın akışı ve editör, T20 katalog, T21–T23 alarm motoru/bildirim/yönetim,
  T24–T26 değişiklik beslemesi, T27 doğrulama, T28 UI/UX denetimi.

Kapanış belgesi ve F3'e devredilenler: [[references/f2-kapanis]].

## Nasıl çalışılıyor

Bir koordinatör ve paralel uygulayıcı ajanlar; ayrı worktree'ler; ağır
doğrulamalar koordinatörde. Kurallar ve gerekçeleri:
[[skills/paralel-ajan-koordinasyonu]].

Deponun en pahalı hata sınıfı ve onu arayan bakış:
[[concepts/sessiz-yanlis-davranis]] ve
[[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

## Araçlar

- [[references/graphify-depo-bilgi-grafigi]] — `graphify-out/` altındaki
  sorgulanabilir kod grafı; "bu sınıfa kim dokunuyor" sorusunu dosya taramadan
  cevaplıyor.
- Bu vault'un kendisi — `docs/wiki/`, `obsidian-wiki` ile yönetiliyor.
  Kurulum: [[README]].

## Ortam notları

- .NET 10 SDK `~/.dotnet` altında; `DOTNET_ROOT` gerekiyor.
- `Bizigo.Api`'yi elle koşturmak: CWD **depo kökü**, içerik kökü **bin dizini**,
  `ASPNETCORE_ENVIRONMENT=Development`.
- Keycloak realm'inde **yalnızca `bizigo-claims` client scope var**;
  `openid profile email` canlıda `invalid_scope` alıyor. Ölçüldü.
- İnceleme akışı PR üzerinden. `main` dışındaki bir dala push tek başına hiçbir
  kapıyı çalıştırmıyor.

## Açık sorular

- F3'ün kapsamı bu vault'a henüz ingest edilmedi; `docs/epic/f3-teknik-plan/`
  ve `f3-yol-haritasi/` okunmadı. ^[inferred]

## Kaynaklar

- `README.md` — durum, hızlı başlangıç, bekçiler, telemetri, graphify
- `CLAUDE.md` — çalışma protokolü
- `docs/epic/f2-kapanis/index.md` — F2 kapanışı
