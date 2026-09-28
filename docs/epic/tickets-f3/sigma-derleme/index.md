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
| 4 · Üretilen SQL canlı ClickHouse'ta koşup **doğru sonucu** veriyor | 🔄 **cümle iki iddia taşıyor** | Aşağıdaki düzeltmeye bakın: canlı koşum **yapılmış** ve gerçek bir kusur bulmuş; **kapı hâline gelmemiş** |

### DÜZELTME · Kapı 3 canlıda koştu — bu denetimin ilk hâli yanlıştı

Bu bölümün ilk yazımı *"Kapı 3'ün canlı yarısı koordinatörde, ölçülmedi"*
diyordu. **Yanlış.** Vault sayfası (`skills/f3-sigma-derleme-kapilari`) canlı bir
koşumu kaydediyor ve kaynağı `t32-derleme-tasarimi` §*Ölçüldü · Kapı 3 ilk
koşumda gerçek bir eşleme kusuru buldu*: `routeros_forward_new` beyanı **canlı
ClickHouse'ta düştü** ve düşmesi doğruydu — `activity_name` boş, çünkü RouterOS
parser'ı `action`'ı bilerek boş bırakıyor ve zincir adı `fw_chain`'e gidiyor.
Kayıp boru hattında, ve *"bunu ancak bu kapı gösterebilirdi"*.

Yani kriter 4'ün cümlesi **iki iddia** taşıyor ve ikisi ayrı hâlde:

| İddia | Hâli |
| --- | --- |
| *"Üretilen SQL canlı ClickHouse'ta koşup doğru sonucu veriyor"* | **ölçüldü** — bir kez, ve gerçek bir eşleme kusuru buldu |
| *"…ve her koşumda ölçülüyor"* | **hayır** — CI yalnızca `golden_gate --shape-only` (çevrimdışı yarı) koşuyor; canlı yarı bir **kayıt**, bir **kapı** değil |

Bu, koordinatörün bugün üçüncü kez adını koyduğu sınıfın dördüncü örneği: **bir
kriter cümlesi iki arıza kipini taşıdığında ölçüm birini görüyor ve yeşil ikisini
temsil ediyor.** Burada tersi oldu — biri ölçülmüştü, ve tek satır olduğu için
ölçülmemiş sayıldı.

**Kalan iş bu yüzden bir koşum değil bir karar:** canlı yarı CI'ya girecek mi
(altın örnek yükleyen bir iş, `sigma-explain`'e ek adım), yoksa faz kapanışında
elle koşturulan bir ölçüm olarak mı kalacak? Bugünkü hâl ikincisi ve **yazılı
değildi**.

Beyan dosyası bugün 19 kalem taşıyor (`catalog/sigma/expectations.json`):
**7 `at_least_one` · 12 `none`** (10'u `corpus_gap`, 2'si `invariant`). Vault
sayfası *"sekiz beyanın sekizi"* diyor — sayı o koşumdan bu yana **oynadı**, ve
bu ayrı bir bulgu: canlı yarı bir kapı olmadığı için sayının oynaması hiçbir
yerde kırmızı yanmıyor.

Yerelde koşan üç çevrimdışı kapı (hepsi bu makinede, konteyner yok):
`ruleset --verify` → *24 kural çiviyle birebir aynı* · `view_columns --check` →
*göçlerle birebir aynı* · `compile --check` → *birebir aynı*.

### Kalan kalemler — üçü ayrı sınıfta

| Kalem | Sınıf | Not |
| --- | --- | --- |
| Kapı 3'ün canlı yarısının **kapı olması** | **karar** | Koşum yapılmış (yukarıdaki düzeltme); açık olan CI'ya girip girmeyeceği |
| Kural başına **duvar saati** maliyeti | **sessiz makine** | Bir **kayıt**, kapı değil: `t32-derleme-tasarimi` §6 ölçekleme sorusunu saatsiz cevaplamış (karesel deyim, 24 kuralda görünmüyor). Bu makinede bugün ölçülemez |
| Korpus: `ruleset_commit` = `t30-ornekleminden-terfi` | **karar → VERİLDİ** | Aşağıdaki bölüme bakın: çivili örneklem **kalıyor**, ama kapsamın iki iddiası ayrıldı |

Üçüncüsü bu denetimin **beklemediği** bulgusuydu: T32'nin açık kalmasının sebebi
yalnızca duvar saati değil. Duvar saati bir kayıt; korpus bir **karar**; Kapı
3'ün canlı yarısı bir **koşum**. Üçü tek satıra *"ölçüm bekliyor"* diye
yazıldığında ikisi görünmez oluyordu.

### Korpus kararı ve kapsamın İKİ iddiası (2026-09-15)

**Karar: determinizm kapısının korpusu terfi ettirilmiş örneklem olarak
kalıyor.** Gerekçe kapının kendi işi — yukarı akışa bağlı bir korpus, upstream
her hareket ettiğinde kapıyı **oynatır**, ve oynayan bir kapı determinizm
ölçmüyor, **upstream'in hareketini** ölçüyor. Çivili korpus doğru korpustur.

**Ama kapsam tek satırda iki ayrı iddia taşıyordu** ve ikisi bugün farklı
hâlde:

| İddia | Hâli | Nasıl |
| --- | --- | --- |
| *"Derleme deterministik ve tekrarlanabilir"* | **ölçüldü** | Çivili korpusta `compile --check` → birebir aynı |
| *"SigmaHQ'nun yayımlanmış kural setini derleyebiliyoruz"* | **ölçülmedi** | Hiçbir koşum bunu sınamıyor. Bugün derlenen küme T30'un terfi ettirilmiş örneklemi |

İkincisi bu ticket'ın kabul kriterlerine **eklenmiyor** — T32 derleme hattını
kuruyor ve kurdu; yayımlanmış setin derlenebilirliği hattın değil **korpusun**
sorusu. F3'ün ölçülmemiş kalemleri arasında duracak.

**Bu ayrımın kendisi bir desendir ve bugün üçüncü kez görüldü:** bir kriter
cümlesi iki iddia taşıdığında ölçüm birini görüyor ve **yeşil ikisini temsil
ediyor**. Diğer iki örnek: T62'nin sağlık kontrolü (*"süreç ayakta"* ↔
*"bağımlılıklar hazır"*) ve T63'ün dayanıklılık kriteri (`kill -9` yazılan
baytları sayfa önbelleğinden silmiyor, yani `FlushToDisk` açık ve kapalı koşumlar
aynı sonucu veriyor — süreç ölümü fsync'i **ölçemiyor**).

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
