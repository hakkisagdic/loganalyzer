---
title: Nerede kalındı — devir notlarının çizdiği durum
category: references
tags: [surec, log-analiz, kaynak-ozeti, bizigo]
aliases: [devir notu, kota kesintisi, faz durumu]
relationships:
  - target: "[[references/kapsam-feature-parity-matrisi]]"
    type: related_to
  - target: "[[concepts/kapsam-kestirme-besleme-yolu]]"
    type: related_to
  - target: "[[skills/kapsam-kapanacak-ile-kapanmayacagi-ayirmak]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: derived_from
sources:
  - docs/epic/devir-notu-kota-kesintisi/index.md
  - docs/epic/is-envanteri/index.md
  - docs/epic/fs-simulatorler/index.md
  - docs/epic/t36-devir-notu/index.md
source_digest: "sha256-12/v1 docs/epic/devir-notu-kota-kesintisi/index.md=769f322740d7 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048 docs/epic/t36-devir-notu/index.md=5027982dbb85"
summary: İki devir notunun (kota kesintisi ve T36→T37) ve envanterin birlikte çizdiği durum fotoğrafı — faz durumu, devreden borç, koordinatörde biriken canlı doğrulamalar ve gitmeyen iki mesaj.
provenance:
  extracted: 0.92
  inferred: 0.08
  ambiguous: 0.0
base_confidence: 0.8
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Nerede kalındı — devir notlarının çizdiği durum

> **Bu bir fotoğraf.** Sayılar 2026-08-21 günündendir; sonraki bir tur
> geçersiz kılar. Bir sayıya dayanacak olan önce kaynağındaki tarihe baksın.

Depoda iki ayrı **devir notu** türü var ve ikisi farklı şeyi devrediyor:

| Tür | Örnek | Neyi devrediyor |
| --- | --- | --- |
| **Oturum devri** | `devir-notu-kota-kesintisi` | Durum, yarım kalanlar, kime ne verilecek |
| **Ticket devri** | `t36-devir-notu` | Devralanın **koddan çıkarması gereken** ayrımlar |

İkisinin ortak yanı, ikisinin de en çok yeri "kaybolması pahalı olan
ayrımlara" ayırması — biri süreç tarafında, diğeri sözleşme tarafında.

## 1 · Faz durumu

| Faz | Durum |
| --- | --- |
| **F1** — Boru hattı | 12/12 kapalı · borcu duruyor |
| **F2** — Görünürlük | 16/16 kapalı |
| **F3** — Detection + RCA | **buradayız** · 5 kapalı, 5 açık, 1 başlamadı |
| **FS** — Simülatörler | F3'e **paralel**, bloke etmiyor · S01–S07 |
| F4 — Agentic | başlamadı |
| F5 — Kanıt genişletme | başlamadı |

F3'ün on bir ticket'ı: kapalı **T29** (`signature_hash`), **T34** (kanıt
sözleşmesi), **T35** (beş korelasyon — ama D7 açık), **T36** (kanıt paketi),
**T37** (rapor ekranı, canlı doğrulaması koordinatörde). Açık: **T30** Sigma
prototipi, **T31** ProcessingPipeline, **T32** derleme + üç kapı, **T33**
kural yönetimi, **T38** altın küme. Başlamamış ve sahipsiz: **T39**
(`specificity` ölçütü). Fazın içeriği ayrı sayfada:
[[references/f3-detection-ve-rca-kaniti]].

F1'den kalan ayrı bir ticket daha var: **T40 — ham arşiv kurtarma**
(`status: 0`). Bulduğu şey eksik bir özellik değil: 48 saatlik WAL penceresi
"nesne kaybolursa yerelden yeniden yükle" için seçilmiş ama **kurtarmayı yapan
kod yok**. Üstelik `DeleteExpiredSegmentsAsync` silme kararını yalnızca
`VerifiedAt`'e bakarak veriyor, yani mekanizma yazılsa bile **kendi kaynağını
silebilir** — korumanın mekanizmasız kalma hikâyesi:
[[skills/f1-veri-kaybeden-depoyla-tasarim]].

## 2 · Kesildiği an

Çalışma 2026-08-21'de kota bitince ortada kesildi. İyi haber notun ilk
bölümünde: `main = b904be4`, push edilmiş, **CI'nın sekiz işinin sekizi
yeşil** — "bundan önceki dört push kırmızıydı; bu ilk temiz koşum".

Kesilme gerçekten ortadaydı ve notun en somut katkısı bu iki satır:

| Kime | Ne | Durum |
| --- | --- | --- |
| Ajan **2** | Çok-parser'lı vendor kısıtı genel mi (MikroTik'te 12, Fortinet'te 3 alan çifti aynı satırda hiç birlikte gelmiyor) | rate limit, **hiç işlenmedi** |
| Ajan **7** | T38'in kalan yarısı: alarm kapatma **ekran** tarafı + `user_name` kalemi | rate limit, **hiç işlenmedi** |

`user_name` kalemi ayrıca bir sözleşme ayrışması: alan `FIELD_MAP`'te var
(Sigma tanıyor) ama korelasyon izin listesinde yok — kullanıcı kuralda
yazabiliyor, aynı alanı lift ile inceleyemiyor.

## 3 · Devreden borç

**Doğrulanmamışlar (D listesi):**

- **D3** — `SidecarLiveTests` atlanıyor, canlı sidecar gerekiyor.
- **D5** — Sigma kapsam oranının **paydası dört kez oynadı** (24 → 14 → 15 →
  kesişim düzeltmesinden sonra aşağı) ve hâlâ kesin değil
  ([[concepts/f3-oranin-paydasi]]).
- **D7** — T35'in beş korelasyon sağlayıcısının **kapsam negatif testi yok**;
  ticket `status:2` ama kapı sınanmamış. Bedeli tek cümlede: ilk-görülen imza
  başka grubun verisinden gelirse rapor yanlış olur ve kullanıcı bir sinyalin
  **yokluğunu bulgu sanar**.

**Kod eksik değil, doğrulama eksik (F1'den devreden):** T03 çift yazma
penceresi (ölçüldü, var; tekilleştirme anahtarı açık soru), T02 ölçen ama
yargılamayan hız bekçisi, T05 `matchTimeout=50 ms` gerekçesi kayıtta yok,
T12/D3 sidecar throughput iddiası **ölçülmemiş**, B14 ve B16 gerekçeli kabul.

## 4 · Koordinatörde biriken canlı doğrulamalar

Hepsi Docker gerektiriyor, hepsi faz sonu için — ve
[[skills/paralel-ajan-koordinasyonu]]'nun test bölünmesi gereği ajanlara
verilemiyor:

- **Göçler uygulanmadı** (`AddGoldenReviews`,
  `AddActualRootCauseToGoldenReview`).
- `POST /v1/rca` ve `GET /v1/rca/quality` **canlı yığına karşı hiç koşmadı**.
- `/rca` ekranı **gerçek tarayıcıda açılmadı**.
- Uçtan uca **ham arşiv kurtarma** gerçek RustFS'te koşmadı.
- Scrub örnekleme oranı ve saklama süresi **ölçülmedi**.
- Simülatör tarafından: `EventRetentionChainTests` yazıldı **koşturulmadı**
  (swap %89), API imajı yeni sayaçlarla yeniden kurulmadı — ayrıntı
  [[concepts/kapsam-kestirme-besleme-yolu]].

## 5 · Bu turda adı konan hata sınıfları

Kota notu §6 yedi sınıfı ortak imzalarıyla tabloya döküyor; en çok tekrar
edenler: *adı ile gövdesi ayrışan bekçi* (5 örnek), *koşum düzeninin
sessizliği* (5 — çıkış kodu okunmuyor, biri **yeşil üretiyordu**), *ölçüm
aracının kendi sessiz yanlışı* (4), *kural vendor'ın sözlüğünü değil bizim
varsayımımızı arıyor* (4).

**En pahalı önlenmiş hata:** EF'in ürettiği göç `enabled` → `status`
geçişinde her **pasif** kuralı sessizce açıyordu — kullanıcının kapattığı bir
alarm bir sabah kendiliğinden bildirim göndermeye başlardı.

## 6 · T36 → T37 devrinin çivilediği şey

Ticket devri notunun tamamı tek bir değişmezin etrafında: **dört durum**
(`Empty`, `NeverFed`, `Unavailable`/`Failed`, `NotRegistered`) yanıtta,
ekranda ve export'ta ayırt edilebilir kalmak zorunda — aynı kuralın faz
ölçeğindeki hâli [[concepts/f3-bosluk-tek-cins-degildir]]. Ayrıca:

- `RankedEvidence` bir **sıra**dır, hipotez değil; skor pakete yazılmıyor ve
  kullanıcıya gösterilmiyor. "Neden" cümlesini kuran taraf F4.
- `Drilldown` tipi `EventQuery`, **ham SQL değil** — paket saklandığı için SQL
  dizgisi taşımak kapsam kapısını (K17) atlayan bir yolu diske yazmak olurdu.
- Export'ta üç uyarı (kapsam dışı, zaman güvenilmezliği, eksik kanıt) **en
  üstte**: "raporun sonunda duran bir kısıt okunmuyor".

## 7 · Bu belgelerin bilmediği

- **F3'ün sayıları bağlayıcı değil** — baseline tabanı ve Sigma kapsam oranı
  gerçek müşteri verisi olmadan kesinleşmiyor.
- **Simülatörden çıkan hiçbir sayı bağlayıcı değil** — ölçek, vendor sürüm
  farkları ve ağ gerçekliği kapsam dışı.
- Ticket dosyalarındaki `status` alanları **bayat** (B15) ve envanter bu
  yüzden koddan çıkarıldı; en az üç ticket'ta dosya ile kod çelişiyor.
- `.githooks/post-checkout` ve `post-commit` çalışma ağacında takipsiz
  duruyor; kimin bıraktığı bilinmiyor.

## Kaynaklar

- `docs/epic/devir-notu-kota-kesintisi/index.md` — §1 CI, §2 faz durumu,
  §3 gitmeyen mesajlar, §4 borç, §6 hata sınıfları, §7 bilinmeyenler
- `docs/epic/is-envanteri/index.md` — §1 biten iş, §2 borç, §3 D listesi,
  §5 T40 ve çift yazma penceresi
- `docs/epic/fs-simulatorler/index.md` — §2 paralellik, §9 S-ticket'ları,
  §10.5 yapılmayanlar
- `docs/epic/t36-devir-notu/index.md` — §0 dört durum, §2–§4 sözleşme kararları
