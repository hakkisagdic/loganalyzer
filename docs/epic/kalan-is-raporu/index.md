---
kind: spec
title: "Kalan iş — uygulamanın tamamı için ne var (2026-09-05)"
---

# Kalan iş raporu

Bugünkü ölçülmüş hâl: derleme **0 uyarı 0 hata** · birim **1247 geçti /
4 atlandı / 0 düştü** · UI **499 geçti / 33 dosya** · `api:check` **birebir**.

`main` = `65ad1df`, **uzağa push edilmedi**. Entegrasyon testleri ve compose
kapısı bu turda **koşmadı** — Docker daemon bu makinede ölü (bkz. §3).

> **Bu belgenin bir bekçisi var artık.** `EpicStatusTests` aşağıdaki "açık"
> listesini ticket dosyalarının `status` alanıyla karşılaştırıyor ve
> çeliştiğinde birim paketi kırmızı yanıyor. Kendi merge turunda **dört kez**
> konuştu. Sebebi §6'da yazılıydı ve buraya bir bekçi bağlanana kadar aynı
> ayrışma üç turda üç kez döndü — sonuncusunda T38'in brief'i yanlış öncülle
> başladı.

---

## 1 · Faz faz durum

| Faz | Ne | Durum |
| --- | --- | --- |
| **F1** — boru hattı | Ingest, depolama, parser motoru, kimlik, API | **14/14 kapandı** |
| **F2** — arayüz | Ekranlar, BFF, alarm, change | 16/17 · **T49 sürüyor** (canlı doğrulaması koordinatörde) |
| **F3** — detection ve kanıt | Sigma, korelasyonlar, kanıt paketi, **üç kapı ticket'ı** | 13/14 · **T32 sürüyor** |
| **F4** — agentic RCA | Prompt tabanı, plugin, tetikleyici, kota, LLM, rapor | 8/9 · **T47 sürüyor** |
| **F5** — gözlemlenebilirlik | Metrik · trace · topoloji sağlayıcıları | **kapsam kararı verildi (S1)**: topoloji karşılandı, metrik ve trace **kalıcı muaf** |
| **FS** — simülatörler | Cihazsız uçtan uca koşum | **8/8 kapandı** |
| **MCP** — protokol | İki yüzey, sekiz ticket | **M01 koşuyor**, M02–M08 açık |

Bu turda kapananlar: **T38 · T44 · T48 · T50 · T51 · T53 · S06 · S07 · S08**.
`status: 1` bırakılanlar bilerek öyle — T32 ve T47 kendi ticket'larının canlı
yarısını koşturamadı, T49'un kabul kriterlerinden üçü Docker istiyor. Hiçbiri
"ölçülmedi ama kapandı" sayılmadı.

---

## 2 · Açık ticket'lar — on beş kalem

### F3 — bir kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T32** | Derleme hattı ve SQL versiyonlama | Kod ve kapılar bitti; açık olan **duvar saati ölçümü** — kural başına derleme maliyeti sessiz makine istiyor ve o koşum koordinatörde. Ölçekleme sorusu saatsiz cevaplandı ve karesel bir kusur buldu |

**T38 ve T48 bu turda kapandı.** T38'in kodu zaten yazılmıştı; eksik olan
altıncı bekçiydi (altın kümede kapsam, gerçek SQL'e karşı). T48 kapının
**ikinci** elle listesini kaldırdı ve yanında iki kalem daha doğdu: T50
(keşfedilen uzantı ↔ kompozisyon kökü) ve T53 (bu belgenin bekçisi), ikisi de
kapandı.

### F4 — bir kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T47** | Kalite ölçümü | F4'ün **kabul sınavı**, cilalama değil. Tiyatro yarısı bitti (`ContradictingEvidenceVerdict` artık okunuyor, payda görünür); **atılan cümle oranı başlamadı** |

**T54 kapandı** ve kapanışı bir öncülü yanlışladı. Muafiyet artık `rca_runs`'ta:
`model_boundary` (dört değerli kapalı küme) + `model_boundary_override_reason`,
damgası `RcaAdmission.TryStartAsync`'te ve **zorunlu bir parametre** — yani
unutulması derlenmiyor. Ticket'ın *"kayıt zaten `RcaReportEntity`'de belge
olarak duruyor"* gerekçesi ölçümle düştü: belgeyi hiçbir üretim kodu yazmıyor,
dolayısıyla kolon bir kopya değil **tek gerçek kayıt**.

Bıraktığı sınır yazılı: üretimdeki tek değer `NotEngaged`, çünkü modeli çağıran
üretim yolu yok. `Overridden`'ın gerekçesiz var olamayacağı **tip düzeyinde**
ölçüldü; bir üretim koşumunun `Overridden` damgalandığı **ölçülmedi**.

**T44 ve T51 kapandı.** T44 iki kapıyı da kurdu; T51 raporu kalıcı yaptı ve
sayacı ekrana çıkardı. T51'in bıraktığı sınır kayıtta: **`SaveAsync`'i hiçbir
üretim kodu çağırmıyor** — tablo var, yol var, yazan yok.

### FS — kapandı

S06, S07 ve **S08** bu turda kapandı. S06'nın cevabı ikinci şıkla geldi ve
ürünün gerçek bir kusuruydu: toplayıcı sayfalama açıkken **hatasız** yarım
config alıyordu, ve fark motoru kaybı *"silinmiş yüzlerce satır"* diye
okuyordu — yani kusur sessiz kalmıyor, **başka bir şey hakkında yalan
söylüyordu**. S08 çekim modelini düzeltti.

Kalan açık kalem ticket değil **veri**: gerçek bir RouterOS cihazından tek bir
`/export terse` çıktısı. MikroTik'in sayfalayıp sayfalamadığı bilinmiyor ve
öykünme bilmediği bir olguyu taklit etmiyor.

### F2 — bir kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T49** | Dashboard container'ı | Compose'a `ui` girdi, Keycloak'ın ön/arka kanal ayrımı çözüldü. Kabul kriterlerinden **üçü** Docker istiyor ve koşulmadı: yığın kalkıyor mu · container'daki ekrandan giriş yapılabiliyor mu · e2e container'a karşı koşuyor mu. Ticket'ın öngördüğü dört sorundan **üçü çıkmadı** |

### MCP — bir kalem açık, biri kısmî

M01 (protokol çekirdeği + uyum kapısı), M02 (komut çekirdeği + CLI paritesi),
M06 (redaksiyon + K6 kapısı) ve M09 (kimliğin bulunması) **kapandı**. M03
(`bizigo-sim`), M04 (okuma araçları), M05 (RCA araçları) ve M08 (kimlik taşıma)
main'de ve `status: 1` — dalları girdi, kabul kriterlerinin tamamının kapandığı
ayrıca ölçülmedi. **M07** (kaynaklar ve abonelik) başlamadı.

> ⚠️ Bu bölüm **düzyazı**, tablo değil — ve `EpicStatusTests`'in raporu okuyan
> kapısı yalnızca tablo satırı görüyor. Yani buradaki bir bayatlama bekçiye
> **görünmez**; bölüm bir kez tam olarak bu yüzden bayatladı (T61 §4.2).

### F5 — kapsam kararı verildi (2026-09-05)

RCA belgesinin uyarısı — *"F1–F4 bittiğinde yeniden kapsam kararı verilmeli"* —
karşılandı. Karar: **S1 · Topoloji-lite**.

| Tür | Kader |
| --- | --- |
| **Topology** | **Karşılandı, sınırıyla:** `TopologyProvider` envanter öznitelikleri (upstream · vlan · firmware) üzerinden çalışıyor. **İlişki grafiği yok** |
| **Metric** | **Kalıcı muaf** — `EvidenceKinds.Exempt` |
| **Trace** | **Kalıcı muaf** — K2 (ağ cihazları trace üretmiyor) |

Kararı ölçüm belirledi ve ölçüm beklenmedik çıktı: RCA §3.1'in *"lift topolojiyi
telafi ediyor"* gerekçesi **yazılmamıştı** — ne olay tablosunda ne envanterde
VLAN/upstream/firmware vardı. Yani F5'i ertelemenin gerekçelerinden biri var
olmayan bir telafiye dayanıyordu.

**Kapanmayan boşluk kayıtta:** §4'ün ilk maddesi (*korelasyonlar çekme
modelinde*) **S1 ile kapanmadı** ve F5'in hiçbir seçeneği onu kapatmıyordu —
sağlayıcı sözleşmesi çekme şeklinde. Ayrıntı ve seçenekler:
[F5 kapsam kararı](../f5-kapsam-karari/index.md).

---

## 3 · Koordinatörde biriken canlı doğrulamalar

Bunlar ticket değil, **koşum** — ve hiçbiri bir ajana verilemez (§2):

| Ne | Durum |
| --- | --- |
| `POST /v1/rca` ve `GET /v1/rca/quality` canlı yığına karşı | Koşulmadı |
| Uçtan uca arşiv kurtarma, **gerçek RustFS** ile | Koşulmadı |
| Canlı model koşumu (Ollama/vLLM) — T42 sahte `HttpMessageHandler` ile sınandı | Koşulmadı |
| Benchmark'lar `-c Release`, **sessiz makinede** | Koşulmadı |

Dördü de aynı sınıfa giriyor: bugün **çalıştığı varsayılıyor**, ölçülmedi.

---

## 4 · Yazılı duran bilinen boşluklar

Bunlar ticket değil, **kayda geçmiş sınırlar**. Kapatılmaları ayrı karar
gerektiriyor:

- **Korelasyonlar çekme modelinde.** Beş korelasyon *"sorarsan görüyorum"*
diyor, *"bir şey oldu"* diyemiyor. Ürünün **push modda anomali tespiti yok**
ve F4 bittiğinde de olmayacak.
- **Eşzamanlı uç slot muhasebesine girmiyor** (T46). `MaxConcurrentGlobal`
bugün yalnızca takvim işçisinin tavanı.
- **Kuyruktan devralma tek örnek varsayıyor** (T46). İki API örneği aynı satırı
devralabilir; çözümü koşullu güncelleme ama ölçmeden yazmak sınanmamış bir
kilit eklemek olurdu.
- **`model_boundary_override_reason` koşum kaydına bağlanmadı** (T42). Muafiyet
yapılandırmada görünüyor, **raporda görünmüyor** — muafiyetin sessiz olduğu
hâle yakın.
- **Entropi terfisi** (T41). Gölge katman ölçüldü: 96 belirtecin 59'u aday, ve
listenin başında **FortiGate imza adları** var. Terfi ayrı bir ticket ve
**bu iki sayıyla açılmalı**.
- **Kalan beş plugin sabiti** (§8.1). `lead: 30m`, `max_items: 400`,
`max_duration: 60s`, hipotez `3`, aksiyon `2` — işaretli, ölçülmedi.
- **"Konteyner gerekmiyor" ayrımı bir bekçiye çevrilebilir mi?** Bugün yalnızca
okuma disiplinine bağlı, ve bu depoda okuma disiplinine bağlanan kurallar
tekrar tekrar kaybetti.

---

## 5 · Sıra önerisi

**Kritik yol MCP'nin M01'i** — M02–M08 ona bağlı ve MCP en geniş yeni yüzey.

Paralel gidebilecekler (kesişmeleri yok):

```
M01 ──► M02 ──► M04 ──► M05 ──► M06 ──► M07/M08
  └───► M03

T44 ──► T47
T38 ──► T47
T32 (bağımsız)
T48 (bağımsız)
T49 (bağımsız)
S06 ──► S07
```

**T47 iki koldan besleniyor** (T44 ve T38) ve F4'ün kabul sınavı — yani
F4'ün kapanışı en geç o.

**F5'in kapsam kararı** F1–F4 kapandığında verilecek; bugün karar vermek
için erken ve belgenin kendi uyarısı bu.

---

## 6 · Bu raporun bilmediği şey

**Ticket `status` alanları bayatlıyor.** Bu raporu yazarken FS'in beş
ticket'ının durumu gerçekle uyuşmuyordu — S04 ve S05 kapanmıştı, `0`
görünüyorlardı. Düzelttim, ama **mekanizma yok**: alanları güncel tutan bir
bekçi olmadığı sürece bir sonraki rapor da aynı düzeltmeyi yapacak.

Bu, `WikiSourceDigestTests`'in vault için çözdüğü sorunun ticket
katmanındaki karşılığı ve **çözülmedi**.

**T45 ve T46'nın ticket dosyası hiç yazılmadı** — kapsamları
`tickets-f4/index.md`'nin tablosunda ve brief'lerde yaşadı. İkisi de kapandı,
ama kararları ticket dosyası yerine ajan raporlarında duruyor.
