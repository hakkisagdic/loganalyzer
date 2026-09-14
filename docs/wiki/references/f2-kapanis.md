---
title: F2 kapanışı — görünürlük sevk edildi
category: references
tags: [surec, test, kaynak-ozeti, bizigo]
aliases: [f2-kapanis, F2 kapanış belgesi]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: related_to
  - target: "[[concepts/elle-tutulan-liste-bekciyi-korlestirir]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: derived_from
sources: [docs/epic/f2-kapanis/index.md, docs/epic/t27-kapanis-taramasi/index.md, README.md]
source_digest: "sha256-12/v1 README.md=c8129c49b36e docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/t27-kapanis-taramasi/index.md=fc23cd892d11"
summary: docs/epic/f2-kapanis/index.md özeti — F2'nin ölçülen kısıtları, yanlış çıkan altı iddiası, bekçilerin durumu ve F3'e devredilen beş soru.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
---

# F2 kapanışı — görünürlük sevk edildi

`docs/epic/f2-kapanis/index.md` belgesinin özeti. F1 boru hattını kurdu; F2 ona
bir yüz verdi. Belge kendi ifadesiyle **bir başarı raporu değil**: F1'in
kapanışı işe yaradıysa kendi kırıklarını saydığı için yaradı.

Kapanış anındaki sayılar (`main` = `962ccae`): 18 proje 0 uyarı, **740** birim
testi (3 atlandı), **247** UI testi, **96** entegrasyon testi, `api:check`
birebir, `tsc` temiz.

## Ölçülen kısıtlar (§1)

Bunlar tercih değil kısıt — F3 bunlara dayanacak.

- **Keycloak scope'ları.** Realm dosyası `clientScopes` verdiği için yerleşik
  scope'lar hiç oluşmuyor. `openid` geçiyor; `openid profile` ve
  `openid profile email` `invalid_scope` alıyor. F1'den kalma OIDC işleyicisi
  **üçüncü satırı istiyordu** — tarayıcı akışı sevk edilseydi ilk kullanıcının
  ilk girişinde patlardı ve hiçbir test bunu görmezdi.
- **Sıcak yol maliyeti (K35).** İki koşum: 1,46× ve 1,62×. İkincisinde
  ölçümün kirlendiği kanıtlandı — bkz. [[skills/paralel-ajan-koordinasyonu]].
- **Sigma.** `compiled=24, runs=14`, `match_ratio = %0`. Sıfır, veri yanlış
  olduğu için; veriden bağımsız tek kullanılabilir sayı **on kuralın var olmayan
  kolonlara SQL üretmesi**.
- **F1'in iki ölçümü hâlâ geçerli.** Tam metin indeksi ~10-11 karakterden sonra
  seçici; keyset sayfalama ancak `owner_group` + `source_id` verildiğinde sabit
  süreli. İkisi de ekranda karşılığını buldu.
- **Entegrasyon paketi yerelde koştu: 93/93.** Bugüne kadar yalnızca CI
  koşuyordu — yani paketin yeşilliği tek bir ortamın yeşilliğiydi.

## Yanlış çıkan altı iddia (§2)

Fazın en değerli çıktısı. Altısı da aynı şekle sahip ve hiçbiri koşturarak
bulunmadı. Ayrıntı ve tam tablo: [[concepts/sessiz-yanlis-davranis]].

Belgenin kural hâline getirdiği cümle:

> Bir yorumun bir şeyi iddia etmesi, kodun onu yaptığı anlamına gelmiyor — ve
> ikisi ayrıştığında belirti üretmiyor.

İkinci ve üçüncü kalıp — elle liste körlüğü ve doğrulama listesinden düşen kapı
— ayrı sayfada: [[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

## Bilinçli kabul edilen riskler (§3)

Altı kalem kapanmadan bırakıldı, her biri gerekçesiyle. İkisi özellikle
öğretici çünkü **alternatifi daha sessiz bir kusur üretiyordu**:

- **B9** — `GetDocument.Insider` adına bağımlılık. Ayrımı ortam değişkenine
  bağlamak daha kötü olurdu: bayrak üretime taşınabilir ve göçleri sessizce
  atlayan bir API bırakırdı. Araç adı değişirse belge üretimi kırmızı yanıyor —
  hatanın doğru yönü.
- **B14** — şema tamamlama listesi motorun kopyası. Bedeli "öneri eksik
  görünüyor"; sessiz yanlış davranış **değil**.

> Bir riski kabul etmek onu unutmak değil; bu tablo o yüzden var.

## Bekçilerin durumu (§4)

Fazın kalıcı çıktısı ekranlar değil bekçiler. Sekiz bekçinin **kümesini nasıl
bulduğu** ve **kırmızı yandığının ölçülüp ölçülmediği** tablo hâlinde duruyor.
`ExpectedExemptCount` dışında **elle beslenen liste kalmadı**, ve o zaten
denetlenen kümeyi değil beklenen sayıyı tutuyor.

Devralana bırakılan uyarı: **bekçinin kendisi de bayatlayabiliyor.** Sayıyı
sabitleyen bir test, sayı *düzeldiğinde de* düşer.

## F3'e devredilen sorular (§5)

1. Baseline penceresi ne kadar olmalı?
2. Sigma kapsamı gerçekte ne?
3. `Weight` nasıl normalleştirilecek?
4. Kuru koşu gerçek çalıştırmayla aynı sonucu veriyor mu?
5. Replay canlı ingest'i bozmuyor — artık doğru mu? (Kapatma **yapısal**,
   ölçümle değil; yük altında ölçüm hâlâ yapılmadı.)

## Bitiş şartı ve belgenin kendi hatası (§6)

T27'nin kabul kriteri "bekleme listesi boşalmadan F2 bitmiş sayılmaz" diyordu;
liste 21 satırdan sıfıra indi. Ama bölüm **bir kez eksik yazıldı**: `Pending`
bitiş şartlarından biriydi, tamamı değil — dört uçtan uca akış ve iki çapraz
doğrulama da isteniyordu ve belge onlara hiç değinmiyordu.

Bugünkü tabloda dört uçtan uca akış hâlâ ⚠️: **ikisi akış, ikisi parça.**
Akış olan ikiden biri parser yayını — yordamı
[[skills/f2-atomik-yayin-akisi]].

Belgenin kendi üstüne yazdığı gözlem, bu vault için de geçerli bir uyarı: ^[inferred]

> Bir kapanış belgesinin "fazın bitiş şartı" bölümü eksik olduğunda, belge
> okuyanı "faz bitti" diye bırakıyor — ve bu belge tam olarak onu yaptı.

## Bilerek yapılmayanlar (§7)

Beş kalem, hepsi gerekçeli: cihaz config için REST/SNMP taşıması (erken
genelleme), ham config yedekleme (saklama/sızıntı sorumluluğu doğar), connector
için ayrı ekran, alarm bağlantısının kural kimliğini çözmesi (günler sonra
tıklandığında **bugünkü** kuralı gösterirdi), bölüm içi satır taşınmasını fark
saymak (her `write mem`'de yüzlerce sahte değişiklik).

## Kaynaklar

- `docs/epic/f2-kapanis/index.md` — kapanış belgesinin tamamı
- `docs/epic/t27-kapanis-taramasi/index.md` — §6'daki eksikliği bulan tarama
- `README.md` — F1/F2 ticket listeleri
