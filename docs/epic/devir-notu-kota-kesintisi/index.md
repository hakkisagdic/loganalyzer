---
kind: spec
title: "Nerede kaldık — kota kesintisi devir notu"
---

# Nerede kaldık

Çalışma **2026-08-21**'de kota bitince ortada kesildi. Kotalar **pazartesi**
yenilenecek. Bu belge o ana kadar biriken durumu tutuyor.

Kesilme gerçekten ortadaydı: iki ajana gönderilen mesaj rate limit'e takıldı ve
hiç işlenmedi, bir arka plan koşumu yarıda kaldı. Aşağıda hangi mesajın kime
gitmediği de yazılı.

---

## 1 · İyi haber: main yeşil ve push edilmiş

Kesilmeden önceki son endişem **CI'nın doğrulanmamış olmasıydı**. Doğrulandı:

```
main = b904be4  ·  push edilmiş (unpushed 0)
CI (push, 2026-08-21 14:33) → success

✓ Derle ve birim testleri
✓ Arayüz — tip kapısı, testler, derleme
✓ Sidecar — pytest
✓ Sigma derleme hattı — kapılar ve testler
✓ Kapı 2 — ClickHouse üretilen SQL'i kabul ediyor mu
✓ Entegrasyon testleri (Testcontainers)
✓ Geliştirme ortamı doğrulaması
✓ Compose dosyası ayrıştırılabiliyor mu
```

**Sekiz işin sekizi yeşil.** Bundan önceki dört push kırmızıydı; bu ilk temiz
koşum.

Yani pazartesi temiz bir yerden başlıyoruz — yarım kalan bir merge, çakışan bir
dal ya da kırmızı bir kapı yok.

---

## 2 · Faz durumu

| Faz | Durum |
| --- | --- |
| **F1** — Boru hattı | 12/12 ticket kapalı · borcu §4'te |
| **F2** — Görünürlük | **16/16 kapalı** — T27 bu turda kapandı |
| **F3** — Detection + RCA | **buradayız** · 5 kapalı, 5 açık, 1 başlamadı |
| F4 — Agentic | başlamadı |
| F5 — Kanıt genişletme | başlamadı |

### F3'ün on bir ticket'ı

| Ticket | Durum | Sahip | Kalan |
| --- | --- | --- | --- |
| T29 `signature_hash` | ✅ | — | — |
| T34 kanıt sözleşmesi | ✅ | — | — |
| T35 beş korelasyon | ✅ | — | ama **D7 açık** (§4) |
| T36 kanıt paketi | ✅ | — | — |
| T37 rapor ekranı | ✅ | 4 | canlı doğrulaması koordinatörde |
| **T30** Sigma prototipi | 🔄 | 1 | kapsam kararı — payda 4. kez oynayacak |
| **T31** ProcessingPipeline | 🔄 | 1 | kesişim düzeltmesi + 4. kutu |
| **T32** derleme + üç kapı | 🔄 | 6 | kural yazım kılavuzu · `--refresh` ölçümü |
| **T33** kural yönetimi | 🔄 | 1 | 2. artım: manifest → `alert_rules` |
| **T38** altın küme | 🔄 | 7 | alarm kapatma **ekran** tarafı |
| **T39** `specificity` ölçütü | ⬜ | — | başlamadı, sahipsiz |

---

## 3 · Kesildiği anda tam olarak ne yapılıyordu

### Gitmeyen iki mesaj

| Kime | Ne | Durum |
| --- | --- | --- |
| **2** (`dc037c2d`) | Geri düşüş bulgusunun kabulü + **çok-parser'lı vendor kısıtının genel olup olmadığını ölç** görevi | rate limit, **hiç işlenmedi** |
| **7** (`f1497c85`) | T38'in kalan yarısı: **alarm kapatma ekran tarafı** + `user_name` kaleminin gerekçesi | rate limit, **hiç işlenmedi** |

Pazartesi ilk iş bu ikisini yeniden göndermek. İçerikleri aşağıda §5'te.

### Yarıda kalan koşum

Sidecar pytest'i, swap %80'in altına inmesini bekleyen bir arka plan işiydi
(`bzrn8eao1`). Sonucu alınamadı — **ama CI onu koşturdu ve yeşil**, yani kalem
kapandı. Yeniden koşturmaya gerek yok.

### Cevabı beklenen iki soru

1 sordu, ikisi de cevaplandı ve **kayıtta**:

- Dördüncü kutu açılsın mı → **evet**, gerekçe: `absent`/`present` ayrımının
  aynısı, cevapları zıt.
- `gated` değişiklik ölçütü → **`source_sha` + `blockers`**, `output_sha` değil
  (gated kayıtta o alan yok).

---

## 4 · Devreden borç

### Doğrulanmamış (D listesi)

| # | Ne | Kim |
| --- | --- | --- |
| **D3** | `SidecarLiveTests` atlanıyor — canlı sidecar gerekiyor | koordinatör |
| **D5** | Sigma kapsam oranının **paydası dört kez oynadı** ve hâlâ kesin değil: 24 → 14 → 15 → kesişim düzeltmesinden sonra aşağı inecek | 1 |
| **D7** | **T35'in beş korelasyon sağlayıcısının kapsam negatif testi yok** — ticket `status:2` ama kapı sınanmamış | 7 yazdı, koordinatör koşturacak |

### F1'den devreden, kod eksik değil — **doğrulama** eksik

| Kalem | Durum |
| --- | --- |
| T03 çift yazma penceresi | **ölçüldü, var**; tekilleştirme anahtarı açık soru (`EventId` her çözümlemede yeniden üretiliyor) |
| T02 ölçen ama yargılamayan hız bekçisi | açık — mutlak eşik **koyulmamalı**, aynı süreçte alınan tabana oran |
| T05 `matchTimeout=50 ms` gerekçesi | kayıtta yok |
| T12/D3 sidecar throughput iddiası | mantıklı, **ölçülmemiş** |
| B14 şema tamamlama listesi | gerekçeli kabul |
| B16 worktree `node_modules` bayatlığı | yapısal |

### Koordinatörde biriken canlı doğrulamalar

Hepsi Docker gerektiriyor, hepsi faz sonu için:

- **Göçler uygulanmadı** — `AddGoldenReviews`, `AddActualRootCauseToGoldenReview`
- `POST /v1/rca`, `GET /v1/rca/quality` **canlı yığına karşı hiç koşmadı**
- `/rca` ekranı **gerçek tarayıcıda açılmadı**
- Uçtan uca **ham arşiv kurtarma** gerçek RustFS'te koşmadı
- Scrub örnekleme oranı ve saklama süresi **ölçülmedi** (T04 #2, #3)

---

## 5 · Pazartesi ilk turda kime ne verilecek

| Ajan | İş | Durum |
| --- | --- | --- |
| **1** | Kesişim düzeltmesi + **dördüncü kutu** · sonra T33 2. artım | mesaj gitti, iş biliniyor |
| **2** | **Çok-parser'lı vendor kısıtı genel mi** — dört vendor'da kaç çift? | ⚠️ **mesaj gitmedi** |
| **3** | Boşta. T27 kapandı, keşif belgesi yazıldı | yeni iş gerekiyor |
| **4** | Boşta. T37 kapandı | yeni iş gerekiyor |
| **6** | Kural yazım kılavuzu (üç şart) · `--refresh`'in 269 kuralda ölçümü | mesaj gitti |
| **7** | **T38 alarm kapatma ekran tarafı** | ⚠️ **mesaj gitmedi** |

### 2'ye gidecek mesajın özü

Kendi ölçümünü çürüttü ve bu kabul edildi: `device_hostname` **%100 dolu** diye
raporlamıştı, ama `EventNormalizer` geri düşüş yapıyor —

```csharp
Host = core.host varsa o, yoksa source.Raw.SourceKey
```

Yeni sayaç: **değeri satırdan gelmeyen** satırlar. nginx **22/24**, Fortinet
13/22, Cisco 1/27, MikroTik 0/14. İki yöntem aynı sayıya vardı.

**Yeni görev:** MikroTik'te ayrı ayrı dolu ama aynı satırda hiç birlikte olmayan
**12 alan çifti** var (`system` kimlik, `firewall` ağ alanlarını dolduruyor);
Fortinet'te 3. Soru: bu kısıt **her çok-parser'lı vendor'a** mı özgü? Sayı
küçükse özel durum, büyükse **kural yazım kılavuzu maddesi**.

### 7'ye gidecek mesajın özü

T38'in kalan yarısı: **alarm kapatma ekran tarafı**. Sözleşme çivili
(`POST /v1/alerts/triggers/{triggerId}/close`, alan adları 4'ün ucuyla birebir).

Ekran şunları göstermeli: kapatma akışı · incelemenin **zorunlu** olduğu ·
**"bilmiyorum"un kaydedilebildiği**.

Uyarı: kapatma düğmesi paket üretimini **tetikliyor** (verilen karar), yani ucuz
bir işlem değil. Ekran bunu göstermeli — yoksa yavaş bir düğme "takıldı" diye
okunur.

Ayrıca `user_name` kalemi: alan `FIELD_MAP`'te **var** (Sigma tarafı tanıyor) ama
korelasyon izin listesinde **yok**. İki tarafın ayrışması kalemi acilleştiriyor —
kullanıcı Sigma kuralında `user_name` yazabiliyor, aynı alanı lift ile
inceleyemiyor.

---

## 6 · Bu turda adı konan hata sınıfları

Pazartesi devam ederken bunlar elde olsun. Hepsi **ölçülerek** bulundu.

| Sınıf | Örnek sayısı | Ortak imza |
| --- | --- | --- |
| Adı ile gövdesi ayrışan bekçi | 5 | Mekanik imzası **yok** — ölçüldü, kapı yazılamadı |
| Ölçüm aracının kendi sessiz yanlışı | 4 | *"ölçemedim"* ile *"sorun yok"* aynı çıktıya iniyor |
| Koşum düzeninin sessizliği | 5 | Çıkış kodu okunmuyor; biri **yeşil üretiyordu** |
| Kural vendor'ın sözlüğünü değil bizim varsayımımızı arıyor | 4 | SQL doğru, kolon doğru, **dizge** yanlış |
| Aynı girdinin iki kopyası | 2 | Sürüklenme kapısı göremez — çıktıyı girdiye tutuyor |
| Sahte, ölçülmek isteneni ifade edemiyor | 2 | Bakabileceği bir dünya yok |
| Kutu yüklemleri sayıyor, kuralları değil | 3 | Yüklemler ayrı ayrı var, **kesişim boş** |

En pahalı **önlenmiş** hata: EF'in ürettiği göç `enabled` → `status` geçişinde
her **pasif** kuralı sessizce açıyordu. Kullanıcının kapattığı alarm bir sabah
kendiliğinden bildirim göndermeye başlardı.

---

## 7 · Bu belgenin bilmediği

- **F3'ün sayıları bağlayıcı değil.** Baseline tabanı ve Sigma kapsam oranı
  gerçek müşteri verisi olmadan kesinleşmiyor; ikisi de ayrıntısıyla envanterde.
- Ajanların worktree'leri **duruyor** ve dalları main'e girmiş durumda. Pazartesi
  temizlenebilir, ama önce her birinin main'e tamamen girdiği
  `git branch -d` ile doğrulanmalı (`-D` değil).
- `.githooks/post-checkout` ve `post-commit` çalışma ağacında **takipsiz**
  duruyor; kimin bıraktığı bilinmiyor.
