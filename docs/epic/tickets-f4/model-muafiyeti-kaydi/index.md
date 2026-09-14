---
title: "T54 — Sessiz muafiyet: model sınırı muafiyeti koşum kaydına bağlı değil"
kind: ticket
status: 1
---

# T54 — Muafiyet yapılandırmada var, raporda yok

**Bağımlılık:** T42 (alan), **T44 (kaydın kendisi — açık)** · **Sonraki:** —

## Sorun

`kalan-is-raporu` §4'te yazılı:

> **`model_boundary_override_reason` koşum kaydına bağlanmadı** (T42). Muafiyet
> yapılandırmada görünüyor, **raporda görünmüyor** — muafiyetin sessiz olduğu
> hâle yakın.

Biri model sınırı muafiyeti açıyor, gerekçesini yapılandırmaya yazıyor, ve o
koşumun raporuna bakan hiç kimse muafiyetin uygulandığını göremiyor. Rapor,
muafiyetsiz bir koşumdan **ayırt edilemiyor** — §7'nin tam sınıfı.

## Bugünkü hâl ölçüldü

**Alan var ve doğru şekilde duruyor.** `ModelEndpoint.BoundaryOverrideReason`
(`src/Bizigo.Rca/Models/ModelEndpoint.cs:72`) ve `AuditFields()` ikisini birden
veriyor:

```csharp
["model_boundary_overridden"] = BoundaryOverridden,
["model_boundary_override_reason"] = BoundaryOverrideReason ?? string.Empty,
```

Yorumu da doğru: *"Kayda ve rapora giden tek satır — muafiyet gizlenmiyor."*

**Ama `AuditFields()`'ın üretimde tüketicisi yok.** Çağıranlar: `ModelRequest`
(aynı sözlüğü genişletiyor) ve iki birim testi. Başka hiçbir yer.

**Ve model katmanı ile koşum katmanı bugün main'de hiç bağlı değil.**
`src/Bizigo.Rca/Models/*` ile `RcaAdmission` / `RcaRunLifecycle` /
`RcaRunEndpoints` arasında tek bir referans yok. Yani "koşum kaydına bağla"
denilen bağ, main'de var olmayan bir noktaya bağlanacak.

## Asıl bulgu — T44 bunu kapatmıyor, **kapanmış gibi gösterecek**

Bağın kurulacağı yer T44'te (`t44-llm-adimlari`, **birleşmemiş**) ve orada
zaten kurulmuş durumda:

| Katman | T44'teki hâli | Muafiyet var mı |
| --- | --- | --- |
| `ScenarioStepRunner` | `_endpoint` elinde (`ModelEndpoint`) | — |
| `RcaReportModelInfo` | `Provider, Model, PromptTokens, CompletionTokens, UnreportedAttempts` | **hayır** |
| `RcaReportEntity` + `AddRcaReports` göçü | belge kalıcı | **hayır** |
| API yanıtı (`EvidenceResponses`) | altı alan geçiyor | **hayır** |
| Ekran (`ReportView.tsx`) | model bloğu çiziliyor | **hayır** |

T44 birleştiğinde rapor **modeli söyleyen bir bölüm** kazanıyor — sağlayıcı,
model adı, belirteç sayıları — ve muafiyeti **söylemiyor**. Bu, bugünkü
hâlden **kötü**: bugün raporda model hakkında hiçbir şey yok ve okuyan da bir
şey olmadığını biliyor. T44'ten sonra model hakkında bir bölüm olacak ve
okuyan onu **tam** sanacak.

> Muafiyetin sessiz olması tehlikeliydi; **muafiyetin, muafiyeti gösterdiğini
> iddia eden bir bölümün içinde sessiz olması** daha tehlikeli.

## Sözleşme — çivilenmiş (§9)

Muafiyet `RcaReportModelInfo`'ya giriyor, ayrı bir yere değil:

```csharp
public sealed record RcaReportModelInfo(
    string Provider,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    int UnreportedAttempts,
    bool BoundaryOverridden,          // YENİ
    string? BoundaryOverrideReason);  // YENİ
```

Çağrı noktası tek: `ScenarioStepRunner`'ın `ModelInfo = new RcaReportModelInfo(…)`
satırı, `_endpoint.BoundaryOverridden` ve `_endpoint.BoundaryOverrideReason`
ile. Alanlar `AuditFields()`'takilerle **aynı adları** taşıyor
(`model_boundary_overridden`, `model_boundary_override_reason`) — ikinci bir
adlandırma, iki gösterimin ayrışabileceği bir yer daha açardı.

### Üç hâl, ve `null`'ın neyi söylediği

Koordinatörün işaret ettiği ayrım burada **zaten çözülü**, ve çözümün nerede
durduğu yazılmalı yoksa dördüncü bir alan eklenir:

| Hâl | Nasıl görünüyor |
| --- | --- |
| Model hiç koşmadı | `ModelInfo`'nun **kendisi yok** — rapor `null` model yorumu taşıyor (T51'in kararı) |
| Model koştu, sınır normal doğrulandı | `BoundaryOverridden = false`, gerekçe `null` |
| Model koştu, muafiyet uygulandı | `BoundaryOverridden = true`, gerekçe **dolu** |

Yani `BoundaryOverrideReason == null` tek başına belirsiz değil: yanındaki bool
onu ayırıyor, ve "hiç koşmadı" hâli bir katman yukarıda zaten ayrılmış. Bu iki
alanın **ikisi birden** gerekiyor — yalnız gerekçe taşınsaydı, boş bir gerekçe
*"muafiyet yok"* ile *"muafiyet var ama gerekçe yazılmamış"* arasında ayrım
yapamazdı. İkincisi `ModelBoundaryGate` tarafından zaten reddediliyor
(`ModelBoundaryGate.cs:186`), ama kaydın o kapıya **güvenmesi** gerekmiyor:
kayıt kendi başına okunabilmeli.

### API ve ekran

§8: yanıt tipi tüketiciyle birlikte gelir. Model bloğu **ekranda zaten var**
(T44), dolayısıyla iki alan da yanıta ve ekrana giriyor — bekleyen kalem yok,
`ProducesContractTests.Pending`'e bir şey eklenmiyor.

Ekranda muafiyet **gizlenmiyor ve küçültülmüyor**: `false` hâli de yazılıyor.
Yalnızca `true` iken görünen bir rozet, muafiyetsiz koşumu *"bu soru
sorulmamış"* hâline sokardı — T38'in "gizlenen sıfır" kararıyla aynı gerekçe.

## Bekçiler

§6: her biri kırmızı yanabildiği ölçülerek teslim edilir.

| Bekçi | Ne kanıtlar | Konteyner? |
| --- | --- | --- |
| `Muafiyetli_kosum_raporunda_muafiyet_gorunuyor` | Asıl iddia | ❌ |
| `Muafiyetsiz_kosum_da_bunu_soyluyor` | `false` hâli gizlenmiyor | ❌ |
| `Muafiyet_gerekcesi_kayitta_birebir` | Gerekçe kısaltılmıyor/boşaltılmıyor | ❌ |
| `Rapor_alan_adlari_AuditFields_ile_ayni` | İki gösterim ayrışmıyor | ❌ |

## Kapsam dışında

- **Muafiyet politikasını değiştirmek.** Muafiyetin *ne zaman* verilebileceği
ayrı bir karar; bu ticket yalnızca *"verildiyse görünüyor mu"*.
- `RcaRunEntity`'ye kolon eklemek. Kayıt zaten `RcaReportEntity`'de belge
olarak duruyor; ikinci bir yer §9'un yasakladığı kopya olurdu — ve ayrı bir
göç, T44'ün birleşmemiş `AddRcaReports`'uyla **göç zincirini** bozardı.

## Sahiplik — koordinatörün kararı bekleniyor

Değişikliğin tamamı T44'ün **birleşmemiş** yüzeyinde: bir record'a iki alan,
bir çağrı satırı, yanıt tipi ve ekran. T49 dalından yazmak §9'un *"aynı satırı
iki ajana yazdırma"* maddesini ihlal ederdi ve `ControlPlaneDbContextModelSnapshot`
üzerinde çakışırdı.

İki yol var:

1. **T44 kendi dalında kapatır** — en ucuzu; alanlar zaten elinde ve tek çağrı
noktası onun.
2. **T44 birleştikten sonra ayrı bir tur** — bedeli: arada üretilen raporlar
alanı taşımaz ve altın kümenin o kısmı bu boyutta kör kalır. T38'in "çelişen
kanıt alanı bugün açılıyor" kararının aynı gerekçesi.

**Birinci yol öneriliyor.** İkincisi seçilirse gerekçesi buraya yazılmalı.
