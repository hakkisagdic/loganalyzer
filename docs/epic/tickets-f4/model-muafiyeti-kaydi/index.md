---
title: "T54 — Sessiz muafiyet: model sınırı muafiyeti koşum kaydına bağlı değil"
kind: ticket
status: 2
---

# T54 — Muafiyet yapılandırmada var, raporda yok

**Bağımlılık:** T42 (alan), T44 (rapor yarısı — **kapandı**, `d718ca7`) · **Sonraki:** —

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

### Ölçülen hâl — `RcaModelBoundaryRecordTests`, 12 kapı

Beş kusur, beş kırmızı, beşinde de kontrol yeşil
(`tools/t54-kirmizi-olcumu.py`). Üçü yazma/tel tarafında, ikisi tip tarafında.

| Kusur | Kırmızı yanan | Yeşil kalan |
| --- | --- | --- |
| Tel gerekçeyi `string.Empty`'ye çeviriyor | `Null_ile_bos_dize_…`, `Muafiyetsiz_kosum_da_bunu_soyluyor` | `Damga_gerekceyi_ucun_kendisinden_aliyor` |
| Sınır hâli telden düşüyor (sabit dizge) | `Muaf_kosum_muafiyetsizinden_…`, `Dort_hal_telde_ayri_gorunuyor` | `Damga_…` |
| Damga satıra hiç yazılmıyor | `TryStart_damgayi_satira_yaziyor` | `Muafiyet_gerekcesi_telde_birebir` |
| Damga tipine `public` yapıcı açılıyor | `Damganin_gecersiz_hali_tip_duzeyinde_ulasilamaz` | `Damga_…` |
| Damga parametresi isteğe bağlı oluyor | `Damganin_gecersiz_hali_…` | `TryStart_damgayi_satira_yaziyor` |

### Ölçüm iki bekçiyi ve bir beklentiyi düzeltti — üçü de bu ajanın hatası

**1 · İki tip kusuru derlemeyi kırmıyor.** İlk tasarımda ikisi de
`derleme_kirilmali=True` yazılıydı. Ölçüldü: damga parametresini isteğe bağlı
yapmak bugünkü tek çağıranı hiçbir şey değiştirmeye zorlamıyor, yani her şey
derleniyor ve **bütün davranış testleri yeşil kalıyor**. O hâlde ölçüm hiçbir şey
ölçmeyecek ve yeşil sonuç *"kapı sağlam"* diye okunacaktı. Tipin şekli
hakkındaki bir iddia tipin şekline bakarak sınanmalı — kapı bir **yansıma**
iddiasına çevrildi.

**2 · Bir "kontrol" kontrol değildi.** `Muafiyet_gerekcesi_telde_birebir`
sınır anahtarını da okuyor, dolayısıyla tel kusurundan etkilenmesi **doğru**.
Kontrol, tele hiç bakmayan tip düzeyi kapısına taşındı.

**3 · Ve asıl olan: bir bekçi hiçbir şey ölçmüyordu.**
`Muaf_kosum_muafiyetsizinden_ayirt_edilebiliyor` kusur altında **iki kez** yeşil
kaldı. İlk teşhis *"iki koşum gerekçe alanından da ayrılıyor"* idi ve yalnızca
sınırda ayrılan bir çift eklendi — **yetmedi**. Asıl sebep fixture'daydı:

| Alan | Varsayılan | Sonuç |
| --- | --- | --- |
| `RcaRunEntity.Id` | `Guid.NewGuid()` | her çağrıda farklı |
| `RcaRunEntity.RequestedAt` | `DateTimeOffset.UtcNow` | her çağrıda farklı |

Yani karşılaştırma sınırları değil **kimlikleri** karşılaştırıyordu ve
`Assert.NotEqual` **her zaman** geçiyordu. Fixture sabitlendi, ve testin başına
*iddianın boş olmadığını* gösteren bir taban karşılaştırması eklendi (sınır
dışında iki koşum birebir aynı).

Ders T44'ün `copy2` tuzağıyla aynı aile — yeşil bir sonuç *"kusur etkisiz"*
değil *"ölçüm hiç yapılmadı"* anlamına da geliyor. Farkı: orada ölçüm aracı
yalancıydı, burada **bekçinin fixture'ı**. Ve yakalayan şey bir bekçi değil,
kusuru enjekte edip kırmızı **beklemek** oldu.

### T61'in bekçisi bu turda kendi işini yaptı

`status: 0 → 2` yazıldığı an `EpicStatusTests.Rapor_acik_dedigi_her_kalem_gercekten_acik`
kırmızı yandı: `kalan-is-raporu` T54'ü hâlâ açık sayıyordu. Rapor düzeltildi
(F4 *"iki kalem"* → *"bir kalem"*, tablo `7/9 · T47 sürüyor, T54 açık` →
`8/9 · T47 sürüyor`). Dün kurulan kapının bugün tuttuğu ilk gerçek ayrışma.

## Kapsam dışında

- **Muafiyet politikasını değiştirmek.** Muafiyetin *ne zaman* verilebileceği
ayrı bir karar; bu ticket yalnızca *"verildiyse görünüyor mu"*.
- **`ModelBoundaryGate.VerifyAsync`'i üretimde çağırır hâle getirmek.** Modeli
çağıran bir üretim yolu yokken doğrulanan uç, tüketicisi olmayan bir karar
olurdu (§8); ve verdict'in reddinin bir koşumu durdurup durdurmayacağı T47'nin
kolunda. Kalem olarak bildirildi ve koordinatör kaydetti.

### ~~`RcaRunEntity`'ye kolon eklemek~~ — **öncül ölçümle düştü (2026-09-15)**

Bu bölüm şöyle yazılıydı:

> `RcaRunEntity`'ye kolon eklemek. **Kayıt zaten `RcaReportEntity`'de belge
> olarak duruyor**; ikinci bir yer §9'un yasakladığı kopya olurdu — ve ayrı bir
> göç, T44'ün birleşmemiş `AddRcaReports`'uyla göç zincirini bozardı.

İkinci gerekçe zamanla çözüldü (`AddRcaReports` birleşti). **Birinci gerekçe
ise ölçüldüğünde düştü: belgeyi hiçbir üretim kodu yazmıyor.**

| Öncül | Bugün | Ölçen komut |
| --- | --- | --- |
| "Kayıt zaten belge olarak duruyor" | `RcaReportStore.SaveAsync`'in **üretimde çağıranı yok** | `grep -rn 'RcaReportStore' --include=*.cs src/` → yalnızca kayıt, okuma ve yorumlar |
| Belgeyi üreten koşucu bağlı | `ScenarioStepRunner` hiçbir yerde `new`'lenmiyor | `grep -rn 'ScenarioStepRunner' --include=*.cs src/` → tanım + yapıcı |
| Sınır kararı üretimde veriliyor | `ModelBoundaryGate.VerifyAsync`'in 14 çağrısı, **hepsi `tests/`** | `grep -rn 'VerifyAsync' --include=*.cs src/ tests/` |

Koordinatör üç iddiayı bağımsız olarak doğruladı. `src/` içinde görünen tek
`VerifyAsync` isabeti bir `<see cref="…">` **belge yorumu** — çağrı değil.

Yani "zaten duruyor" dediği yer **boş**, ve `rca_runs`'a kolon eklemek bir
kopya değil **tek gerçek kayıt**. Kapsam buna göre düzeltildi ve uygulandı;
ayrıntısı aşağıda.

## Uygulanan — koşum kaydı (2026-09-15)

Rapor yarısı `d718ca7`'de kapandı (`RcaReportModelInfo`'nun iki alanı, tel,
ekran, Markdown). Bu tur **koşum kaydını** bağladı.

### Kapalı küme, dört değer

```csharp
public enum RcaModelBoundary
{
    Unspecified = 0,   // koşum başlamadı ya da hiç başlamayacak (reddedildi)
    NotEngaged  = 1,   // koştu, modele hiç konuşmadı  ← bugün üretimdeki TEK değer
    Verified    = 2,   // uç adres sınıfına karşı doğrulandı, muafiyet yok
    Overridden  = 3,   // muafiyet uygulandı — gerekçe DOLU
}
```

`bool` yerine kapalı küme, çünkü bir `bool` *"muafiyet yok"* ile *"kimse
bakmadı"*yı aynı bayta indirirdi ve ikisi taban tabana zıt: biri güvence, öteki
ölçümsüzlük.

**`Unspecified` ile `NotEngaged` ayrı** ve gerekçesi mekanik: satır
`AdmitAsync` ile doğuyor, damga `TryStartAsync`'te basılıyor, reddedilen koşum
hiç başlamıyor. Birleştirilseydi reddedilen bir koşum *"modele konuşmadı"* diye
okunurdu — doğru bir cümle, ölçülmemiş bir yerden söylenmiş.

**`Unspecified = 0` bilinçli:** göç mevcut satırlara `0` yazıyor ve bu doğru —
geçmiş koşumların sınırı hakkında kimse bir şey söylemedi. Varsayılan
`Verified` olsaydı veritabanının kendisi, hiç doğrulanmamış bir uç hakkında
güvence beyan ederdi.

### Damga: geçersiz hâl var olamıyor

`RcaModelBoundaryStamp` — yapıcı `private`, iki fabrika: `NotEngaged()` ve
`From(ModelEndpoint)`. Gerekçe **ucun kendisinden** okunuyor, çağırandan
alınmıyor: alınsaydı çağıran `Overridden` deyip başka bir metin geçirebilirdi ve
kayıt, ucun gerçekten hangi gerekçeyle açıldığından ayrışabilirdi.

`TryStartAsync(runId, stamp, ct)` — **parametre zorunlu, varsayılanı yok.**
`= null` verilseydi model yolu bağlandığı gün çağıran hiçbir şey değiştirmeden
derlenir ve kayıt sessizce `Unspecified` kalırdı. Kalıp T41'in
`RedactedPrompt`'u ve M06'nın `McpBoundaryDeclaration.Basis`'i.

### Ölçümün kapsamı — bu paragraf olmadan kapı varmış gibi okunur

`Overridden`'ın **gerekçesiz var olamayacağı tip düzeyinde ölçüldü** ve koşum
gerektirmiyor. Bir **üretim** koşumunun `Overridden` ya da `Verified`
damgalandığı **ölçülmedi**: modeli çağıran üretim yolu yok, dolayısıyla
üretimdeki tek değer `NotEngaged`. İkisi ayrı iddia ve ikincisi bu ticket'ın
kapsamı dışında.

### Yanında bulunan boşluk

`TryStartAsync`'in bu değişiklikten önce **hiçbir testte çağıranı yoktu** —
birim ve entegrasyon paketlerinin tamamında sıfır isabet. Yani `Queued →
Running` geçişi ve `StartedAt` damgası da sınanmamıştı.
`TryStart_damgayi_satira_yaziyor` artık yazma yolunu da tutuyor.

### Göç — okundu

`20260915082750_AddRcaModelBoundary`: `rca_runs`'a **iki kolon ekliyor**,
başka hiçbir tabloya ve hiçbir mevcut kolona **dokunmuyor**, veri taşımıyor,
yeniden adlandırma yapmıyor. `Down` ikisini düşürüyor. Bu, bu depodaki en pahalı
**önlenmiş** hatanın (`enabled → status`, pasif kuralları sessizce açan göç)
tam karşıtı: eklenen varsayılan bir güvence beyan etmiyor, **yokluğu** beyan
ediyor. **Uygulanmadı** — faz sonu koşumunda koordinatör uygulayacak.

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
