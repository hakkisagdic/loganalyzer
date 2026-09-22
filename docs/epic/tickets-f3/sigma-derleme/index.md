---
title: "T32 — Derleme hattı ve SQL versiyonlama"
kind: ticket
status: 2
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
| 4 · Üretilen SQL canlı ClickHouse'ta koşup **doğru sonucu** veriyor | 🔄 **iki ayrı iddia** | Canlı koşum bir kez yapıldı ve gerçek eşleme kusuru buldu; fakat CI kapısı hâline gelmedi |

### Düzeltme — Kapı 3 canlıda bir kez koştu

Bu denetimin ilk hâli canlı yarıyı *ölçülmedi* sayıyordu; vault kaydı bunun
yanlış olduğunu gösterdi. `routeros_forward_new` canlı ClickHouse'ta düştü ve
düşmesi doğruydu: RouterOS parser'ı `action` alanını bilerek boş bırakıyor,
zincir adı `fw_chain` alanına gidiyor. Kapı böylece gerçek bir eşleme boşluğu
buldu.

Ancak iki iddia ayrılmalı:

| İddia | Durum |
| --- | --- |
| Üretilen SQL canlı veride doğru sonucu veriyor | **bir kez ölçüldü** |
| Bu doğruluk her CI koşumunda korunuyor | **hayır** — CI yalnızca `golden_gate --shape-only` koşuyor |

Bugünkü beklenti dosyası 19 kalemdir: **7 `at_least_one`, 12 `none`**; ayrıca
bir gerekçeli `undeclared` kural vardır. Bu üç sayı ve her beyanın gerçek bir
üretilen SQL dosyasına baktığı `test_expectation_count.py` ile çivilendi.

Yerelde koşan üç çevrimdışı kapı (hepsi bu makinede, konteyner yok):
`ruleset --verify` → *24 kural çiviyle birebir aynı* · `view_columns --check` →
*göçlerle birebir aynı* · `compile --check` → *birebir aynı*.

### Kalan kalemler — üçü ayrı sınıfta

| Kalem | Sınıf | Not |
| --- | --- | --- |
| Kapı 3'ün canlı yarısının **CI kapısı olması** | **karar** | Canlı koşum yapıldı; açık olan her CI koşumuna girip girmeyeceği |
| Kural başına **duvar saati** maliyeti | ✅ **iki koşum** | Compose yığını durdurup kaynak kapısı geçtikten sonra ölçüldü: 24/100/269 kuralda yaklaşık 0,37–0,40 ms/kural; ölçekleme 1,067× ve 1,013× |
| Korpus: `ruleset_commit` = `t30-ornekleminden-terfi` | **karar verildi** | Determinizm kapısının korpusu çivili örneklem olarak kalıyor; yayımlanmış SigmaHQ setini derleyebilme ayrı ve henüz ölçülmemiş iddia |

Üçüncüsü bu denetimin **beklemediği** bulgusu: T32'nin açık kalmasının sebebi
yalnızca duvar saati değil. Duvar saati bir kayıt; korpus bir **karar**; Kapı
3'ün canlı yarısı bir **koşum**. Üçü tek satıra *"ölçüm bekliyor"* diye
yazıldığında ikisi görünmez oluyordu.

### Korpus kararı

Determinizm kapısının korpusu terfi ettirilmiş örneklem olarak kalır. Yukarı
akışa bağlı hareketli korpus, her upstream değişiminde kapıyı oynatır ve
determinizm yerine upstream hareketini ölçer. Bununla birlikte kapsamın ikinci
iddiası açıkça ayrıldı: çivili örneklemde deterministik derleme ölçüldü;
SigmaHQ'nun yayımlanmış tam setini derleyebilme henüz ölçülmedi.

Bu son cümle T32'yi açık tutmuyor: tam upstream set, determinizm kapısının
kabul korpusu değil, ayrı bir uyumluluk iddiasıdır. Hareketli upstream'i CI
korpusuna bağlamak aynı girdi/aynı çıktı sorusunu upstream değişikliğiyle
karıştırırdı.

### Kapanış kararı — canlı Gate 3 her PR'da değil

Kabul kriteri *"en az biri canlı ClickHouse'ta koşup doğru sonucu versin"*
diyordu; bu koşum yapıldı ve gerçek bir RouterOS eşleme kusuru buldu. Bu
canlı yarı **her PR'ın CI kapısı yapılmadı**:

- her-PR kapısı olan `compile --check`, `ruleset --verify`, kolon kapısı ve
  Kapı 2 self-test'i ağsız/belirlenimci kalıyor;
- Gate 3 canlı veri beklentileri faz kapanışında ve kural seti
  yükseltmesinde koşturulan ortam kapısı; ClickHouse + tohum veri istiyor;
- beklenti envanteri 19 = 7 `at_least_one` + 12 `none`, ayrıca 1 gerekçeli
  `undeclared`; bu sayılar ve SQL dosyası bağları testle çivili.

### Duvar saati kaydı

`python -m sigma_build.cost` iki kez koştu (Python 3.13.11). Kurulum maliyeti
kural başına paydaya sokulmadı; ilk koşumdaki soğuk paket/eklenti maliyeti bu
yüzden sonucu bozmadı.

| Koşum | 24 | 100 | 269 | Ölçekleme |
| --- | --- | --- | --- | --- |
| 1 | 0,3714 ms/kural | 0,4009 | 0,3962 | **1,067×** |
| 2 | 0,3770 ms/kural | 0,3883 | 0,3818 | **1,013×** |

İki koşumda da tüm sentetik kurallar derlendi, ret sıfırdı. Bağlayıcı
sonuç mutlak bir timeout değil: kural başına maliyet korpusla büyümüyor,
toplam maliyet doğrusal.

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
