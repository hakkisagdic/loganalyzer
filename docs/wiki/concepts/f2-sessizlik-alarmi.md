---
title: Sessizlik alarmı — verinin yokluğunu ölçmek
category: concepts
tags: [log-analiz, mimari, kavram, bizigo]
aliases: [K32, susan cihaz, silence alarm]
relationships:
  - target: "[[concepts/f2-kapsam-tek-kapi]]"
    type: uses
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: extends
  - target: "[[concepts/f2-olculen-kisit-ekrani-tasarlar]]"
    type: related_to
  - target: "[[references/f2-kapanis]]"
    type: derived_from
sources:
  - docs/epic/f2-teknik-plan/index.md
  - docs/epic/tickets-f2/envanter-ekrani/index.md
  - docs/epic/tickets-f2/alarm-motoru/index.md
  - docs/epic/tickets-f2/bildirim-kanallari/index.md
  - docs/epic/tickets-f2/alarm-ekrani/index.md
  - docs/epic/f2-kapanis/index.md
source_digest: "sha256-12/v1 docs/epic/f2-kapanis/index.md=c701d88f78fd docs/epic/f2-teknik-plan/index.md=70c97eb131ab docs/epic/tickets-f2/alarm-ekrani/index.md=961c2c3d4262 docs/epic/tickets-f2/alarm-motoru/index.md=6e126dd480f4 docs/epic/tickets-f2/bildirim-kanallari/index.md=1693c325578e docs/epic/tickets-f2/envanter-ekrani/index.md=89e5d842af30"
summary: Üç alarm tipinin ikisi verinin varlığı, biri yokluğu üzerinde çalışıyor. Sessizlik en zor ve en değerli olanı; eşiğin kimin elinde olduğu ve "son görülme"nin tek yerden gelmesi kararların özü.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:45:00Z
updated: 2026-08-24T17:45:00Z
---

# Sessizlik alarmı — verinin yokluğunu ölçmek

K32'nin alarm kapsamı üç tip: **eşik**, **oran**, **sessizlik** — tek
değerlendirici.

| Tip | Soru | Örnek |
| --- | --- | --- |
| Eşik | Sayı bir sınırı aştı mı | 5 dk'da `action=deny` > 100 |
| Oran | Değişim hızlandı mı | Hata oranı önceki saate göre 3× |
| Sessizlik | Beklenen veri **gelmedi mi** | `fw-core-01` 15 dk'dır susuyor |

Teknik plan ve T21 aynı cümleyi iki kez yazıyor: **sessizlik en zoru ve en
değerlisi.** Diğer ikisi verinin *varlığı* üzerinde, bu *yokluğu* üzerinde
çalışıyor. Ağ tarafında susan bir cihaz, gürültü yapandan tehlikeli.

Bu, [[concepts/sessiz-yanlis-davranis]] sayfasının ürün özelliğine dönüşmüş
hâli: **belirti üretmeyen durumu ölçülebilir kılmak.** ^[inferred]

## "Son görülme" üçüncü kez yazılmıyor

T17 ve T21 aynı veriye ihtiyaç duyuyor, ve `/v1/health/pipeline` göstergeleri
bunun yarısını **zaten** hesaplıyor. T21'in notu açık: *"üçüncü bir kopya
yazılmamalı"*.

Sevk edilen çözüm: `GET /v1/sources/activity` ucunun arkasında T21'in
sessizlik alarmıyla **ortak** olan `IScopedQuery.GetSourceActivityAsync`
duruyor. Ayrı bir uç olmasının sebebi kapsam değil **maliyet**: liste kontrol
düzlemine, etkinlik ClickHouse'a gidiyor ve T15'in kaynak filtresi listeyi her
açılışta çağırıyor.

Kapsam yine tek kapıdan: [[concepts/f2-kapsam-tek-kapi]].

## Eşik ekranın değil kuralın

T17'nin en öğretici kararı: **envanter ekranı kendi sessizlik eşiğini
tanımlamıyor.** "Ne zamandır susuyor" bir **olgu** olarak gösteriliyor
(`last_ingested_at`'ten geçen süre), eşik değil.

Gerekçe doğrudan sessiz kusur mantığı:

> İkinci bir eşiği burada tanımlamak, envanterin "sağlıklı" dediği bir kaynağın
> alarm üretmesi demek olurdu ve ayrıştıkları ancak **biri şikâyet ettiğinde**
> fark edilirdi.

Aynı bölümde ikinci bir ölçüm kararı: `last_event_at` değil
**`last_ingested_at`** kullanılıyor — ilki cihazın kendi saati ve saati şaşmış
bir cihaz "gelecekte" görünebiliyor. Yani sessizliği ölçen saat **bizim**
saatimiz olmak zorunda.

## Motorun maliyet kapıları

K16'nın uyarısı burada bağlayıcı: 50 kişilik bir kurumda herkes cron'lu kural
yazabiliyorsa **tek kötü kural ClickHouse'u doyurur.** T21 bunu iki kabul
kriterine çeviriyor — değerlendiriciye baştan eşzamanlılık limiti ve timeout,
ve kural sayısı arttığında sorgu sayısının **doğrusal ötesi büyümemesi**.

Susturma (bakım penceresi) ve tekrar tetiklenme aralığı da motorun içinde:
pencerede tetiklenme yok, pencere bitince var.

## Tetiklenip kimseye gitmeyen alarm, alarm değil

T22 bu cümleyle açılıyor. Dört kanal (Slack, Teams, e-posta, genel webhook),
şifreli saklanan gizli bilgiler, yeniden deneme ve geri adım, gürültü kontrolü.

Mesajın taşıdığı **bağlantı** küçük görünen ama işe yararlığı belirleyen kısım:
*"bir şey oldu" ile "şuna bak" arasındaki fark.* Bağlantının doğru zaman
aralığını açması kabul kriteri.

İki tasarım kararı bu bağlantının etrafında:

- **Bağlantı kural kimliğini çözmüyor**, filtreler bağlantıya gömülüyor. Sebebi
  zaman: kullanıcı bildirimi günler sonra tıklıyor ve kimliği çözen bir ekran
  **bugünkü** kuralı gösterirdi (`docs/epic/f2-kapanis/index.md` §7).
- Buna rağmen kapanışta çıkan altı kusurdan biri tam buradaydı: `AlertLinkBuilder`
  *"ekran kuralı kimliğinden okuyor"* diye belgelenmişti, **hiç okumuyordu**, ve
  alarm işaret ettiğinden **geniş** bir ekran açıyordu. Belirti yok, hata yok —
  [[concepts/sessiz-yanlis-davranis]].

Bugün iki bekçi bu bağlantıyı tutuyor ve ikisi de kümesini kendi buluyor:
`alert-criteria-bridge` `PARAM` anahtarlarından türüyor, `AlertLinkTargetTests`
dosya sisteminden okuyor (rota var mı, `PARAM` ne diyor). Elle liste yok —
karşılaştırma: [[concepts/elle-tutulan-liste-bekciyi-korlestirir]].

## Önizleme: eşik seçmenin tek kör olmayan yolu

T23'ün önizlemesi kuralı geçmiş veriye karşı koşturup *"son 24 saatte kaç kez
tetiklenirdi"* gösteriyor. Ticket bunu açıkça bir gürültü mekanizması sayıyor:
K16'nın kurumunda **eşiğini görmeden yazılan kural ya hiç tetiklenmiyor ya
herkesi boğuyor.**

Tetiklenme geçmişi ayrıca **"gönderildi" ile "ulaştı"yı ayırıyor** — kanalın
başarısız denemeleri de görünüyor.

## Bugünkü durum ve açık uç

Kapanış §6: *"sussun → alarm → bildirim"* akışı **yalnızca parça hâlinde**;
dört uçtan uca akıştan ikisi akış, ikisi parça.

Ayrıca T21'in kural modeli "kaydedilmiş arama"yla başlıyor ama T15 kayıtlı
aramaları `localStorage`'a yazdı ve **sunucudaki bir kural tarayıcıdaki girdiye
referans veremiyor**. Kuralın bugün neye dayandığı belgelerden çıkmıyor.
^[ambiguous] Ayrıntı: [[concepts/f2-olculen-kisit-ekrani-tasarlar]].

## Kaynaklar

- `docs/epic/f2-teknik-plan/index.md` — K32 ve "Alarm motoru" bölümü
- `docs/epic/tickets-f2/envanter-ekrani/index.md` — T17 eşik/olgu ayrımı, `last_ingested_at`
- `docs/epic/tickets-f2/alarm-motoru/index.md` — T21 tipler, kapsam, maliyet kapıları
- `docs/epic/tickets-f2/bildirim-kanallari/index.md` — T22 kanallar, bağlantı, gürültü
- `docs/epic/tickets-f2/alarm-ekrani/index.md` — T23 önizleme, tetiklenme geçmişi
- `docs/epic/f2-kapanis/index.md` — §2 `AlertLinkBuilder`, §4 bekçiler, §6 akış durumu, §7 bağlantı kararı
