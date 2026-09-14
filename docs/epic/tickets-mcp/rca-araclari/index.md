---
title: "M05 — bizigo RCA araçları"
kind: ticket
status: 0
---

# M05 — RCA'yı dışarıdan tetiklemek ve okumak

[MCP teknik plan §5](../../mcp-teknik-plan/index.md): `rca.trigger` ·
`rca.runs` · `evidence.bundle`. Üçü de K20'nin (dış API tetikleyicisi) ve
K22'nin (kanıt paketi **LLM'siz** okunabilir) alanında.

## 1 · Bugünkü hâl — ölçüldü

`src/Bizigo.Api` içinde bugün duran RCA uçları:

| Uç | Araç karşılığı |
| --- | --- |
| `POST /v1/rca` | `rca.trigger` |
| `GET /v1/rca/runs` | `rca.runs` |
| `GET /v1/rca/{id}` | `evidence.bundle` / rapor okuma |
| `GET /v1/rca/quality` | — (araç karşılığı planda yok) |

Ayrıca bu hafta girdi: `Bizigo.Rca/Reasoning/` altında `RcaReportStore` ·
`RcaReportDocument` · `ScenarioStepRunner` · `SentenceBinder` ve
`RcaReportEntity` + iki EF göçü. Yani T44/T51'in yazma yolu **var**.

**Ölçmediğim:** T46'nın *"üç yönlü ayrım"*ının bugünkü kod hâli. Plan bunu
`rca.runs`'ın şartı diye yazıyor; ben `RcaRunLifecycleTests` ve
`rca-runs` uçlarının **varlığını** gördüm, ayrımın üç kovasının ne olduğunu
**kodda doğrulamadım**. M05 doğrulayacak.

## 2 · Kapsam

**İçinde**

- `rca.trigger` — `Idempotency-Key` **şart** (§6.1).
- `rca.runs` — T46'nın üç yönlü ayrımı **korunarak**; MCP yüzeyinde ikinci bir
  durum gösterimi doğmuyor.
- `evidence.bundle` — kanıt paketi. K22'nin sınavı: paket **LLM olmadan**
  okunabilir olmalı, yani aracın döndürdüğü şey bir model özeti değil paketin
  kendisi.

**Dışında**

- Kaynak (resource) olarak sunulan belgeler ve abonelik —
  [M07](../kaynaklar-ve-abonelik/index.md).
- Redaksiyon kapısı — [M06](../redaksiyon-kapisi/index.md); ama M05 **onsuz
  sevk edilmiyor**.
- `rca.quality` aracı: **bilinçli olarak ertelendi**, unutulmuş kalem değil —
  §6.3.

## 3 · Kabul kriterleri

1. Üç araç ilan ediliyor, hepsi `McpSurface.Product`.
2. `rca.trigger` `Idempotency-Key` olmadan **çağrılamıyor**, ve anahtar
   **modelin uydurabileceği bir yerden gelmiyor** (§6.1).
3. `rca.runs` T46'nın üç yönlü ayrımını **olduğu gibi** taşıyor; MCP'ye özel
   bir durum kovası eklemiyor. İkinci bir gösterim doğarsa
   [T53](../../tickets-f3/ticket-statusu-bekcisi/index.md)'ün ölçtüğü sınıf
   tekrar eder: aynı şeyin iki gösterimi sessizce ayrışır.
4. `evidence.bundle`'ın çıktısı **paketin kendisi**; bir özet değil.
5. Kapsam [M04](../okuma-araclari/index.md)'ün kapısından geçiyor —
   `AccessScope.System` çağrısı yok.
6. Kırmızı yanabildiği ölçüldü (`CLAUDE.md` §6).

## 4 · Bitti tanımından karşıladıkları

Doğrudan bir madde **sahiplenmiyor** — ve bu yazılı olmalı, yoksa okuyan
kişi M05'i bitti tanımının bir maddesine bağlı sanar. Beslediği maddeler:

- **§2** — üç aracın şeması ve örnek çağrısı M01'in sözleşme kapısından
  geçiyor. Kapıyı M01 kurdu; yükü **araç ekleyen** taşıyor.
- **§7** — kapsam tek kapıdan; sahibi M04, M05 ihlal etmemekle yükümlü.
- **§5** — redaksiyon; sahibi M06, M05 onun tüketicisi.

## 5 · Bağımlılık ve sıra

**M04** (okuma araçları ve kapsam kapısı) **ve T46** (kuyruk ve kota). M04
önce, çünkü kapsamın MCP yüzeyindeki tek kapısını o kuruyor ve M05 onu
kullanıyor — ikinci bir kapı açmak §9'un yasakladığı kopya olurdu.

## 6 · Bilinen sınırlar ve açık sorular

### 6.1 · `Idempotency-Key` — **karara bağlandı, üç ölçümle**

Plan anahtarı **şart** koşuyor, kaynağını söylemiyordu. Kısıt: anahtar
**modelin uydurabileceği** bir yerden gelmemeli.

**Ölçüm 1 — oturumdan türetmek yanlış olurdu.** Akışlanabilir HTTP'nin kipi
bizde **durumsuz** ve bu bir tercih değil, çivilediğimiz `2026-07-28`
revizyonunun varsayılanı; SDK durumlu kipi *"geriye dönük uyum kaçış kapısı"*
diye işaretliyor. Durumsuz kipte her istek **kendi oturumu**, yani `SessionId`
her yeniden denemede değişiyor — eksik değil **yanlış**, ve tam da kaçınmak
istediğimiz "her deneme yeni anahtar" davranışını kaza eseri üretirdi.

**Ölçüm 2 — kalıcı stabil bir anahtar da yanlış.** `RcaAdmission.AdmitAsync`'in
idempotency araması **zaman sınırsız**: bir anahtar bir kez kullanıldıysa
sonsuza kadar aynı koşumu döndürüyor. Yalnızca özne + kapsamdan türeyen bir
anahtar, aynı alarmın altı ay sonraki meşru RCA'sını da bastırırdı.

**Ölçüm 3 — REST anahtarı istemciden alıyor** (`Idempotency-Key` başlığı, hiç
türetme yok). Yani paylaşılacak bir türetme yok; MCP'ninki **MCP'ye özgü bir
davranış** ve yazılı olması gerekiyor:

> REST anahtarı çağırandan alır çünkü çağıran bir dış sistemdir ve kendi tekrar
> denemesini kendi tanır. MCP'de çağıran bir model ve anahtarı ona sordurmak,
> idempotency'yi tam da korunmak istenen tarafa vermek olurdu — bu yüzden
> sunucu türetiyor.

**Karar:** `McpIdempotency.KeyFor(subject, ownerGroups, from, to)` →
`mcp:{subject}:{sha256(özne + kapsam + pencere)}`.

| Senaryo | Sonuç |
| --- | --- |
| Model aynı çağrıyı tekrarlıyor | **aynı anahtar** → tek RCA, kota bir kez |
| Aynı alarmın başka bir penceresi | farklı anahtar → koşuyor |
| Aylar sonra aynı alarm, yeni pencere | farklı anahtar → koşuyor |
| Model "taze anahtar" uydurmaya çalışıyor | **yapamıyor** — kimlik alanlarını değiştirmek *farklı bir RCA* istemek demek |

**Hash'e yalnızca kimlik taşıyan alanlar giriyor.** Argüman torbasının tamamı
girseydi model anlamsız bir alan ekleyip anahtarı değiştirebilirdi.
`McpIdempotencyTests.Anahtarin_girdileri_yazili_kalıyor` imzayı tutuyor: yeni
bir parametre eklemek testi kırıyor, yani o alanın anahtara girmesi **bilinçli
bir karar** oluyor.

**Kısıtın harfi değil ruhu.** Model anahtarın *girdilerini* seçiyor — farklı
bir pencere isteyerek farklı bir anahtar üretebilir. Bu bir kaçak değil: o
zaman **farklı bir RCA** istemiş oluyor, aynı RCA'yı iki kez değil.
Engellenmek istenen şey buydu.

### 6.1.1 · Ajan tetiklemesi yeni bir **kaynak**

Anahtarın kaynağını araştırırken çıkan asıl bulgu: bu üründe
`Idempotency-Key` bir teknik ayrıntı değil, **kaynağın kendisini belirleyen
şey**. `POST /v1/rca` anahtarlı talebi `External`, anahtarsızı `Manual`
sayıyor.

MCP ikisine de uymuyor ve ikisi de **yanlış cevap** verirdi:

- `External` **yalan söylerdi** — MCP bir dış sistem değil, kullanıcının ajanı.
  `Source` alanı `rca.runs`'ta *"bunu ne tetikledi"* sorusunu cevaplıyor ve
  cevabın yanlış olması §7'nin sınıfı: alan dolu, değer makul, anlam yanlış.
- `Manual` **kotayı yerdi** — anahtarsız talep idempotent değil.

**Karar (koordinatör):** `RcaTriggerSource.Agent = 4` ve
`RcaTriggerSources.FromAgent(...)`. Kapalı küme bu depoda *"eklemek bilinçli
bir hareket olsun"* diye kapalı, **eklenemez** diye değil.

**Planın şartı karşılandı; anahtarın kaynak ayrımını belirlemesi kodun kendi
kararıydı ve üçüncü bir değerle korundu.**

**Ölçülen yan etki:** `agent` kelimesi senaryo plugin tetikleyici sözlüğüne de
girdi, çünkü o sözlük enumdan **türüyor** (T45). Niyet edilmemişti; bırakıldı,
çünkü dışlamak T45'in kaldırdığı muafiyet listesini geri getirirdi — ve
ajanın tetiklediği RCA gerçekten farklı bir bağlamda koşuyor: çıktısını bir
model okuyacak. `ScenarioPluginLoaderTests` kırmızı yandı ve bilinçli bir
harekete zorladı; gerekçe orada yazılı.

**Kota:** `RcaQuotaGate.EffectiveLimit` rezervi yalnızca `Schedule` için
ayırıyor, dolayısıyla ajan **tam günlük limiti** görüyor. Ölçüldü,
varsayılmadı.

### 6.1.2 · MCP'den bağımsız bir kalem: penceresiz idempotency araması

`RcaAdmission.AdmitAsync:141` anahtarı **zaman sınırı olmadan** arıyor. Bu
yalnızca MCP'yi değil **REST istemcisini de** ilgilendiriyor: aylar sonra aynı
anahtarı yeniden kullanan bir istemci sessizce eski koşumu alır, yeni bir RCA
istediğini sanarak. Hata yok, sayaç yok, belirti yok.

**M05 düzeltmiyor** — kapsamının dışında. Koordinatör ayrı ticket açıyor.

### 6.2 · T46'nın ayrımı — **ölçüldü, ve "üç yönlü" eksik bir tarif**

Belgenin ilk hâli *"aramadım, doğrulamadım"* diyordu. M05 koda baktı; ayrım
plandakinden **hem daha zengin hem başka bir şekilde** duruyor.

**Ölçülen (`src/Bizigo.ControlPlane/RcaTriggerEntities.cs`):**

| Enum | Değerler |
| --- | --- |
| `RcaRunState` | `Rejected` · `Queued` · `Running` · `Complete` · `Empty` · `Truncated` · `Cancelled` · `Failed` — **sekiz** |
| `RcaRejectionReason` | `None` · `Debounced` · `DepthExceeded` · `AncestorRepeat` · `QuotaExceeded` — **beş** |

**Planın cümlesindeki asıl yanlış bir sayı değil, bir yer:** `QuotaExceeded`
bir **koşum durumu değil**. `Rejected` bir koşumun **ret sebebi**. Yani
*"`Empty` ≠ `QuotaExceeded` ≠ `Cancelled`"* tek bir kapalı kümenin üç değeri
gibi okunuyor ama **iki ayrı enumun** üzerine yayılmış. Sonucu doğrudan M05'i
bağlıyor: `rca.runs` yalnızca durumu döndürürse *"kota doldu"* düz bir
`rejected` olur ve debounce / derinlik / döngü ile **ayırt edilemez** hâle
gelir — planın önlemek istediği şeyin ta kendisi.

**Teldeki cevap üç değil dört yönlü.** `RcaRunListResponse`'un kendi belgesi
şöyle yazıyor:

| Cevap | Anlamı |
| --- | --- |
| `rejected` satırı | Kapı reddetti — **bakılmadı** (sebep `reason`'da) |
| `empty` satırı | Bakıldı, **bulunamadı** |
| `cancelled` satırı | Başladı, bir sınırda **kesildi** |
| **boş liste** | **Hiç tetiklenmedi** |

Dördüncüsü ancak reddin bir satır olarak kalmasıyla doğru kalıyor.

**Ve bağımsız bir ikinci eksen var:** `counts_against_quota`. `Cancelled`
kotadan **düşülüyor**, `Rejected` düşülmüyor. Durumu kota anlamıyla
karıştırmak *"iptal edildi, o hâlde bedava"* çıkarımını doğurur.

**M05'in bundan çıkardığı karar:** araç ikinci bir gösterim üretmiyor. Var olan
`RcaRunResponse` ve `RcaRunLifecycle.Describe(state, rejection)` — yani cümlenin
tek sahibi — MCP yüzeyinde de kullanılıyor. Aynı şeyin iki gösterimi bu depoda
sessizce ayrışıyor (T53'ün ölçtüğü sınıf).

### 6.3 · `GET /v1/rca/quality` — bilinçli erteleme

Uç var, planın araç tablosunda karşılığı yok. Bu bir gözden kaçma **değil**:
**`rca.quality` bilinçli olarak ertelendi.**

Sebep, ölçünün **şeklinin hareket hâlinde** olması. Kalite ölçüsü F4'ün kabul
sınavı ve T47 onu hâlâ yazıyor — bu hafta iki alan ekledi
(`CorrectFindingRank`, `Unspecified`) ve payda kararı bir kez değişti.

Şekli oynayan bir şeyi protokolde ilan etmek, `CLAUDE.md` §8'in *"tüketicisi
olmayan bir tip tahmindir"* maddesinin **daha kötü** hâli: burada tüketici
**var**, ama sözleşme yarın farklı olacak — ve **MCP'de ilan edilen bir şemayı
geri almak istemcileri kırıyor**. Bir ekran sözleşmesini kırmak bedavayken
(§8, *"kırmak bedava iken kır"*), ilan edilmiş bir MCP şemasını kırmak değil.

**T47 kapandığında yeniden değerlendirilecek.** Bu bir kapsam kararı ve
sahibi bu satır.

### 6.4 · Bağlam maliyeti

Araç başına **194 belirteç** (M01 ölçtü), en pahalı kalem `description`. Üç
araç ≈ 580 belirteç ve **her bağlamda** taşınıyor.
