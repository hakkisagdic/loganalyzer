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
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=a06e12975995 docs/epic/f3-teknik-plan/index.md=af05d3fc9852 docs/epic/f3-yol-haritasi/index.md=473574f06a4f docs/epic/rca-raporu-ozelligi/index.md=1da27ea79668 docs/epic/sigma-clickhouse-arastirmasi/index.md=8715e251b522 docs/epic/tickets-f3/index.md=950604929fb8 docs/epic/tickets-f4/prompt-redaksiyon-tabani/index.md=37dc201f87d2"
summary: F3'ün iki kolu (Sigma detection ve RCA kanıtı), planı yarı yarıya değiştiren template_id bulgusu, on ürün ticket'ının durumu, sonradan eklenen üç kapı ticket'ı, ve fazın sayılarının neden bağlayıcı olmadığı.
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

> [!warning] Yukarıdaki `Durum` sütunu **tarihli bir görüntü** (2026-08-25) ve
> ticket dosyalarının kendi `status` alanıyla aynı şey değil. Bugünkü canlı
> örnek T38: bu tablo onu *"tek açık kol"* diye gösteriyor, ticket dosyası
> `status: 2` taşıyor, ve `kalan-is-raporu` 2026-09-05'te güncellenip T38'i
> açık listesinden **çıkardı**. Yani bugün ayrışan taraf bu sayfa.
>
> **Bu sayfa T53'ün bekçisinin kapsamı dışında.** `EpicStatusTests` yalnızca
> `docs/epic` altındaki dört gösterimi karşılaştırıyor; `docs/wiki` sayfalarının
> statü iddialarını hiçbir kapı sınamıyor ve `WikiSourceDigestTests` de
> yakalayamıyor — bu sayfa `kalan-is-raporu`'nu kaynak göstermediği için o
> belge değiştiğinde damga bayatlamıyor. Yukarıdaki cümlenin bir önceki hâli
> tam olarak böyle yanlışlandı: T38'i *"raporda da açık"* diye örnek veriyordu
> ve rapor düzeltilince iddia sessizce yanlış oldu, hiçbir test kırmızı
> yanmadan. Tarihli görüntüyü tarihiyle okuyun.

### Sonradan eklenen beş kapı ticket'ı

Faz on ticket'a bölünmüştü; `docs/epic/tickets-f3/index.md` bugün ayrı bir
başlık altında **beş tane daha** taşıyor. Ayrımın anlamı var: bunlar fazın
ürününü değil **bekçilerini** taşıyor ve bitti tanımına madde eklemiyor.

| # | Ne kapatıyor |
| --- | --- |
| T48 | `Produces<T>` kapısı kaydedilecek servisleri elle listeliyordu; delik dört kez açıldı |
| T50 | Bekçiler bir uzantının *var olduğunu* sınıyordu, *bağlı olduğunu* değil |
| T53 | Ticket `status` alanları bayatlıyordu; statünün dört gösterimi artık birbirini yalanlayamıyor |
| T56 | Damıtılmış vault sayfalarının kaynaklarına karşı denetimi. **Ticket dosyası hiç yazılmadı** — kimliği main'e girdi ve iki tur boyunca hiçbir tabloda kayıtlı değildi |
| T61 | T53'ün **beyan ettiği sınır gerçekleşti**: dört gösterim *birden* yanlış olabiliyordu ve oldu. Beşinci gösterim depo dışından — `git`'in merge geçmişi |

Beşi de aynı sınıfın örneği — [[concepts/elle-tutulan-liste-bekciyi-korlestirir]]
ve `CLAUDE.md` §7'nin *"bir bekçinin sessizce atlaması, bekçinin kendisinden
tehlikelidir"* maddesi. ^[extracted]

> **T61 bu sayfanın yukarıdaki uyarısını kaldırmıyor.** Beşinci gösterim de
> `docs/epic` ile `git` arasında; `docs/wiki` sayfalarının statü iddialarını
> hâlâ hiçbir kapı sınamıyor. T61 kendi belgesinde bunu ayrıca yazıyor: kapı
> **tek yönlü** — `status: 0` iken işin var olduğunu yakalıyor, `status: 2`
> iken işin **yok** olduğunu yakalamıyor, çünkü *"iş yok"* gözlenebilir bir
> olgu değil. ^[extracted]

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
| Prompt redaksiyon tabanı (T41) | F4 **önkoşulu — kapandı** | 2026-08-25: taban yoktu, yazıldı. Üç katman: üretici söz dizimi ve dar bilinen-biçim kümesi maskeliyor, entropi **yalnızca sayıyor**. Altın korpusta yanlış pozitif **0/87** |
| Metrik · trace · topoloji sağlayıcıları | F5 — **karara bağlandı** | K21'in bedeli. F5 kapsam kararı (2026-09-05): **topoloji karşılandı** (envanter öznitelikleri, ilişki grafiği yok), **metrik ve trace kalıcı muaf**. Bkz. [[concepts/f5-soz-ile-sinir-ayri-listeler]] |
| Sigma korelasyon kuralları | sonra | backend destekliyor ama önce tekiller otursun |
| PDF export | kapsam dışı | Markdown var; PDF gelirse **aynı metinden** üretilmeli |

K21 ("kanıt türlerinin hepsi") projedeki **tek en büyük kapsam genişlemesi**
olarak kayıtlı: metrik + trace, K1'in "sadece log analiz katmanı" sınırını
aşıyor. Kapsam F3'te daraltılmadı, **sıralandı** — ve sözleşmenin beş türü de
tanıması bu sıralamanın taşıyıcısıydı.

**2026-09-05'te sıralama bir karara döndü.** F5 kapsam kararı ölçtü ki RCA
belgesinin *"lift topolojiyi telafi ediyor"* gerekçesi yazılmamıştı: ne olay
tablosunda ne envanterde VLAN/upstream/firmware vardı. Topoloji o yüzden
karşılandı; metrik ve trace K1'in sınırında bırakıldı. Ayrımın taşıyıcısı bir
mekanizma, bir not değil — `EvidenceKinds.Exempt` sayılı bir liste ve
`EvidenceStatus.OutOfScope` telde `not_registered`'dan ayrı duruyor.

## Kaynaklar

- `docs/epic/f3-teknik-plan/index.md` — K35, K36, mimari, beş korelasyon
- `docs/epic/f3-yol-haritasi/index.md` — ticket durumları, üç kol, §8 uyarısı
- `docs/epic/tickets-f3/index.md` — on ürün ticket'ı + üç kapı ticket'ı,
  bağımlılık grafiği, bitti tanımı
- `docs/epic/rca-raporu-ozelligi/index.md` — K19–K22, piyasa konumu, riskler
- `docs/epic/sigma-clickhouse-arastirmasi/index.md` — backend seçiminin doğrulaması
- `docs/epic/tickets-f4/prompt-redaksiyon-tabani/index.md` — T41, F4'ün önkoşulu
