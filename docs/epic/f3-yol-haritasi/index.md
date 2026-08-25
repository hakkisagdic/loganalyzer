---
kind: spec
title: "Kalan işin haritası — F3 ve devreden borç"
---

# Kalan işin haritası

Bu belge **2026-08-25** günündeki durumu anlatıyor. Ticket durumları, ölçülmüş
sayılar ve kimin neyi beklediği o günün fotoğrafı; sonraki bir tur bunu
geçersiz kılar.

Amacı tek şey: *"ne kaldı ve neyi bekliyor"* sorusunun cevabını, dağılmış
ticket dosyalarını tek tek açmadan verebilmek.

> Bir önceki hâli 2026-08-21'e aitti ve bayatladı: F2'yi açık, F3'ün Sigma
> kolunu açık gösteriyordu. Dördü de kapandı. **Bu belgenin kendisi bayatlayan
> bir belge** — okurken tarihine bak.

---

## 1 · Beş faz, neredeyiz

| Faz | Kapsam | Durum |
| --- | --- | --- |
| **F1** — Boru hattı | ingest → parser → OCSF/OTel → ClickHouse, ham arşiv, replay, envanter, OIDC | **12/12 kapalı** · borcu §4'te |
| **F2** — Görünürlük | Next.js UI, arama, parser editörü, katalog, alarm + bildirim, change feed | **16/16 kapalı** — T27 kapandı |
| **F3** — Detection + RCA kanıtı | Sigma → SQL, kural yönetimi, kanıt sağlayıcı, RCA raporu | **buradayız** — 9/11 kapalı |
| F4 — Agentic | senaryo plugin, MCP server, LLM yorumu, dört tetikleyici | başlamadı · **ilk kararı verildi** (§6) |
| F5 — Kanıt genişletme | metrik, trace, topoloji sağlayıcıları | başlamadı |
| **FS** — Cihaz simülatörleri | SSH config çekimi, syslog basımı, değişiklik bildirimi; filo + kapsam yayılımı | **paralel** · 2/7 · [belge](../fs-simulatorler/index.md) |

F5 sonrası tanıtım materyali var, henüz kapsamlandırılmadı.

**FS neden bu tabloda ve neden numarasız:** ekip gerçek cihazlara erişmiyor,
dolayısıyla `Bizigo.Devices` ve ingest'in canlı yolu bugüne kadar hiçbir yerde
koşmuyordu. Ayrı bir faz çünkü üç yüzeye dokunuyor; **paralel** çünkü F3'ün
kritik yoluyla hiçbir bağı yok. Ticket'ları `S` önekli (S01…S07).

---

## 2 · F3 — dokuzu kapandı, ikisi açık

| Ticket | Durum | Sahip | Kalan |
| --- | --- | --- | --- |
| T29 · `signature_hash` | ✅ | — | — |
| T30 · Sigma prototipi | ✅ | — | 269'a ölçekleme **yapılmadı** (§3) |
| T31 · ProcessingPipeline | ✅ | — | nginx ailesinin altın örnek doğrulaması **yok** (§3) |
| T33 · kural yönetimi | ✅ | — | açık yok |
| T34 · kanıt sözleşmesi | ✅ | — | — |
| T35 · beş korelasyon | ✅ | — | kapsam negatif testleri **sonradan** eklendi (D7) |
| T36 · kanıt paketi | ✅ | — | — |
| T37 · rapor ekranı | ✅ | — | canlı yığın doğrulaması §5'te |
| T39 · `specificity` ölçütü | ✅ | — | soru **bugün yok**; bekçi doğduğu gün yanacak |
| **T32** · derleme + üç kapı | 🔄 | **6** | beyanların kesişim düzeltmesi sonrası gözden geçirilmesi |
| **T38** · altın küme | 🔄 | **7** | ekran yarısı indi; **nginx combined örneği** T31'den devredildi |

### Sigma kolunun ölçülmüş hâli (2026-08-25)

```
manifest : total 24 · written 21 · gated 3 · gated_upstream 0 · failed 0
Kapı 3   : beyanlı 19 · bilerek beyansız 1 · ölçüm bekleyen 0
kapsam   : compiled == runs == 21 · 7 kural satır döndürüyor · %29
maliyet  : kural başına 6,6 ms · eşleme satırı 208 (42 alan)
```

**Kapsam kararı çivilendi:** `firewall` + `network_connection`. Payda **≥14**
ve oran **≤%43** — alt sınır olarak yazıldı, çünkü kesişim dolu olması kanıt
değil (aracın asimetrisi, §2'de kodda).

Kararı belirleyen şey oran değil **verinin varlığı**: DNS'in verisi yok,
hiçbir parser sorgu adı üretmiyor, o kurallar derlenmiyor bile. Oran nereye
düşerse düşsün o kategori kapsama giremez.

### Bu turda yönü yanlış tahmin ettiğimiz yer

`fw_chain` düzeltmesinin ve `VENDOR_EMPTY_COLUMNS`'ın derleme sayısını
**düşüreceğini** sandık — ikimiz de. Ölçüm tersini gösterdi:

| | Önce | Sonra |
| --- | --- | --- |
| Satır döndürdü | 6 | **7** |
| Eşleşme oranı | %25 | **%29** |

**Bekçi eklemek kapsamı daraltmadı, genişletti.**

---

## 3 · Kapanan ticket'ların taşıdığı açıklar

Kapanmış bir ticket'ın açık kalemi olması bir çelişki değil — kriter
karşılanmadıysa **yazılı** olduğu sürece kapanabilir. Bu turda üç kez bunu
yaptık ve üçünde de kapatan kişi **kendi işini sayarak** buldu.

| Nereden | Ne | Kim kapatacak |
| --- | --- | --- |
| T30 | 269 kurala ölçekleme yapılmadı; çarpım **ayrık alan** üzerinden, kural sayısı üzerinden değil | T30'un gerçek korpusu geldiğinde |
| T31 | **nginx ailesinin `at_least_one` beyanı yok** — beş beyanının hepsi `none`. Sebebi eşleme değil korpus: örneklerimiz `access-json`, o biçim `core.host` doldurmuyor | **T38** — combined biçimli altın örnek |
| T35 | Beş korelasyon sağlayıcısının kapsam negatif testi ticket kapandıktan **sonra** yazıldı (D7) | ✅ kapandı |

nginx bulgusu tek başına kayda değer: **vendor kırılımını saymak beş saniyelik
işti** ve T31'in *"tam kapandı"* diye kapanmasını engelledi.

---

## 4 · F1'den devreden borç

Hiçbiri "kod eksik" değil. Hepsi **doğrulanmamış** ya da **gerekçesi kayıtta
olmayan** şeyler — bu deponun en pahalı hata sınıfı (§7).

| Kalem | Ne | Durum |
| --- | --- | --- |
| T03 · çift yazma | Zaman aşımı penceresinde aynı batch WAL'a iki kez yazılıyor | **ölçüldü, var** |
| T03 · kimlik | İki kayıt birbirine bağlanamıyor — `EventId` her çözümlemede yeniden üretiliyor | tekilleştirme anahtarı **açık soru** |
| T40 · ham arşiv kurtarma | ✅ **kapandı** — kurtarma nesneyi sha256'sından tanıyor, tutan yoksa hiçbir şey yazmıyor | kalan: gerçek RustFS koşumu (§5) |
| **T05 · karantina hiç bağlı değil** | `ParserQuarantine` üretimde **hiç örneklenmiyor**; `parsers.quarantined` kolonuna hiçbir yerde yazılmıyor | **ayrı ticket** — yazma yolu ve operatör yüzeyi kararı |
| T05 · `matchTimeout=50 ms` | Gerekçesi kayıtta yok | zemini oluştu, **8**'de |
| T02 · ölçen ama yargılamayan | ✅ **kapandı** — sayı artık tablodan geliyor, sürücünün raporundan değil | — |
| T02 · elle liste | ✅ **kapandı** — `ScopeNegativeTests` yansımayla keşfediliyor, `Pending` boş | — |
| T12 / D3 | *"Sidecar arızalıyken throughput düşmüyor"* | mantıklı ama **ölçülmemiş** |
| B14 | Şema tamamlama listesi istemcide motorun kopyası | gerekçeli kabul |
| B16 | Worktree `node_modules` bayatlığı | yapısal; **merge sonrası `npm install` bir kural** |

### T05'in bulduğu şey ticket'ından büyük çıktı

Üç katman, üçü de ölçüldü:

1. **`TimedOut` Dispatcher'dan sağ çıkmıyordu** → `EventComposer`'ın uyarısı
   sevk edilen konfigürasyonda **hiç ateşlenmiyordu**. Belge *"yalnızca
   logluyor"* diyordu; gerçek bir kademe kötüydü — **log satırı bile yoktu**.
   ✅ Düzeltildi.
2. **`ParserQuarantine` üretimde hiç örneklenmiyor**, ve `parsers.quarantined`
   kolonuna hiçbir yerde `true` yazılmıyor. `PublishedParserLoader`'ın süzgeci
   **kimsenin yazmadığı bir kolona** bakıyor.
3. **`RedosLinter` operatöre *"karantina devrede"* diyordu** — bir bekçinin
   ağzından çıkan yanlış cümle. ✅ İddia kaldırıldı, yerine karşı-iddia
   konmadı.

İkincisi ayrı bir ticket ve **bilinen bir erteleme değil**: `t12-kararlar` ve
`tickets/sidecar` karantinadan hiç bahsetmiyor; `f1-kapanis`'teki tek satır
**riski** tarif ediyor, bağlantıyı iddia etmiyor. İki kişi aradı, ikisi de
bulamadı.

---

## 5 · Koordinatörde biriken canlı doğrulamalar

Hepsi Docker gerektiriyor, hepsi faz sonu için:

- **Göçler uygulanmadı** — `AddGoldenReviews`, `AddActualRootCauseToGoldenReview`
- `POST /v1/rca`, `GET /v1/rca/quality` **canlı yığına karşı hiç koşmadı**
- Uçtan uca **ham arşiv kurtarma** gerçek RustFS'te koşmadı
- Scrub örnekleme oranı ve saklama süresi **ölçülmedi** (T04 #2, #3)

---

## 6 · F4'ün verilmiş tek kararı

Faz başlamadı ama bir karar çivilendi ve RCA belgesinin 2. kuralını
**değiştirdi**:

> **Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
> gösteriliyor.** *"Model 12 cümle üretti, 3'ü kanıta bağlanamadı ve
> çıkarıldı."*

Önceki hâli *"desteklenmemiş rozetiyle göster"*di. Değişti çünkü o bir
**gösterme** kararıydı: model uydurursa rapor onu gösteriyor ama
**engellemiyor**. Yalnızca atmak da yetmezdi — o zaman kalite ölçülemez olurdu.
İçerik atılıyor, **sayı kalıyor**, ve o sayı F4'ün kalite göstergesi.

**Hâlâ açık:** prompt'a ne kadar içerik girecek (ham `raw_data` mı, kanıt
özetleri mi), anomali tetikleyicisinin hangi sinyalden doğacağı, kotanın neyi
koruduğu, ve senaryo plugin formatının dört senaryo yazılmadan çivilenip
çivilenemeyeceği.

---

## 7 · FS — 2/7

| Ticket | Durum | Not |
| --- | --- | --- |
| S01 · cihaz profili | ✅ | Dört profil, `SimulatedDeviceTransport` |
| S02 · syslog basıcı | 🔄 | Çapa `SampleClock`'a taşındı; TTL artık **kapı** |
| S03 · SSH sunucusu | 🔄 | Container yazıldı; **`SshDeviceTransport` F1'den beri ilk kez koştu ve geçti** |
| S04 · senaryo motoru | ⬜ | S02+S03'ü bekliyor |
| S05 · filo + kapsam | ⬜ | FS-a'nın kapanış ticket'ı |
| S06 · CLI öykünmesi | ⬜ | FS-b |
| S07 · webhook üreteci | ⬜ | FS-b |

**Faz ikiye bölündü.** FS-a (S01–S05) ürünün gerçek cihaz olmadan uçtan uca
koşmasını sağlıyor — diğer fazların beklediği kapı bu. FS-b (S06–S07)
toplayıcının **doğru** koştuğunu sağlıyor. İkisi ayrı sorular.

---

## 8 · Bugünün ölçülmüş hâli

```
CI (push, 2026-08-25) → success, DOKUZ işin dokuzu

birim         965 · UI          477 · entegrasyon 160/161
sigma-build   180 · sidecar      52 · derleme 19 proje 0 uyarı
```

Yerelde koştururken kapı listemi **sekiz** sanmıştım; dokuzuncu iş
(*"Uçtan uca — çalışan ürün"*) paralel oturumdan gelmişti ve listemde yoktu.
Yani "tam" diye koşturduğum liste tam değildi, ve bunu ancak CI söyledi —
envanterdeki *"CI'nın gördüğü şey senin koşmadığın şeydir"* maddesinin
bugünkü örneği.

---

## 9 · Bu belgenin bilmediği şey

**Gerçek müşteri verisi olmadan kapanmayacak** iki kalem var ve ikisi de
"yapılacak iş" değil:

- **Baseline pencere uzunluğu.** Dirsek tohumlama düğmesiyle yedi kat kayıyor;
  bağlayıcı bir taban bu fixture'dan çıkmıyor.
- **Sigma kapsam oranının anlamı.** Payda bu turda **dört kez** oynadı
  (24 → 14 → 15 → ≥14) ve alt sınır olarak yazıldı.

İkisi de F3'ü bloke etmiyor ama **F3'ün sayıları bağlayıcı değil** demek. Bir
sonraki fazda birinin bu sayılara dayanması gerekirse önce bu paragrafı okusun.
