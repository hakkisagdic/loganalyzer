---
title: F3 — Detection ve RCA kanıtı
category: references
tags: [log-analiz, mimari, kaynak-ozeti, bizigo]
aliases: [F3, F3 fazı, detection fazı]
relationships:
  - target: "[[concepts/f3-bosluk-tek-cins-degildir]]"
    type: contains
  - target: "[[concepts/f3-kanit-once-akil-sonra]]"
    type: contains
  - target: "[[skills/f3-sigma-derleme-kapilari]]"
    type: contains
  - target: "[[references/f2-kapanis]]"
    type: extends
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: derived_from
sources:
  - docs/epic/f3-teknik-plan/index.md
  - docs/epic/f3-yol-haritasi/index.md
  - docs/epic/tickets-f3/index.md
  - docs/epic/rca-raporu-ozelligi/index.md
  - docs/epic/sigma-clickhouse-arastirmasi/index.md
  - docs/epic/tickets-f4/prompt-redaksiyon-tabani/index.md
source_digest: "sha256-12/v1 docs/epic/f3-teknik-plan/index.md=af05d3fc9852 docs/epic/f3-yol-haritasi/index.md=473574f06a4f docs/epic/rca-raporu-ozelligi/index.md=5190a1f093f2 docs/epic/sigma-clickhouse-arastirmasi/index.md=8715e251b522 docs/epic/tickets-f3/index.md=e6589b0ed6b1 docs/epic/tickets-f4/prompt-redaksiyon-tabani/index.md=75e5f331bd85"
summary: F3'ün iki kolu (Sigma detection ve RCA kanıtı), planı yarı yarıya değiştiren template_id bulgusu, on ticket'ın bugünkü durumu ve fazın sayılarının neden bağlayıcı olmadığı.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-25T00:00:00Z
---

# F3 — Detection ve RCA kanıtı

F1 boru hattını kurdu, F2 ona bir yüz verdi ([[references/f2-kapanis]]). F3,
ürünün **kendi başına düşünmeye başladığı** faz — ama henüz LLM'siz.

Fazı yöneten ayrım K22: **kanıt F3'te, akıl F4'te**
(`docs/epic/f3-teknik-plan/index.md`). Kanıt paketi LLM olmadan tek başına
değerli ve model kapalıyken bile okunabiliyor; hipotez kuran taraf F4.

Bu sayfa fazın haritası. Fazın **tekrar eden kararları** ayrı sayfalarda:

- [[concepts/f3-bosluk-tek-cins-degildir]] — F3'ün en çok tekrarlanan ayrımı
- [[concepts/f3-oranin-paydasi]] — bir sayı paydası söylenmeden okunamaz
- [[concepts/f3-kanit-once-akil-sonra]] — kanıt katmanının dürüstlük kuralları
- [[concepts/f3-determinizm-bir-kapi-sartidir]] — iki ayrı alt sistem, tek kural
- [[skills/f3-sigma-derleme-kapilari]] — üç kapı, üç farklı sınıf
- [[skills/f3-eslesmeyen-kural-teshisi]] — üç eksenli teşhis yordamı

## Planı yazarken çıkan bulgu — fazın yarısı buna bağlıydı

Teknik plan yazılırken ölçülen tek şey planın yarısını değiştirdi: RCA'nın beş
deterministik korelasyonundan ikisi (**ilk-görülen imza** ve **hacim sapması**)
`template_id` kolonuna dayanıyordu ve o kolon

| Olay | `template_id` |
| --- | --- |
| Ayrıştırması başarısız | doluyor — ama imzanın **ilk** görülüşünde boş |
| Ayrıştırması başarılı | yalnızca **%1** örnekleme |

diye doluyordu. Sebep bir hata değil, K14'ün bilinçli sonucu: sidecar sıcak
yolda değil. Ama sonucu şu: *"yeni bir şey oldu"* diyen tam o satırda kimlik
yok — ve RCA belgesi ilk-görülen imzayı **"tek en güçlü sinyal"** diye
tanımlıyor (`docs/epic/rca-raporu-ozelligi/index.md` §3.1).

## İki karar fazı çiviledi

| # | Karar | Ne değişti |
| --- | --- | --- |
| **K35** | Sıcak yolda **`signature_hash`**, her olayda | İki korelasyon sidecar'dan kurtuldu ve saf SQL'e döndü; örnekleme kalktı, ilk görülüşte boş kalmıyor |
| **K36** | Sigma'da önce **prototip**, sonra kapsam kararı | Hazır `ocsf_pipeline` bizim görünümümüze karşı **0 kural** veriyor; kendi hattımızın maliyeti ölçülmeden kapsam seçmek tahmin olurdu |

K35'in bedeli de kayıtlı: maskeleme artık her olayda koşuyor ve 16 KB sınırını
aşan satırın hash'i boş kalıyor — kabul edilebilir, **ama rapor bunu söylemeli**.

K36'nın dayanağı `docs/epic/sigma-clickhouse-arastirmasi/index.md`: backend
(`pySigma-backend-clickhouse`) üç aylık, iki yıldızlı, tek geliştiricili. Bu
risk, derleme-zamanı kararını **üçüncü kez** gerekçelendirdi: üretilen SQL
repoda versiyonlandığı için proje terk edilse bile mevcut kurallar çalışmaya
devam eder.

## On ticket, iki kol, tek buluşma

`docs/epic/tickets-f3/index.md` fazı ikiye bölüyor ve dilimleme mantığını da
yazıyor: **T29 ve T30 kod değil sayı teslim ediyor**, çünkü F1'in dersi
doğrulanmamış her katmanın kırık çıktığıydı.

| Kol | Ticket'lar | Durum (`docs/epic/f3-yol-haritasi/index.md`, **2026-08-25**) |
| --- | --- | --- |
| **Detection** | T30 prototip → T31 pipeline → T32 derleme → T33 kural yönetimi | T30/T31/T33 **kapandı**; **T32 açık** |
| **Kanıt** | T29 → T34 sözleşme → T35 korelasyonlar → T36 paket → T37 ekran | **beşi de kapandı** |
| **Buluşma** | T38 altın küme | **tek açık kol** — ekran yarısı indi; T31'den devredilen nginx örneği burada |

Yol haritası ayrıca üçüncü bir bağımsız kol sayıyor: F2'nin kapanış
doğrulaması (T27).

## Fazın açık kalan tek kararı

**T30'un kapsam kararı**, ve onu bloke eden şey bir kod değil bir ölçüm: 24
kuralın kaçının eşleşmediği biliniyor, **neden** eşleşmediği bilinmiyor. Ölçüm
üç kutulu:

1. **eşleme eksik** — alan var, biz bağlamamışız
2. **örneklemde desen yok** — bağlasak da eşleşmez
3. **yanlış sebeple eşleşiyor** — sayı yeşil, sebep yanlış

Üçüncü kutu bu turda doğdu ve bugünkü üyesi `asa_teardown_rst`: `RST` araması
`first` ve `burst` sözcüklerine denk geliyordu. **Ne kapı ne `--discover` bunu
söyleyebildi**; örnek dosyanın içeriğini okuyan gördü. Yordam:
[[skills/f3-eslesmeyen-kural-teshisi]].

## Bu fazın sayıları neden bağlayıcı değil

`docs/epic/f3-yol-haritasi/index.md` §8 bunu kendi başlığı altında yazıyor ve
bu, fazın en dürüst paragrafı: **gerçek müşteri verisi olmadan kapanmayacak**
iki kalem var.

- **Baseline pencere uzunluğu.** Süpürme ölçüldü: dirsek, tohumlama düğmesiyle
  **yedi kat** kayıyor (`zipf 2.0 → 7 gün`, `zipf 1.4 → 1 gün`). Tek eğri
  koşturan bir araç "7 gün" derdi ve o sayı verinin değil düğmenin karakteri
  olurdu. Sonuç `SEÇİLEBİLİR TABAN YOK` — bir eksiklik değil, **ölçümün
  sınırının ölçülmüş hâli**.
- **Sigma kapsam oranının anlamı.** Bkz. [[concepts/f3-oranin-paydasi]].

İkisi de F3'ü bloke etmiyor ama fazın sayılarına dayanacak biri önce bu
paragrafı okumalı.

## F3'ün dışında bırakılanlar

| Ne | Nerede | Gerekçe |
| --- | --- | --- |
| LLM yorumu, **beş** tetikleyici, kuyruk/kota | F4 | K22 — kanıt önce |
| Prompt redaksiyon tabanı (T41) | F4 **önkoşulu** | 2026-08-25: prompt'a giden metinde sır tanıyan bileşen yok; olmadan `masked`/`raw` düzeyleri açılmıyor |
| Metrik · trace · topoloji sağlayıcıları | F5 | K21'in bedeli; sözleşme beşini de tanıyor, uygulaması yok |
| Sigma korelasyon kuralları | sonra | backend destekliyor ama önce tekiller otursun |
| PDF export | kapsam dışı | Markdown var; PDF gelirse **aynı metinden** üretilmeli |

K21 ("kanıt türlerinin hepsi") projedeki **tek en büyük kapsam genişlemesi**
olarak kayıtlı: metrik + trace, K1'in "sadece log analiz katmanı" sınırını
aşıyor. Kapsam daraltılmadı, **sıralandı** — ve sözleşmenin beş türü de bugünden
tanıması bu sıralamanın taşıyıcısı.

## Kaynaklar

- `docs/epic/f3-teknik-plan/index.md` — K35, K36, mimari, beş korelasyon
- `docs/epic/f3-yol-haritasi/index.md` — ticket durumları, üç kol, §8 uyarısı
- `docs/epic/tickets-f3/index.md` — on ticket, bağımlılık grafiği, bitti tanımı
- `docs/epic/rca-raporu-ozelligi/index.md` — K19–K22, piyasa konumu, riskler
- `docs/epic/sigma-clickhouse-arastirmasi/index.md` — backend seçiminin doğrulaması
- `docs/epic/tickets-f4/prompt-redaksiyon-tabani/index.md` — T41, F4'ün önkoşulu
