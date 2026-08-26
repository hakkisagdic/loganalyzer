---
kind: spec
title: "Kalan iş — uygulamanın tamamı için ne var (2026-08-26)"
---

# Kalan iş raporu

Bugünkü ölçülmüş hâl: derleme **21 proje 0 uyarı** · birim **1181 geçti /
4 atlandı / 0 düştü** · entegrasyon **167 geçti / 5 atlandı / 0 düştü** ·
uçtan uca hazırlık **gerçek yığına karşı geçti**.

`main` = `3a1a148`, uzakla eşit.

---

## 1 · Faz faz durum

| Faz | Ne | Durum |
| --- | --- | --- |
| **F1** — boru hattı | Ingest, depolama, parser motoru, kimlik, API | **13/13 kapandı** |
| **F2** — arayüz | Ekranlar, BFF, alarm, change | 16/16 kapandı · **T49 yeni açıldı** |
| **F3** — detection ve kanıt | Sigma, korelasyonlar, kanıt paketi | 9/12 · **T32, T38, T48 açık** |
| **F4** — agentic RCA | Prompt tabanı, plugin, tetikleyici, kota, LLM | 5/7 · **T44, T47 açık** |
| **F5** — gözlemlenebilirlik | Metrik · trace · topoloji sağlayıcıları | **başlamadı** |
| **FS** — simülatörler | Cihazsız uçtan uca koşum | 5/7 · **S06, S07 açık** |
| **MCP** — protokol | İki yüzey, sekiz ticket | **M01 koşuyor**, M02–M08 açık |

---

## 2 · Açık ticket'lar — on beş kalem

### F3 — üç kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T32** | Derleme hattı ve SQL versiyonlama | Sigma kolunun son halkası. Kapsam kararı T30'un ölçümüne bağlıydı ve ölçüm bitti |
| **T38** | Altın küme ve inceleme akışı | F3'ün **tek buluşma noktası** — detection ve kanıt kolları burada birleşiyor. Alarm kapatmanın zorunlu parçası; doldurulmazsa kalite hiç ölçülemez |
| **T48** | `Produces<T>` kapısının kör noktası | Delik **dört kez** açıldı, her seferinde bulan kişi farklı. Sorun dikkat değil, mekanizmanın yokluğu |

### F4 — iki kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T44** | LLM adımları ve ikinci kapı | **Koşuyor.** Cümle bağlama kapısı + atılan cümle sayacı — F4'ün Karar 1'i buna bağlı |
| **T47** | Kalite ölçümü | F4'ün **kabul sınavı**, cilalama değil. T44'ü ve T38'in altın kümesini bekliyor |

### FS — iki kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **S06** | N3: CLI öykünmesi | Bugün cevabı **olmayan** bir soruyu tutuyor: toplayıcı sayfalamayı biliyor mu? `--More--` görülmezse çıktının yarısı **hatasız** kaybolur |
| **S07** | İmzalı webhook üreteci | Idempotency: aynı teslimat iki kez → tek kayıt. Yoksa RCA'nın *"öncesinde şu config değişti"* kanıtı iki kez görünür |

### F2 — bir kalem

| # | Ticket | Neden açık |
| --- | --- | --- |
| **T49** | Dashboard container'ı | *"Yığının tamamı tek komutla kalkıyor mu"* sorusunun cevabı bugün **hayır** — API kalkıyor, ekran kalkmıyor. Ve BFF'in container ağında çalışıp çalışmadığı **hiç sınanmıyor** |

### MCP — sekiz kalem

M01 (protokol çekirdeği + uyum kapısı) **koşuyor**; kalan yedisi ona bağlı.
M02 CLI paritesi · M03 `bizigo-sim` · M04 okuma araçları · M05 RCA araçları ·
M06 redaksiyon + K6 kapısı · M07 kaynaklar/abonelik · M08 kimlik taşıma.

### F5 — başlamadı, ve ayrı bir proje büyüklüğünde

Metrik, trace ve topoloji sağlayıcıları. RCA belgesinin kendi uyarısı:

> K21'in maliyeti — ürünü *"log analiz katmanı"*ndan *"gözlemlenebilirlik
> platformu"*na taşıyor. F1–F4 bittiğinde **yeniden kapsam kararı verilmeli**.

Kanıt sözleşmesi beş türü de **bugünden tanıyor** (T34), uygulaması yok.
Yani F5 bir genişleme değil, sıralanmış bir kapsam.

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
