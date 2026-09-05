---
title: Determinizm bir kapının ön şartıdır
category: concepts
tags: [test, veri-deposu, kavram, bizigo]
aliases: [aynı girdi aynı çıktı, sürüklenme kapısı, içerik hash'i]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[skills/f3-sigma-derleme-kapilari]]"
    type: related_to
  - target: "[[concepts/f3-kanit-once-akil-sonra]]"
    type: related_to
  - target: "[[references/f3-detection-ve-rca-kaniti]]"
    type: derived_from
sources:
  - docs/epic/t32-derleme-tasarimi/index.md
  - docs/epic/t36-kanit-paketi/index.md
  - docs/epic/sigma-clickhouse-arastirmasi/index.md
  - docs/epic/tickets-f3/sigma-derleme/index.md
source_digest: "sha256-12/v1 docs/epic/sigma-clickhouse-arastirmasi/index.md=8715e251b522 docs/epic/t32-derleme-tasarimi/index.md=ca8f23c25262 docs/epic/t36-kanit-paketi/index.md=3916d770a854 docs/epic/tickets-f3/sigma-derleme/index.md=d117ba100cfd"
summary: F3'te iki ilgisiz alt sistem (Sigma SQL derlemesi ve kanıt paketi) aynı kurala vardı — karşılaştırılan çıktıya duvar saati, ağ ya da sıra belirsizliği karışırsa kapı ya kalkar ya yumuşatılır.
provenance:
  extracted: 0.8
  inferred: 0.2
  ambiguous: 0.0
base_confidence: 0.8
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T18:40:00Z
updated: 2026-08-24T18:40:00Z
---

# Determinizm bir kapının ön şartıdır

F3'te iki alt sistem birbirinden habersiz aynı sonuca vardı:

- **Sigma derleme hattı** — *"aynı girdi, aynı SQL"*, yoksa sürüklenme kapısı
  birebir karşılaştırma yapamaz.
- **Kanıt paketi** — *"aynı girdiyle aynı paket"*, yoksa `content_hash` her
  koşumda kayar ve *"bu aynı paket mi"* sorusu hep hayır cevaplanır.

İkisinin ortak cümlesi bu sayfanın tamamı:

> Bir kapı çıktıyı çıktıyla karşılaştırıyorsa, o çıktıya karışan her **duvar
> saati**, **ağ** ve **sıra belirsizliği** kapıyı öldürür — ya kaldırtarak ya
> yumuşattırarak.

## Sigma tarafı: tarih hiçbir yerde yok

Kabul kriteri A *"aynı girdi, aynı SQL"*, B *"çıktı depodakiyle aynı değilse CI
düşsün"*. Bu ikisi birlikte, kural dosyasına **derleme tarihi** yazmayı
imkânsız kılıyor: her koşum farklı bayt üretirdi ve kapı ya kaldırılırdı ya
tarihi görmezden gelen bir istisnayla yumuşatılırdı.

Uygulama sırasında ikinci bir düzeltme geldi ve öğreticiliği ilkinden fazla:
tarih *"manifest'in koşum başlığında dursun"* denmişti — **yanlış**, çünkü
**manifest de karşılaştırılan çıktının parçası**. Yani tarihi oraya koymak
manifest'in kendi kapısını öldürüyordu.

Sonuç: **derleme tarihi hiçbir yerde yok.** Kaybedilen bilgi de yok — manifest
commit'li, yani `git log detections/sigma/manifest.json` sorunun cevabı ve git
bunu daha güvenilir tutuyor. Kaybedilen tek şey aynı bilginin ikinci,
sürüklenebilir kopyası.

Ticket'ın "derleme tarihi" maddesi hakkında verilen hüküm de kayda değer:
**yanlış değil, eksik düşünülmüş** — yazıldığı sırada sürüklenme kapısı henüz
tasarımda yoktu, ikisi aynı anda var olamıyor ve kapı daha değerli.

Girdinin parçası olan sürümler duruyor: `ruleset_commit`, `pipeline_version`,
`pipeline_sha`. Onlar koşumun değil **girdinin** özelliği, ve değiştiklerinde
çıktı da değişiyor.

## Girdi tanımı: cron yok, ağ yok

Kriter A *"aynı girdi"* diyorsa kural setinin sürümü **girdinin tanımına**
giriyor. Buradan iki bilinçli sapma çıktı:

- **Kural seti sabit SHA'ya çivileniyor, elle yükseltiliyor.** Referans akış
  (`clicksiem/sigma_rules`) günlük cron kullanıyor; biz kullanmıyoruz. Günlük
  cron derlemeyi tekrarlanamaz yapar ve kapı bir sabahtan diğerine kendi kendine
  kırmızı yanar — **kimsenin bir şey değiştirmediği bir günde düşen bir kapı,
  kısa sürede görmezden gelinen bir kapıdır.** Sürüklenme bir sabahta değil, bir
  **commit'te** görünüyor.
- **Kurallar depoya kopyalanıyor, koşum anında indirilmiyor.** Üç gerekçe:
  (1) **ağ, kapının gerekçesi olamaz** — `ci.yml` bunu zaten yaşadı, kurulum
  indirmesi sınırlandığında iş tek test koşmadan öldü; (2) *"proje terk edilse
  bile kurallar çalışır"* gerekçesi yarım kalırdı, çünkü kaynak yalnızca yukarı
  akıştaysa SQL yeniden **üretilemez**; (3) **kapsam bir liste olmalı, bir filtre
  değil** — yukarı akışa karşı değerlendirilen bir filtre, yukarı akış kural
  eklediğinde korpusu **sessizce** değiştirir.

## Kanıt tarafı: hash'ten duvar saati çıkarılıyor

Aynı kural kanıt paketinde birebir tekrar ediyor: içerik hash'i `id`,
`gathered_at` ve dilim `duration`'larını **dışarıda** bırakıyor. Belge, aynı
ayrımın `ReplayDiff`'te de aynı sebeple bulunduğunu not ediyor — yani desen
F3'te doğmadı, F3'te üçüncü kez kullanıldı.

Sıra tarafı daha ince ve **gerçek bir açık ortaya çıkardı**:

- Dilimler **sağlayıcı kimliğine göre sıralanarak** hash'leniyor, çünkü
  sağlayıcılar paralel koşuyor ve kayıt sırası DI'nin insafında.
- Dilim **içindeki** satır sırası korunuyor, çünkü **sıra sinyalin kendisi**:
  yayılma zaman sıralı, ilk-görülen hacim sıralı.

Bu ikincisi sorguların **kararlı bir `ORDER BY` eşitlik bozucusu** taşımasını
şart koşuyor. Dört korelasyon sorgusundan üçü taşıyordu; `GetAttributeLiftAsync`
taşımıyordu:

```sql
ORDER BY window_count DESC          -- eşit sayılarda sıra sunucunun insafında
```

Eşit `window_count`'lu iki değer koşumdan koşuma yer değiştirir, hash kayar ve
*"aynı paket mi"* sorusu **sessizce hep hayır** cevaplanır. Düzeltildi
(`ORDER BY window_count DESC, value`) ve gerekçesi SQL'in içinde.

Bu tam olarak [[concepts/sessiz-yanlis-davranis]]: kabul kriteri yeşil görünen
bir mekanizmayla ölçülemez hâle geliyordu.

## Ağ tarafı: aynı şart yükseltmeye de uygulanıyor

"Cron yok, ağ yok" bugünkü hattın özelliği; kural seti bir gün SigmaHQ'dan
çekilecek ve o yol tasarlandığında **aynı şart** dört karara dönüştü:

| Karar | Determinizm gerekçesi |
| --- | --- |
| Çivilenmiş commit'in **tarball'ı**, `git clone` değil | Tarball tanım gereği tekrarlanabilir; klonlama ayrıca 7.400+ kurallık bir deponun tüm geçmişini indirir |
| Seçim bir **liste**, filtre değil | Yukarı akışa karşı **duran** bir filtre, yukarı akış kural eklediğinde korpusu sessizce değiştirir — girdi değişmiş olur ama kimse istememiştir |
| Ölçüt **`logsource`**, dizin değil | SigmaHQ'nun dizin ağacı `logsource.category` ile örtüşmüyor; dizine göre seçmek kategorilerden birini kaçırır |
| İndirilen ağaç geçici dizine, sonra **takas** | Yarım bir yükseltme kısmen yeni kısmen eski bir korpus bırakamaz |

Üçüncüsü ile ikincisi aynı ilkenin iki yüzü: **filtre bir insan kararının girdisi
olabilir, hattın çalışma zamanı davranışı olamaz.**

Dördüncüsü replay'in `REPLACE PARTITION` yarasının kardeşi — orada da atomiklik
varsayılmıştı ve okuma ile değiştirme arasındaki pencerede yazılan satırlar
sessizce siliniyordu.

## Determinizmin ikinci yüzü: etiket sürüklenmesi

Determinizm yalnızca "aynı çıktı" değil, **çıktının anlamının etiketiyle
birlikte değişmesi** demek. T32 bunu bir kapıyla zorluyor:

> `pipeline_sha` değişince `pipeline_version` de değişmeli.

Gerekçe: 269 dosyanın **aynı etiket altında** yeniden anlamlanması, sessiz
ayrışmanın ders kitabı hâli. Sürtünme tek satır; bedeli ise *"bu SQL hangi
eşlemeyle üretildi"* sorusunun bir daha cevaplanamaması olurdu.

Aynı mantık kanıt paketinde `schema_version` ve `MinReadableSchemaVersion`'un
**ayrı sabitler** olmasıyla karşılığını buluyor: *"ne yazıyoruz"* ile *"ne
okuyabiliyoruz"* tek sayıya bağlanırsa, sürümü artıran ilk kişi bütün geçmişi
okunamaz yapar **ve fark etmez**.

## İki olay karışmasın diye: manifest iki katmanlı

Determinizm kurulunca ikinci soru doğuyor — **çıktı neden değişti?** Manifest üç
olayı mekanik olarak ayırıyor:

| Olay | Manifest'te | Gözden geçirenin sorusu |
| --- | --- | --- |
| Kural seti yükseltildi | `ruleset_changed`, `source_changed` dolu | "Hangi kuralların kaynağı oynadı?" |
| Pipeline değişti | `pipeline_changed`, `source_changed` **boş** | "Hangi kuralın anlamı oynadı?" |
| İkisi de değişmedi ama çıktı değişti | `output_changed_without_source_change` | **"Ne oldu?"** |

Üçüncü satır asıl bekçi: bir yükseltmede o kümenin **boş olması beklenir**;
dolu çıkarsa iki değişiklik tek diff'in içinde saklanmıştır.

Aynı küme T33'ün kriter boşluğunu da kapatıyor: kriter *"kaynak kural sürümü
değiştiğinde"* diyordu ve **iki olaydan yalnızca birini** kapsıyordu — kullanıcının
**etkin** kuralının ne yakaladığı oynayıp kaynak hiç değişmediğinde sessiz
kalıyordu. Doğru ölçüt `output_sha`.

## Determinizmin bir bedeli de var

Sürüklenme kapısının çalışması için **eski çıktının hayatta kalmaması** gerekiyor.
Referans akışın tuzağı ölçülmüş: dönüşüm başarısız olduğunda eski çıktı depoda
kalıyor ve dosya sayıları **%100 uyum gibi görünüyor**.

Çözüm *"işaretle"* değil **"hiç bırakma"**: hat çıktıyı geçici dizine üretir,
bittiğinde hedef dizinle **takas eder**. Bayat dosyanın hayatta kalabileceği bir
kod yolu yok — *"silmeyi unutma"* diye bir yol olmadığı için unutulamaz.

## Genelleme

Üç soru, yeni bir kapı yazarken sırayla sorulabilir: ^[inferred]

1. Kapının karşılaştırdığı çıktıda **duvar saati** var mı? (tarih, süre, `id`)
2. Çıktının üretimi **ağa** bağlı mı? (indirme, cron, yukarı akış filtresi)
3. Çıktının **sırası** belirli mi? (paralellik, `ORDER BY` eşitlik bozucusu)

Üçünden birine "evet" ise kapı bugün yeşil yansa bile ilk sürtünmede
gevşetilecek — ve gevşetilmiş bir kapı, olmayan kapıyla aynı sonucu verip
üstüne "bu soru sorulmuş" yanılsaması bırakıyor.

## Kaynaklar

- `docs/epic/t32-derleme-tasarimi/index.md` — §4 versiyonlama, çivi ve kopyalama kararları
- `docs/epic/t36-kanit-paketi/index.md` — §3 determinizm ve bulunan açık
- `docs/epic/sigma-clickhouse-arastirmasi/index.md` — dosya sayımı tuzağı, terk edilme sigortası
- `docs/epic/tickets-f3/sigma-derleme/index.md` — kabul kriterleri A ve B
