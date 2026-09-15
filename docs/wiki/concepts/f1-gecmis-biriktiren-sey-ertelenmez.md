---
title: Geçmiş biriktiren şey ertelenmez
category: concepts
tags: [mimari, surec, kavram, bizigo]
aliases: [faz sıralaması, change_events, dilimleme mantığı]
relationships:
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: part_of
  - target: "[[concepts/f1-sema-karari-bir-kez-verilir]]"
    type: related_to
  - target: "[[concepts/f1-ham-sadakat-zinciri]]"
    type: related_to
  - target: "[[references/f2-kapanis]]"
    type: related_to
sources:
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/tickets/index.md
  - docs/epic/f1-kapanis/index.md
  - docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md
  - docs/epic/tickets/api-uclari/index.md
source_digest: "sha256-12/v1 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=8467cc3e3e94 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/tickets/api-uclari/index.md=4e2624c6db9a docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md=7a1070c1c1da docs/epic/tickets/index.md=34b24312bd27"
summary: Faz sıralamasının ölçütü "önce kolay olan" değil; geçmiş biriktirmek zorunda olan şey ilk faza girer, biriktirmeyen ertelenir. Ham arşiv ve change_events F1'de bu yüzden var.
provenance:
  extracted: 0.82
  inferred: 0.18
  ambiguous: 0.0
base_confidence: 0.79
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: supporting
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Geçmiş biriktiren şey ertelenmez

K12'de MVP'nin dört ayağı birden seçilince (ham arşiv+replay, Sigma, vendor
kataloğu, alert) kapsam tek parça taşınamayacak kadar büyüdü ve K13 fazlara böldü.
Sıralamanın ölçütü ilk bakışta bağımlılık gibi görünüyor, ama F1'in içeriğine
bakınca **ikinci ve daha güçlü bir ölçüt** çıkıyor:

> Ham arşiv ve değişiklik akışı baştan konulmazsa sonradan eklemesi çok pahalı —
> ikisi de **geçmiş biriktirmek** zorunda.
> — `docs/epic/mimari-kararlar/index.md` §4

Bu sayfa o ölçütün nerelere uygulandığını topluyor; kararın kendisi karar
belgesinde, uygulaması ticket'larda, sonucu kapanışta duruyor.

## Ölçütün üç uygulaması

| Ne | Hangi faza ait | Neden yine de F1'de |
| --- | --- | --- |
| **Ham arşiv + replay** | Kendi başına F1 kalemi | Replay'in kaynağı geçmiş; sonradan açılan arşiv geçmişi geri getirmiyor |
| **`change_events` tablosu** | RCA F3'te (K22) | *"F3'te geçmiş yoksa özellik boş doğar"* — tablo F1'de açılıyor, yalnızca tablo + `POST /v1/changes` |
| **`ORDER BY` kararı** | T02 | Geçmişi değil şemayı bağlıyor ama aynı sınıf: sonradan değiştirmek tabloyu yeniden yazmak demek ([[concepts/f1-sema-karari-bir-kez-verilir]]) |

`change_events` en saf örnek: F1'de **hiçbir tüketicisi yok**. Yazma ucu var,
besleme yok, okuyan özellik yok. Buna rağmen açılmasının tek sebebi, F3'ün
kalitesinin o güne kadar birikmiş satır sayısına bağlı olması
(`docs/epic/f1-teknik-plan/index.md` §6.3).

Aynı mantığın tersi de kayıtta: risk #10 tam olarak bu tablonun **boş kalması**.
F1'de tablo açmak yetmiyor; kurumun config/deploy araçlarından besleme F2'de
gerçekten bağlanmazsa F3'te sürekli *"değişiklik yok"* diyen bir sağlayıcı olur ve
**RCA kalitesi sessizce düşer** (`docs/epic/mimari-kararlar/index.md` §5).

## Ertelenen şeyin ölçütü de aynı

F1'de bilerek yapılmayanlar listesine bakınca ölçüt tersinden okunuyor: hiçbiri
geçmiş biriktirmiyor, hepsi sonradan aynı fiyata geliyor
(`docs/epic/f1-kapanis/index.md`).

- **Uçtan parser yayınlama** — gözden geçirme akışı olmadan katalog kırılganlaşır;
  T10 yerine okuma + `POST /v1/parsers/try` verdi
  (`docs/epic/tickets/api-uclari/index.md`).
- **Kataloğun geri kalanı** (PAN-OS, Juniper, F5, HAProxy) — motor dört vendor'la
  gerçek yükü gördü; kalanı F2'deki editörle ve F4'teki keşif senaryosuyla çok
  daha ucuza gelecek.
- **`bizigo replay` CLI komutu** — CLI'nin tüm DI grafiğini barındırması
  gerekiyordu, API ucu aynı yeteneği veriyor.

## Faz kaydırmak da bu ölçütle yapıldı

Planla sevk edilen arasındaki en büyük sapma **RCA kanıt sağlayıcılarının F3'ten
F5'e** taşınması. Gerekçe kapsam: K21'in *"kanıt kapsamı: hepsi"* cevabı metrik +
trace + topolojiyi işin içine soktu ve **F5 tek başına F1 büyüklüğünde bir iş**.

Kaydırmayı mümkün kılan şey, sözleşmenin fazdan bağımsız tasarlanmış olması:
**kanıt sağlayıcı sözleşmesi F3'te beş türü de tanıyor, motor hiçbirine özel kod
içermiyor.** Karar belgesinin ifadesiyle bu, kararı *"geciktirilebilir ama geri
alınamaz değil"* yapıyor (`docs/epic/mimari-kararlar/index.md` §5, risk #8 ve §7).

Yani ölçüt iki soruya iniyor:

1. Bu iş **geçmiş** biriktiriyor mu? → Ertelenemez.
2. Ertelenirse **sözleşme** mi kayıyor, yalnızca **uygulama** mı? → Sözleşme
   yerinde kalıyorsa erteleme ucuz.

## Ticket dilimlemesinde aynı ilke

`docs/epic/tickets/index.md` on iki ticket'ı "her ticket bittiğinde depo çalışır
kalsın" ölçütüyle dilimlemiş:

- **T01–T04** bittiğinde: parse yok ama *"ham veri kaybolmadan iniyor ve
  kaybolursa haber veriyor"* — dayanıklılık omurgası ayakta.
- **T05–T08** bittiğinde: gerçek vendor logu normalize olarak ClickHouse'ta.
- **T09–T12** bittiğinde: bitti tanımının tamamı.

Ve bir istisna açıkça gerekçelendirilmiş: **T08 (vendor kataloğu) kasten sona
yakın değil, ortada.** Motor tamamlanmadan gerçek vendor logu görülmezse formatın
eksikleri en pahalı anda ortaya çıkar. Bu karar karşılığını verdi — T08 motorda
on ayrı eksik açtı (bkz. [[skills/f1-deklaratif-parser-motoru]]).

## Aynı ölçütle erken not edilen bir şey

Karar belgesi F5 sonrasına **tanıtım materyalini** (slayt + ekran filmi, muhtemelen
Remotion) bir faz olarak yazmıyor — kapsamı konuşulmadı. Yine de not edilmesinin
sebebi aynı sınıf: tanıtım filmi **uçtan uca akışların** üstüne kurulacak, yani
F2'nin arayüz ticket'ları yazılırken *"bu ekran demo edilebilir mi"* sorusu
bedava bir kısıt olarak akılda tutulmalı (`docs/epic/mimari-kararlar/index.md`
§4). Geçmiş değil ama **geri alınması pahalı** olan şeyi erken yazmanın aynı
mantığı. ^[inferred]

## Kaynaklar

- `docs/epic/mimari-kararlar/index.md` — K12, K13, K21, K22, §4, §5, §7
- `docs/epic/f1-teknik-plan/index.md` — §6.3, §14 iş sırası
- `docs/epic/tickets/index.md` — dilimleme mantığı, T08'in yeri
- `docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md` — `change_events` şimdi açılıyor
- `docs/epic/tickets/api-uclari/index.md` — `POST /v1/changes`, yayın ucunun ertelenmesi
- `docs/epic/f1-kapanis/index.md` — bilerek yapılmayanlar, F3 → F5 kaydırması
