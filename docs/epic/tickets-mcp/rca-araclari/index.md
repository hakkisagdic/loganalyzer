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

### 6.2 · T46'nın üç yönlü ayrımı

**Aramadım, doğrulamadım.** Plana güvenerek yazdım. M05'in ilk adımı bunu
kodda görmek olmalı; ayrım bugünkü koddan farklıysa bu belge güncellenmeli.

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
