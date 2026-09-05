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
- `rca.quality` aracı: planda yok, bu ticket **eklemiyor**. Uç var, araç kararı
  verilmemiş — açık soru (§6.3).

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

### 6.1 · `Idempotency-Key` nereden geliyor

Plan anahtarı **şart** koşuyor, **kaynağını** söylemiyor. Koordinatörün
çiviledeği kısıt: anahtar **modelin uydurabileceği** bir yerden gelmemeli —
oturumdan ya da çağrı bağlamından türemeli.

Gerekçe: anahtar bir araç argümanı olursa idempotency'nin kendisi modele
bırakılmış olur. Model her denemede yeni bir anahtar üretirse aynı RCA koşumu
**kotayı defalarca** tüketir; aynı anahtarı ısrarla üretirse farklı bir
tetiklemeyi **yanlışlıkla bastırır**. İkisi de sessiz.

**Açık soru:** oturum kimliği mi, araç çağrısının protokol düzeyindeki
kimliği mi? M01'in taşıma oturumuna bağlı.

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

### 6.3 · `GET /v1/rca/quality`

Uç var, planın araç tablosunda karşılığı **yok**. Bilinçli bir dışarıda
bırakma mı yoksa gözden mi kaçtı — **bilmiyorum**, uydurmuyorum. Karar
koordinatörün.

### 6.4 · Bağlam maliyeti

Araç başına **194 belirteç** (M01 ölçtü), en pahalı kalem `description`. Üç
araç ≈ 580 belirteç ve **her bağlamda** taşınıyor.
