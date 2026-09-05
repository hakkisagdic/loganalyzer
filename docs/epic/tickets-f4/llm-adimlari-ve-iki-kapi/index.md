---
kind: ticket
title: "T44 — LLM adımları ve ikinci kapı"
status: 2
---

# T44 — LLM adımları ve iki kapı

F4'ün mekanizma kolunun (T42 → T43 → T44) son halkası. T43 **birinci** kapıyı
kurdu (kısıt doğrulama: adım reddedilir, bir tekrar); T44 **ikincisini** kuruyor
(cümle atılır, adım kabul edilir, **sayaç artar**) ve ikisini bir adım
makinesinde birleştiriyor.

Yöneten kararlar: [Karar 1 · RCA §2](../../rca-raporu-ozelligi/index.md) ·
[Karar 4 · plugin formatı §6](../../f4-plugin-format-karari/index.md) ·
[T41'in şartı](../prompt-redaksiyon-tabani/index.md).

---

## 1 · İki kapı, iki sonuç — ve neden ayrı testleri var

| | Kısıt doğrulama (kapı 1) | Cümle bağlama (kapı 2) |
| --- | --- | --- |
| Neye bakıyor | Çıktı **belgesinin yapısı**: `evidence_ids[]` alanları | Serbest metnin **cümleleri** |
| İhlalde | **Adım** reddedilir, bir kez yeniden denenir | **Cümle** atılır, adım kabul edilir |
| Sayaç | — | Karar 1'in atılan cümle sayacı |
| Kodda | `EvidenceIdsMustExistConstraint` | `SentenceBinder` |

İkisi tek kapıda birleştirilseydi bir kötü cümle yüzünden bütün adım atılırdı —
iki iyi hipotez de kaybolurdu. Tersi de kötü: kısıt ihlalini cümle atarak
geçiştirmek, var olmayan bir `evidence_id`'yi rapordan silip raporu **geçerli
göstermek** olurdu.

### Atmak ile saymak ayrı iddialar — ve ölçüldü

Karar 1 iki şey birden söylüyor: *"referanssız cümle rapora hiç girmiyor"* **ve**
*"atıldığı sayılıyor ve gösteriliyor"*. Tek testte ölçülselerdi, sayacı silen bir
değişiklik atma testini hâlâ geçerdi.

Ölçüm bunu doğruladı (§4, ölçüm A ve B): **A** kusuru (atma kaldırıldı) atma
testini kırmızıya çevirdi, sayaç testi **yeşil kaldı**; **B** kusuru (sayaç
susturuldu) tam tersini yaptı. İki iddia birbirinden gerçekten bağımsız.

### Cümlenin bağlanma ölçütü — ve atıf sözdiziminin TAHMİN EDİLMEMESİ

Bir cümle, **adımın gördüğü** bir kimliği metninde geçiriyorsa bağlanmış
sayılıyor. Ölçüt kimliğin *şeklini* değil **kümesini** kullanıyor.

`EV-\d+` gibi bir desen yazmak `EvidenceItem.Id`'nin biçimini varsaymak olurdu ve
öyle bir söz hiçbir yerde verilmedi — sağlayıcı ne üretirse o. Kümeyle
eşleştirmek varsayımı tümden kaldırıyor. Yan kazanç: köşeli parantez içinde geçip
kümeye **çözülmeyen** şeyler ayrıca sayılıyor
(`FabricatedCitationSentenceCount`), yani *"hiç atıf yapmadı"* ile *"atıf
uydurdu"* T47 için ayrı duruyor.

---

## 2 · Doğrulama adımın GÖRDÜĞÜ kanıta karşı

Bu ticket'ın taşıyıcı kararı ve `StepEvidenceView`'ın varlık sebebi.

Görüş alanı `input`'tan türüyor ve **ileri doğru daralarak** ilerliyor:

| `input` | Ne açıyor |
| --- | --- |
| `evidence.items` | Paketin bütün kimlikleri |
| `evidence.summary` | **Hiç kimlik yok** — özet bir kanıt *öğesi* değil (plugin formatı §3.2) |
| `steps.<id>` | O adımın **bağladığı** kimlikler — paketin tamamı değil |
| tanınmayan `evidence.*` | Kimlik yok, ve **adıyla** `UnknownInputs`'ta duruyor |

RCA senaryosunda zincir şöyle daralıyor: `bind-evidence` paketin üçünü de görüyor
ama yalnızca `EV-01`'i bağlıyorsa, `write-actions` **yalnızca `EV-01`'i**
görüyor. `EV-02` pakette **var** ama bu adım için yok.

Paketin tamamına karşı doğrulansaydı bir adım hiç görmediği bir kanıta atıf yapıp
geçerdi: **kimlik doğru, gerekçe uydurma**. Ve kapı yeşil yanarken hiçbir şey
söylemezdi. Ölçüldü (§4, ölçüm C).

Son satır bilerek yazıldı: tanınmayan bir `evidence.*` yoluna kimlik
**vermemek** yetmiyor, vermediğini **söylemek** gerekiyor — yoksa "kimlik
vermedim" ile "böyle bir yol yok" ayırt edilemiyor. Aynı ayrım prompt'a da
giriyor, model olmayan bir listeye atıf yapmasın diye.

---

## 3 · T43'ün bıraktığı üç açık uç — kararlar

### 3.1 · Kısıt adlarını kim tanıyacak

**Karar: yükleme anında küme AÇIK, koşum anında KAPALI.**

| | Yükleme anı | Koşum anı |
| --- | --- | --- |
| Kim | Yükleyici | Motor (`ScenarioConstraintGate`) |
| Bilinmeyen ada ne oluyor | **Geçiyor** — uzantı noktası korunuyor | **Koşum hiç başlamıyor** |

Format kararının (§5.2) gerekçesi duruyor: kaç kısıt olacağını kimse bilmiyor,
küme kapatılırsa her yeni senaryo **çekirdeği** değiştirir. Ama üçüncü hâl —
*"tanımadım ama geçtim"* — bu deponun `Produces<T>` ile ödediği hâlin kendisi
olurdu: kapı üç uç dosyasını hiç görmedi ve **üç test de yeşildi**.

Sonuç: yeni bir kısıt adı yazmak senaryoyu **yüklenebilir** yapıyor ama
**koşulabilir** yapmıyor, ve ayrışma **ilk koşumda** görünüyor — raporun içinde
değil.

Bölme §6.1'in bölmesinin aynısı ve tesadüf değil: çekirdek **zarfı** (ad bir
dizgi mi), motor **içeriği** (o adı zorlayabiliyor muyum) biliyor.

**Ön uçuş, koşum başlamadan.** Üçüncü adımda çıkan bir *"bu kısıdı tanımıyorum"*,
ilk iki adımın belirteçleri ödendikten sonra çıkar — kota harcanır, rapor
üretilmez.

`pattern_must_compile` **uygulanmadı**: format kararının parser kalite taslağında
doğdu, bugün onu yazan bir senaryo yok, ve *tüketicisi olmayan bir tip
tahmindir* (§8). Yazan bir senaryo bugün **adıyla** reddediliyor.

### 3.2 · `write-actions` muafiyeti

**Karar: muafiyet DÜŞTÜ — aksiyon kendi kanıt atfını taşıyor.**
`ExpectedWaivedCount` 2 → 1.

Eski muafiyetin gerekçesi kendi metninde *"DEVRALINMIŞ"* diye yazılıydı ve
düşürücü olan da bu oldu: `bind-evidence` **kendi** yazdığı kimlikleri doğruladı,
bu adımın yazacaklarını değil. Devralınmış bir gerekçe gerekçe değil.

Üç dayanak:

1. **RCA §4.2 zaten `recommended_actions[] = {text, risk, evidence_ids[]}`
   diyor.** Alan tasarımda vardı; muafiyet onu doğrulanmadan bırakıyordu.
2. **Kapı artık dar.** §2'nin görüş alanı sayesinde doğrulama paketin tamamına
   değil, aksiyonun gerçekten dayanabileceği kümeye karşı yapılıyor — yani
   kapatmanın maliyeti yok.
3. `constraints: []` ile geçen bir adım, kapının uygulandığı bir adımdan ayırt
   edilemeyecek biçimde "geçti" görünüyordu — §3.2'nin kapatmak istediği
   sessizliğin kendisi.

`rank-hypotheses`'in muafiyeti **kaldı** ve gerekçesi **mekanik hâle geldi**:
girdisi `evidence.summary`, görüş alanı **boş**, dolayısıyla
`evidence_ids_must_exist` zorlayacak bir şey bulamazdı — yazılı ama iş yapmayan
bir kapı olurdu.

Düşüş de **iki bilinçli hareket**: senaryo dosyası **ve** `ExpectedWaivedCount`.
Ekleme kadar silme de.

### 3.3 · `output.schema` adları

**Karar: tanım motorda, ve küme koşum anında kapalı — kısıt adlarıyla aynı
mekanizma.**

Şemayı **ayrıştıran** ve iki kapıya *neye bakacağını* söyleyen taraf motor. Adı
bir dizgi olarak bırakmak, motorun ayrıştıramadığı bir çıktıyı "sözleşmeye uydu"
saymak olurdu.

Bir şema iki şeyi bildiriyor: hangi alanlar **kimlik** (kapı 1'in hedefi), hangi
alanlar **düzyazı** (kapı 2'nin hedefi). Üçüncü bir bildirim daha var ve o
olmasaydı rapor boş çıkardı:

| Şema | Düzyazısı rapora giriyor mu | Neden |
| --- | --- | --- |
| `hypothesis_list` | **Hayır** | Ara çıktı; görüş alanı boş, her cümle atılırdı |
| `evidence_binding` | Evet | Bulguların gövdesi |
| `action_list` | Evet | Aksiyonların gövdesi |

**Kapının koşmadığı yer yazılı** (`SentenceGateSkipped`, raporun Markdown'ında da
bir satır): sessizce atlayan bir kapı, kapının kendisinden tehlikeli.

Üçü de tek bir `JsonListSchema` ailesiyle uygulandı — üç ayrı sınıf, aynı
ayrıştırma hatasını üç kez yapabilmek demekti. Aile dar: kök bir nesne, içinde
bir dizi, öğeleri düz nesneler. Daha derinini isteyen bir tüketici yok.

---

## 4 · Ölçülen kırmızılar

Yordam §6'nın üç adımı, **iddia adımı dahil**: kusuru yaz → dosyayı **oku ve
kusurun orada olduğunu iddia et** → koştur → geri al. Araç:
`tools/t44-kirmizi-olcumu.py`.

Her ölçümün bir de **kontrol testi** var: kusurdan etkilenmemesi gereken bir
test. Yalnızca "kırmızı yandı" ölçülseydi, her şeyi düşüren bir kusur da aynı
çıktıyı verirdi — kapının **neyi** tuttuğu değil, yalnızca tuttuğu ölçülmüş
olurdu.

| # | Kusur | Hedef test | Kontrol testi |
| --- | --- | --- | --- |
| A | Bağlanmayan cümle rapora giriyor | `Referanssiz_cumle_rapora_girmiyor` **KIRMIZI** | `Atilan_cumle_sayiliyor` **yeşil** |
| B | `Dropped => 0` | `Atilan_cumle_sayiliyor` **KIRMIZI** | `Referanssiz_cumle_rapora_girmiyor` **yeşil** |
| C | Görüş alanı paketin tamamı | `Adimin_gormedigi_kanita_atif_reddediliyor` **KIRMIZI** | `Adimin_gordugu_kanita_atif_geciyor` **yeşil** |
| D | `ScenarioStepPrompt` yapıcısı `public` | `Prompt_tipini_yalnizca_kurucu_uretebiliyor` **KIRMIZI** | `Kanit_metnindeki_sir_prompta_girmiyor` **yeşil** |

Dördünde de kusur, koşumdan önce dosyadan **okunarak** doğrulandı. Ölçüm aracı
ayrıca *"0 test koştu"* hâlini yeşilden ayırıyor: filtre eşleşmemesi bir kapı
geçişi değil, kapının hiç sorulmaması.

### Ölçüm aracının kendi sessiz yanlışı — ve nasıl yakalandı

Ölçümden sonra tam paket koşturuldu ve `Prompt_tipini_yalnizca_kurucu_uretebiliyor`
**hâlâ kırmızıydı** — oysa `grep` kaynakta hiçbir `KIRMIZI-` işareti bulmuyordu.

Sebep araçtaydı: geri alma `shutil.copy2` kullanıyordu ve o **zaman damgasını da
geri yüklüyor**. Düzeltilmiş dosya derlenmiş DLL'den *eski* göründü, MSBuild
projeyi "güncel" sayıp atladı, ve `dotnet build` **0 hata** derken ikili hâlâ
kusurluydu. Kaynak temiz, ikili kusurlu.

| | Kaynak | İkili | Ne görünüyordu |
| --- | --- | --- | --- |
| `grep KIRMIZI-` | temiz | — | "geri alındı" |
| `dotnet build` | — | kusurlu, atlandı | "0 hata, 0 uyarı" |
| `dotnet test` | — | kusurlu | **kırmızı** |

Bu §6'nın adını koyduğu sınıfın **tersten** hâli: orada yeşil bir sonuç *"ölçüm
yapılmadı"* anlamına gelebiliyordu; burada kırmızı bir sonuç *"geri alma
görülmedi"* anlamına geliyor. İkisi de ölçüm aracının kendi hatası, ve ikisi de
sonucu doğru sanmaya yol açıyor.

Yakalayan şey bir bekçi değil, **ölçümden sonra tam paketi bir kez daha
koşturmak** oldu. Araç düzeltildi (`copy` + `touch`), ama asıl ders yordamda:
kusur enjeksiyonundan sonra "geri aldım" demek yetmiyor — geri almanın
**derlemeye ulaştığı** ayrıca görülmeli.

### Bulunan bir vakum: bekçi boş küme üzerinde yeşil yanıyordu

`RcaReportStatusGuardTests.Rca_belge_varliklari_statu_benzeri_kolon_tasimiyor`
(T46) `RcaReport` ile başlayan tipleri `Bizigo.ControlPlane`'de arıyordu.
**O derlemede öyle bir tip yoktu** — döngü boş küme üzerinde dönüyor, hiçbir
iddia koşmuyor, test yeşil yanıyordu.

Bekçi doğru soruyu soruyordu ama sorduğu yerde soracak kimse yoktu. T44 belgenin
ilk gerçek tipini (`RcaReportDocument`) getirdi; derleme listesi genişletildi ve
`Bekci_bos_kume_uzerinde_yesil_yanmiyor` boşluğun geri dönmesini engelliyor.

---

## 5 · Prompt kurucusu — zincirin üçüncü halkası

`ScenarioStepPrompt`'un yapıcısı `private`, tek üreticisi kendi iki fabrikası, ve
o fabrikaların **hiçbir parametresi prompt metni değil**: adım YAML'dan, görüş
alanı kanıt paketinden, şema motordan geliyor.

| Halka | Ne yaptı |
| --- | --- |
| T41 | Redaksiyon kapısını `RedactedPrompt` **tipine** bağladı |
| T42 | `ModelRequest` ve `IModelProvider`'ı o tipten girdi almaya zorladı |
| **T44** | Prompt'u **kuran** tarafı aynı çizgiye çekti |

Üçü birlikte: kanıt metnini modele taşıyan bir yol **ancak** kapıdan geçerek
yazılabiliyor. Alternatifi kurucunun `string` alması ya da döndürmesiydi — o
zaman kapı bir **çağrı alışkanlığı** olurdu ve unutulduğu gün hiçbir şey
kırılmazdı.

Yeniden deneme prompt'u da kapıdan geçiyor: ihlal listesi model çıktısından
türüyor ve onu kapısız geri göndermek tabanın yarısını açık bırakmak olurdu.

İki ayrı test: yansıma (yüzey doğru tipte mi) **ve** davranış (taban gerçekten
koşuyor mu). Bir yüzey doğru tipte olup içeride kapıyı hiç çağırmayabilir.

---

## 6 · Belirteç muhasebesi — sıfırın iki hâli

T42'nin 8 numaralı ölçümü: **bildirilmemiş belirteç `null`, sıfır değil.** Sıfır
yazılsaydı bütçe hiç tükenmez, kapı hiç kapanmaz, sebep hiç görünmezdi.

T44'te ikinci bir tuzak çıktı ve o toplama ait: **kısmi bir toplam bir alt
sınırdır.** İki adımın biri bildirmediğinde toplam *tam* sanılır — sıfır
yazmanın daha sinsi hâli, çünkü sayı makul görünüyor.

Karşılığı üç alan: `PromptTokens` (hiç bildirim yoksa `null`),
`UnreportedAttempts`, ve soruyu tek satırda cevaplayan `TokensComplete`.
Bütçeyi uygulayan taraf (T46) `TokensComplete == false` gördüğünde toplamı
**"bilinmiyor"** saymalı, **"küçük"** değil.

Kısıtın kendisi burada uygulanmıyor — bütçe T46'nın. Buradaki taahhüt yalnızca
ölçülen sayıyı **bildirmek**.

**Bir adım en fazla iki kez koşuyor** (`MaxAttemptsPerStep = 2`) ve sabit
olmasının sebebi kota: tavan senaryodan okunsaydı bir plugin kendi belirteç
bütçesini yazabilirdi ve *"kota kuyrukta uygulanır, senaryonun insafına
bırakılmaz"* (RCA §5) cümlesi delinirdi.

---

## 7 · Sınır: `rca_report` statü taşımıyor

RCA §4.2'nin 2026-08-26 düzeltmesi çivilendi ve T44 onun **kapsamına girdi**:

- `rca_runs` → koşumun başına gelen her şey (kabul, ret, yürütme, sonuç)
- `RcaReportDocument` → üretilen belge (metin, cümle atfı, **atılan cümle
  sayısı**)

Belgeye bir `Status`/`State` özelliği eklenirse `RcaReportStatusGuardTests`
kırmızı yanıyor — artık gerçekten (§4).

---

## 8 · Yapılmayanlar

- **Kalıcılık yok.** `RcaReportDocument` bellekte duruyor; `rca_report` tablosu,
  EF varlığı ve göçü **yazılmadı**. Gerekçe kapsam: T44 mekanizma kolunun son
  halkası, kalıcılık koşum kolunun yüzeyi. Karar 1'in *"gösteriliyor"* yarısı
  bugün **belge düzeyinde** karşılanıyor (`DescribeDroppedSentences`,
  `ToMarkdown`); API yanıtına ve ekrana taşınması T47/UI'ın.
- **`pattern_must_compile` uygulanmadı** — §3.1, tüketicisi yok.
- **Gerçek modelle hiç koşulmadı.** Bütün testler sahte sağlayıcıyla; ölçülen
  şey motorun kapıları, modelin kalitesi değil. Kalite T47'nin ve altın kümenin.
- **Prompt'un gerçekten işe yaradığı ölçülmedi.** Şekil tarifi ve kural
  cümleleri seçildi, sınanmadı; hangi düzeyin (`summary`/`masked`/`raw`) daha az
  cümle attırdığı T47'nin ölçümü.
- **`AddBizigoScenarioPlugins` üretim grafiğine bağlanmadı** — aşağıda.
- **Entegrasyon testi yazılmadı.** Bu ticket'ın hiçbir kapısı konteyner
  istemiyor; birim paketinde tamamı ölçülüyor.

---

## 9 · Tereddütler ve kesişmeler

1. **`AddBizigoScenarioPlugins` `Program.cs`'te hiç çağrılmıyor.** T43'ten beri
   yazılı ama `Bizigo.ScenarioPlugin` kompozisyon kökünün geçişli kapanışında
   değildi; T44'ün `Bizigo.Rca → Bizigo.ScenarioPlugin` referansı onu kapanışa
   soktu ve `ArchitectureTests`'in keşfi bir fazla bularak kırmızı yandı.

   Yani **senaryo kataloğu üretim DI grafiğine bağlı değil.** Bekçiyi ömür
   doğrulamasına soktum ve beklenen listeye ekledim — ama *bağlamak* koşum
   kolunun işi. Listedeki satır okuyana "bağlanmış" gibi görünme riski taşıyor,
   o yüzden yorumda da yazılı.

2. **`Bizigo.Rca.csproj`'a bir proje referansı eklendi** ve o proje T45/T46'nın
   alanı. Dosyanın şekli değişmedi, tek satır eklendi; koordinatöre bildirildi.

3. **Cümle bölme noktalama tabanlı.** Kısaltma ve ondalık sayı yanlış
   bölünebiliyor. Bedeli bir cümlenin ikiye ayrılması — sessiz bir geçiş değil,
   iki parça da aynı atıfı taşıyorsa ikisi de bağlanıyor. Ölçülmedi.

4. **Atıfsız bir cümlenin bir öncekinden bağlam devralması tanınmıyor.**
   Bilerek: devralma kabul edilseydi tek atıflı bir paragrafın tamamı bağlanmış
   sayılırdı ve kapı ilk cümleden sonra hiçbir şey ölçmezdi. Ama bu, insan gibi
   yazan bir modelde atılan cümle oranını **yukarı** çekebilir — T47'nin
   ölçeceği ilk şeylerden biri, ve eşik değil **prompt** ayarı olabilir.

5. **Kapı atfın yerindeliğini ölçmüyor, varlığını ölçüyor.** Doğru kimliğe atıf
   yapan ama o kimlikle ilgisiz bir cümle bağlanmış sayılıyor. Bu bilinen bir
   sınır ve tam da RCA riski #5'in (çelişen kanıt tiyatrosu) yaşadığı yer;
   ölçümü altın kümenin.

---

## 10 · İlk bakılacak yer

> **İlk bakılacak yer:** `StepEvidenceView.For` — görüş alanının daralması
> buradan çıkıyor ve iki kapının da davranışı ona bağlı. Bir raporun beklenenden
> boş çıkması ya da bir adımın beklenmedik reddi neredeyse her zaman burada
> başlıyor.
>
> **Aradım ve elemedim:** `SentenceBinder`'ın bölme deseni (kırmızı ölçümü A ile
> ayrı sınandı, davranışı beklendiği gibi); `JsonListSchema`'nın ``` çit
> sökmesi (ayrıştırma testiyle kapsandı); belirteç toplamı (üç ayrı testle,
> `null` · tam · kısmi).
>
> **Aramadım:** senaryo kataloğunun üretim DI'ye bağlanma yolu (§9.1) —
> bekçinin kırmızısını gördüm, sebebini buldum, ama bağlamayı **denemedim**;
> `IScenarioEvidenceSchema`'nın koşum anı doğrulaması (`ScenarioEvidenceGate`)
> ile ön uçuşumun sırası — ikisi bugün birbirini çağırmıyor ve çağırmalı mı
> bakmadım; `evidence.summary` dışındaki `evidence.*` yollarının gerçekte hangi
> sağlayıcıdan geleceği.
