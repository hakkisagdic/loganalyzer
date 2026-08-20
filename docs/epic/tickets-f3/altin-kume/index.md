---
title: "T38 — Altın küme ve inceleme akışı"
kind: ticket
status: 1
---

# T38 — Altın küme ve inceleme akışı

**Bağımlılık:** T33, T37 · **Sonraki:** —

## Amaç

Kalitenin **ölçülebilir** olması. Altın küme olmadan F4'ün LLM'i iyi mi kötü mü
bilinemez.

## Neden ayrı bir ticket

RCA artifact'ının 2. riski: *"kimse 'doğru muydu?' düğmesine basmazsa altın küme*
*boş kalır ve kalite ölçülemez."* Bu bir arayüz ayrıntısı değil, F4'ün tüm kalite
ölçümünün dayanağı.

## Ticket yazıldığında var olmayan şey

Kodu okuyunca çıktı ve ticket'ın merkezindeki kriteri havada bırakıyordu:

> Alarm kapatan kullanıcı inceleme adımını **atlayamıyor**.

**Depoda "alarm kapatma" diye bir akış yok.** `AlertTriggerEntity` bir
ateşle-unut geçmiş kaydı: `FiredAt`, pencere, `Value`, `Threshold`, `SourceId`,
`OwnerGroup`, `Summary`. Durum yok, sahiplenme yok, kapatan kişi yok. Kapatma
yoksa "kapatırken atlayamama" da yok.

İkinci bulgu aynı sınıftan: `EvidenceBundleEntity`'de **`owner_group` kolonu
yok** — kapsam yalnızca JSON gövdesindeki `BundleScope`'ta, yani
**sorgulanamaz** bir yerde. *"Bir ekip başka ekibin incelemesini görmüyor"*
kriteri için inceleme kaydı kendi `owner_group`'unu taşımak zorunda; pakete
`JOIN` atıp JSON açmak kapsam kapısını sorgu planının insafına bırakırdı.

## Verilen kararlar

Beşi de koordinatörle karara bağlandı. Gerekçeleri burada duruyor çünkü ikisi
kapsam genişletiyor ve biri bir maliyeti bilerek kabul ediyor.

### 1 · Minimum alarm yaşam döngüsü **bu ticket'ta**

`AlertTriggerEntity`'ye durum, kapatan, kapatma zamanı ve inceleme bağı
ekleniyor.

Alternatifleri kötüydü: ayrı bir ticket T38'i ona bağımlı kılardı ve ikisi de
bu fazda bitmezdi; kapatma akışını F4'e ertelemek T38'in vaadini boşaltırdı.
Aynı mantıkla T37'de de karar verildi — T36'nın kabul kriteri aksi hâlde hiç
**gösterilemeyecekti**.

**Dar tutuluyor:** durum + kapatan + kapatma zamanı + inceleme bağı. Atama,
eskalasyon, susturma **yok**.

### 2 · `bundle_id` zorunlu, `trigger_id` isteğe bağlı

F4'ün karşılaştırması pakete bakacak; paketsiz bir inceleme F4'te
**ölçülemez**. Bu yüzden paket bağı zorunlu.

`trigger_id` isteğe bağlı çünkü kullanıcı tetikli RCA'da alarm yok.

**Kapatma anında paket yoksa kapatma paket üretimini tetikler.** T37'nin elle
tetiklemesiyle **aynı mekanizma** kullanılır — ikinci bir üretim yolu
yazılmaz (§9).

Bedeli açık ve kabul edildi: kapatma artık ucuz bir işlem değil. Kullanıcı
"kapat" derken bir şeyin üretildiğini **bilmeli** — ekranda görünmesi gerekiyor.

### 3 · İnceleme kaydı ilişkisel, `schema_version` taşıyor

T36 kanıt paketi için **belge** yolunu seçmişti ve gerekçesi doğruydu: paket
bütün olarak okunuyor, ilişkisel bir alt tablo her göçte geçmiş satırlara
`NULL` kolon ekleyip eski paketleri sessizce farklı bir şekle sokardı.

İnceleme kaydı için aynı gerekçe **geçerli değil**: inceleme alan üzerinde
**toplanıyor**. Kalite göstergesi doğruluk oranı hesaplıyorsa o alanlar kolon
olmalı; belge yolunda her gösterge sorgusu JSON açardı.

`schema_version` yine de taşınıyor — F4 alanları göçle eklenecek ve eski
satırların hangi şemayla yazıldığı bilinmeli.

**Çelişen kanıt alanı bugün açılıyor.** Sonradan eklenirse geçmiş kayıtlar onu
taşımaz ve altın kümenin en eski yarısı bu boyutta kör kalır.

### 4 · Kalite göstergesi rapor ekranının köşesinde — yeni ekran yok

Gösterge tek başına bir ekranı hak etmiyor ve T28 denetimine yeni yüzey eklemenin
bedeli var. `/rca` ekranına giriyor.

**Küme boşken sayı görünüyor, gizlenmiyor.** Gizlenen bir sıfır, *"henüz
ölçülmedi"* ile *"ölçüldü, sıfır"* arasındaki farkı siler.

Bu ticket **veri ve sözleşme** tarafını yazıyor; ekran yerleşimi T37'de.

### 5 · "Bilmiyorum" bir karar, ve ayrı sayılıyor

Zorunluluk kalıyor (atlanamıyor), ama seçeneklerden biri **bilmiyorum**.

Gerekçe: zorunlu bir soruda kaçış yoksa insanlar rastgele seçip geçer ve altın
küme sessizce gürültüyle dolar. Bu, ölçülemezlikten **kötü** — çünkü
ölçülüyormuş gibi görünür (§7).

"Bilmiyorum" doğruluk oranının **paydasına girmiyor**. Ayrıca kendisi bir
gösterge: oranı yüksekse ya kanıt paketi yetersiz ya soru yanlış soruluyor.
Kalite göstergesinin **üçüncü sayısı** bu.

## Kapsam

### İçinde

- **Altın küme deposu (ilişkisel):** `bundle_id` (zorunlu), `trigger_id`
  (isteğe bağlı), `owner_group` (kendi kolonu), insan kararı
  (doğru / yanlış / eksik / bilmiyorum), çelişen kanıt kararı, serbest not,
  inceleyen, zaman, `schema_version`.
- **Minimum alarm yaşam döngüsü:** `AlertTriggerEntity`'ye durum + kapatan +
  kapatma zamanı + inceleme bağı.
- **Zorunlu inceleme:** alarm tetikli RCA'da, alarmı **kapatma akışının zorunlu
  parçası**. Kullanıcı zaten oradadır; ayrı bir "geri bildirim ver" adımı hiç
  kullanılmaz.
- Kullanıcı tetikli RCA'da inceleme **isteğe bağlı** — zorlamak orada kullanıcıyı
  kaçırır.
- **Kapatma, paket yoksa paket üretimini tetikler** (T37'nin mekanizmasıyla).
- **Kalite göstergesi:** kayıt sayısı, doğruluk oranı, "bilmiyorum" oranı —
  kapsam altında, üçü de.

### Dışında

- Ekran yerleşimi ve etkileşim — T37 (4).
- LLM çıktısının değerlendirilmesi — F4. Ama depo şeması onu **taşıyabilmeli**:
  F4 aynı olay için model çıktısını ekleyecek ve insan kararıyla
  karşılaştıracak.
- Atama, eskalasyon, alarm susturma — yaşam döngüsü bilerek dar.

## Kabul kriterleri

Alarm kapatan kullanıcı inceleme adımını atlayamıyorİnceleme kaydı paketsiz yazılamıyor; kapatma anında paket yoksa üretim tetikleniyorBir ekip başka ekibin incelemesini görmüyor — kapsam kolondan, JSON'dan değil"Bilmiyorum" kaydedilebiliyor ve doğruluk oranının paydasına girmiyorÇelişen kanıt kararı bugünden itibaren her kayıtta taşınıyorKalite göstergesi üç sayıyı veriyor ve küme boşken de görünüyorKayıtlar F4'ün ihtiyacı olan şekli taşıyor: aynı kanıt paketi + insan kararı, sonradan model çıktısı eklenebilir

## Bekçiler

§6: her biri **kırmızı yanabildiği ölçülerek** teslim edilir.

| Bekçi | Ne kanıtlar | Konteyner? |
| --- | --- | --- |
| `Paketsiz_inceleme_yazilamiyor` | 2. kararın zorlaması | ❌ |
| `Kapatma_paket_yoksa_uretimi_tetikliyor` | İkinci üretim yolu yok; T37'nin mekanizması çağrılıyor | ❌ |
| `Baska_grubun_incelemesi_gorunmuyor` | Kapsam kolonu ve `IScopedQuery` kapısı | ✅ **koordinatör** (`ScopeNegativeTests` genişletmesi) |
| `Bilmiyorum_dogruluk_oranina_girmiyor` | 5. kararın aritmetiği | ❌ |
| `Kume_bosken_sayi_gorunuyor` | Gizlenen sıfır yok | ❌ |
| `Inceleme_semasi_surumu_tasiniyor` | F4 göçü geldiğinde eski satırlar tanınabilir | ❌ |

## Notlar

RCA artifact'ının 5. riski de buraya bakıyor: *"çelişen kanıt tiyatrosu"* —
model, çelişen kanıt alanını doldurmak için önemsiz bir şey uydurabilir. Altın
küme şeması bunu ayrıca ölçebilmeli, yani "çelişen kanıt doğru muydu?" ayrı bir
alan olmalı. F4'te kullanılacak ama alanın bugün açılması gerekiyor —
sonradan eklenirse geçmiş kayıtlar onu taşımaz.
