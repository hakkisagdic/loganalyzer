---
title: "T32 — Derleme hattı ve SQL versiyonlama"
kind: ticket
status: 1
---

# T32 — Derleme hattı ve SQL versiyonlama

**Bağımlılık:** T31 · **Sonraki:** T33 · **Yöneten karar:** mimari kararlar §3.1

## Amaç

Sigma kurallarını **derleme zamanında** SQL'e çevirip repoda versiyonlamak.
Sıcak yolda Python yok, belirsizlik yok.

## Kapsam

### İçinde

- SigmaHQ kurallarını çeken, T31'in pipeline'ıyla derleyen ve çıktıyı repoya
yazan hat. Referans: [`clicksiem/sigma_rules`](https://github.com/clicksiem/sigma_rules)
— günlük cron, üretilen kurallar commit'li.
- Üretilen SQL repoda versiyonlu; kural kimliği, kaynak sürümü ve derleme
tarihi ile birlikte.
- **CI kapısı:** derleme çıktısı depodakiyle aynı değilse adım düşüyor. Aksi
halde kural değişir, kimse fark etmez.
- Derlenemeyen kural raporlanıyor ve sayısı **görünür**; sessizce atlanmıyor.
- Sidecar imajında Python 3.13+ (backend'in sert kısıtı; imaj zaten
`python:3.13-slim`).

### Dışında

- Kural yönetimi ve çalıştırma — T33.

## Kabul kriterleri

- Hat tek komutla koşuyor ve çıktısı tekrarlanabilir: aynı girdi, aynı SQL.
- CI kapısı sürüklenmeyi yakalıyor; kapının kırmızı yanabildiği ölçüldü.
- Derlenemeyen kural sayısı sıfırdan büyükse CI **uyarıyor** — kabul edilebilir
ama görünmez olmamalı.
- Üretilen SQL'in en az biri canlı ClickHouse'ta koşup doğru sonucu veriyor.

## Notlar

Build-time kararının **üçüncü gerekçesi** ölçümle geldi: backend üç aylık, iki
yıldızlı, tek geliştiricili. Üretilen SQL repoda durduğu için proje terk edilse
bile mevcut kurallar çalışmaya devam ediyor — kaybedilen yalnızca *yeni* kural
derleme yeteneği, ve LGPL-3.0 fork'a izin veriyor.

Bir tuzak ölçüldü: `clicksiem/sigma_rules` deposundaki dosya sayıları %100 uyum
gibi görünüyor ama **yanıltıcı** — dönüşüm başarısız olduğunda eski çıktı dosyası
depoda kalıyor. Bizim hattımız bunu tekrarlamamalı: başarısız derleme eski
dosyayı **silmeli** ya da açıkça işaretlemeli.

## Neden hâlâ `status: 1` — denetim (2026-09-15)

Ticket'ta *"şu kriter koşturulmadı, o yüzden 1"* diye okunabilir bir satır yoktu;
sebep başka bir belgede (`t32-derleme-tasarimi` §5) duruyordu ve o tablo da
bugünün gerçeğini anlatmıyor. Dört kriterin **ölçülen** hâli:

| Kriter | Durum | Ölçüm |
| --- | --- | --- |
| 1 · Tek komut, tekrarlanabilir çıktı | ✅ **ölçüldü** | `python -m sigma_build.compile --check` → *"detections/sigma üretilenle birebir aynı"*. Konteyner yok |
| 2 · CI kapısı sürüklenmeyi yakalıyor, kırmızı ölçüldü | ✅ | Kapı `compile --check`; Kapı 2'nin kendi sınavı CI'da **her koşumda** (`explain_gate --self-test`, sonucu bilinen üç sorgu). `sigma-build` paketi: **201 test geçti** |
| 3 · Derlenemeyen kural sayısı görünür | ✅ | `detections/sigma/manifest.json` → `total 24 · written 21 · gated 3 · failed 0`. Sayı sıfır değil ve **manifestte** duruyor |
| 4 · Üretilen SQL canlı ClickHouse'ta koşup **doğru sonucu** veriyor | 🔄 **yarısı** | CI'nın Kapı 2'si SQL'in **kabul edildiğini** kanıtlıyor (gerçek ClickHouse servisi + ürünün kendi migrator'ı). *"Doğru sonuç"* Kapı 3'ün işi ve **yüklü altın örnek** istiyor — canlı yarısı koordinatörde (§2) |

Yerelde koşan üç çevrimdışı kapı (hepsi bu makinede, konteyner yok):
`ruleset --verify` → *24 kural çiviyle birebir aynı* · `view_columns --check` →
*göçlerle birebir aynı* · `compile --check` → *birebir aynı*.

### Kalan kalemler — üçü ayrı sınıfta

| Kalem | Sınıf | Not |
| --- | --- | --- |
| Kapı 3'ün canlı yarısı (yüklü altın örnek + sonuç doğruluğu) | **konteyner** | Koordinatörde. Kriter 4'ün ölçülmeyen yarısı bu |
| Kural başına **duvar saati** maliyeti | **sessiz makine** | Bir **kayıt**, kapı değil: `t32-derleme-tasarimi` §6 ölçekleme sorusunu saatsiz cevaplamış (karesel deyim, 24 kuralda görünmüyor). Bu makinede bugün ölçülemez |
| Korpus: `ruleset_commit` = `t30-ornekleminden-terfi` | **karar** | Kapsam *"SigmaHQ kurallarını çeken hat"* diyor; bugün çivilenen küme **T30'un terfi ettirilmiş örneklemi**, yukarı akıştan sabit SHA ile çekilen set değil. Korpusun ne olacağı ürün kararı — ölçüm değil |

Üçüncüsü bu denetimin **beklemediği** bulgusu: T32'nin açık kalmasının sebebi
yalnızca duvar saati değil. Duvar saati bir kayıt; korpus bir **karar**; Kapı
3'ün canlı yarısı bir **koşum**. Üçü tek satıra *"ölçüm bekliyor"* diye
yazıldığında ikisi görünmez oluyordu.

### Kapanan üç bayat CI yorumu

`ci.yml`'ın `sigma-build` ve `sigma-explain` işlerinde üç yorum *"bugün sıfır
kural derleniyor"* / *"`detections/sigma/` boş"* diyordu — **T31'i bekliyorlardı
ve T31 kapandı** (`status: 2`). Bugün 24 kural derleniyor, 21 SQL dosyası depoda.
Yorumların **gerekçesi** duruyor (sıfır kuralda bile kapıyı koşturmak bilinçliydi
ve bir kusur yakalamıştı); yanlış olan yalnızca *"bugün"* iddiasıydı, o düzeltildi.

### Bu denetimin aramadığı

- Kapı 2'nin `EXPLAIN` biçim ölçümünün bu makinedeki sonucu — ClickHouse
gerektiriyor.
- `python3` bu makinede **3.9**; hat 3.13 istiyor (`pySigma`). Kapılar
`python3.13 -m venv` ile koştu. Varsayılan yorumlayıcıyla koşturmak *"pySigma
kurulu değil"* hatası veriyor — ve o hata **sessiz değil**: hat sıfır kural
üretmeyi reddediyor, çünkü `--write` koşturan biri bütün çıktıyı silmiş olurdu.
