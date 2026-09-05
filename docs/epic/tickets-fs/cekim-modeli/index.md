---
title: "S08 — Çekim modelinin sayfalama kusuru ve teşhisin düzleşmesi"
kind: ticket
status: 2
---

# S08 — Çekim modelinin sayfalama kusuru ve teşhisin düzleşmesi

**Bağımlılık:** S06 · **Önceki:** — (FS-b'nin kapanışı)

Kusur [S06](../cli-oykunmesi/index.md)'da **bulundu ve düzeltilmedi** — kapsamı
tek başına genişletmemek için. Bu ticket kalemi açıyor.

## Kalem 1 — sessiz kayıp

### Mekanizma

`SshDeviceTransport.Execute` her komut için `CreateCommand` çağırıyor →
**her komut ayrı bir exec kanalı** → gerçek bir cihazda da ayrı bir oturum.
Toplayıcının sayfalama kapatma komutu **oturuma ait bir ayar** yazıyor ve o
kanal kapanınca ayar ölüyor. İkinci kanal varsayılan ayarlarla açılıyor.

Sonuç: `Ok=true`, `Error` boş, `Failure=None`, config **yarım**, içinde
`--More--`. §7'nin sınıfının en pahalı biçimi — çünkü sessiz kalmıyor:
fark motoru eksik satırları **silinmiş yüzlerce satır** diye okuyor. Kusur
kendisi hakkında susuyor, **başka bir şey hakkında yalan söylüyor**.

### Seçilen yol ve neden

Ticket iki yol veriyordu. Üçüncüsü seçildi ve **ikisinden de ucuz**:

| Yol | Bedeli | Karar |
| --- | --- | --- |
| Bütün komutları tek kanalda birleştirmek | Komut sınırlarını çıktıdan ayırmak gerekiyor | ⬜ |
| Kabuk kanalına geçmek | PTY, istem eşleşmesi, zaman aşımı — vendor'a göre değişen yüzey | ⬜ |
| **Her komutu kendi kendine yeten tek bir oturum yapmak** | Sözleşme değişikliği, taşıma değişmiyor | ✅ |

İki ölçüm bu kararı verdirdi:

1. **Komut sınırları çıktıda zaten korunmuyordu.** `Execute` bütün komutların
çıktısını **tek bir `StringBuilder`'a** ekliyor ve `ConfigNormalizer` hepsini
birlikte alıyor. Yani birinci yolun "bedeli" diye yazılan şeyin yarısı zaten
ödenmişti; kanal ayrımının koruduğu tek şey **çıkış kodunun hangi komuta ait
olduğu**.

2. **Çok satırlı tek komut deseni depoda zaten vardı.** `FortiGateCollector`
sayfalama kapatmayı üç satırlık **tek bir dize** olarak gönderiyordu
(`config system console\nset output standard\nend`). Yani ürün çok satırlı
exec dizesinin çalıştığına zaten bahis oynamıştı; `show`'u o dizeye almak yeni
bir bahis değil, **var olan kararı tamamlamak**.

Değişiklik `IConfigCollector.Commands`'ın sözleşmesinde:

> Her eleman **kendi oturumunda** koşuyor ve bu yüzden **kendi kendine yetmek
> zorunda**. Bir elemanda yazılan oturum ayarı bir sonrakine taşınmıyor.

Bu sözleşme S08'e kadar **yazılı değildi** ve ihlali sessizdi. Birden çok
eleman hâlâ meşru — birbirinden bağımsız okumalar için. Ölçüt: *ikinci eleman
birincinin yazdığı bir ayara dayanıyorsa ikisi tek eleman olmalı.*

### Atfetme kayboldu mu

Kısmen, ve yerine daha iyisi geldi. Çıkış kodu artık oturumun tamamına ait,
yani "hangi komut reddedildi" sorusunu kod cevaplamıyor. Cevabı **vendor'ın
kendi metni** veriyor (`% Invalid input detected…`, `command parse error before
'…'`) ve o metin S06'dan beri `DeviceCommandResult.Error`'a taşınıyor.

Yani atfetme sentetik olmaktan çıkıp **cihazın kendi cümlesine** dayandı.

## Kalem 2 — teşhis servis kapısında düzleşiyordu

S06 taşıma katmanında `DeviceFailureKind`'ı ayırmıştı ama `ConfigCapture`
onu **taşımıyordu**: cihaz reddi ile ulaşılamama `DeviceConfigService`
çıkışında yeniden tek bir metne düşüyordu. Ayrımı yapmak için harcanan iş
ekrana giden yolda geri alınıyordu.

`ConfigCapture` artık `DeviceFailureKind` taşıyor ve servis kapısı onu
**yeniden yorumlamıyor, aktarıyor** — yorumlasaydı iki yerde iki sınıflandırma
doğardı (§9).

Yeni bir değer eklendi: `Unsupported` — *bu vendor için toplayıcı yok*. Diğer
dördünden ayrı bir aile, çünkü cihaza **hiç bağlanılmadı** ve operatörün
yapacağı iş farklı: ağ ekibini aramak yerine desteklenen vendor listesine
bakmak.

### Redaksiyon — aradım, elemedim

Cihazın kendi hata metni artık `Error`'a giriyor, yani ekrana ve veritabanına
giden bir yola. `SshDeviceTransport`'un sınıf yorumu *"ikinci savunma katmanı
T25'in servis kapısında"* diyordu — **ve o iddia okunmamıştı**.

Okundu: iki yol da redaksiyondan geçiyor. `ChangeConnectorService.TestAsync`
sonucu `Clean`'den geçiriyor, `ChangeConnectorScheduler` de kalıcılaştırmadan
önce aynı şeyi yapıyor ve yorumunda gerekçesi yazılı. İddia **doğruymuş** —
ama doğru olduğu ancak şimdi ölçüldü.

## Kalem 3 — `Bizigo.Api` referansının bedeli **yokmuş**

S07'de bu referans eklenmemişti ve gerekçe ölçülmemiş bir varsayımdı:
`Bizigo.IntegrationTests` on altı proje referansı taşıyor ve `src/` altındaki
**tek dışlanan** proje `Bizigo.Api`'ydi. Dışlama *bilinçli görünüyordu*, ama
gerekçesi hiçbir yerde yazılı değildi.

Ölçüldü: referans eklendi, bir sonda dosyası `typeof(Program)` yazdı, derleme
**0 hata** verdi. `CS0433` yok. M01'in ölçümü `Bizigo.Mcp`'de alınmıştı ve
buraya taşınmıyor.

**Ölçümün kendisi iki adımlı olmak zorunda kaldı** ve sebebi §6'nın yeni
maddesi: ilk koşum sondayı **hiç derlememişti** — MSBuild artımlı derlemede
dosyayı atladı ve sonuç "0 hata" göründü, yani *"çakışma yok"* ile *"ölçüm
yapılmadı"* aynı çıktıyı verdi. Yordam düzeltildi:

1. Sondaya **kasıtlı bir derleme hatası** kondu → 8 hata → dosya gerçekten
derleniyor.
2. Sonda `typeof(Program)` ile değiştirildi → 0 hata → çakışma yok.

Gerekçe artık `.csproj` yorumunda yazılı — bir sonraki kişi "bilinçli
görünüyor" demek zorunda kalmasın diye. Ve buluşma noktası testi yazıldı:
`WebhookGeneratorDeliveryTests`.

## Ölçülen bekçiler

Yordam: kusuru uygula → **dosyada olduğunu iddia et** → koştur → **yedek
dosyadan** geri al.

| Kusur | Kırmızı yanan |
| --- | --- |
| Toplayıcı komutları yine iki ayrı elemana bölünüyor | `Toplayicinin_her_komutu_kendi_kendine_yetiyor` |
| Öykünme hiçbir vendor'da sayfalamıyor | `Sayfalama_kapatilmazsa_ayni_komut_yarim_cikti_veriyor` |
| Servis kapısı türü taşımak yerine sabitliyor | `Basarisizlik_turu_servis_kapisinda_kaybolmuyor` (3/4) |

İlk ikisi **Docker'sız** koşuyor: `vendor-cli` saf bash ve tek başına kaynak
alınabiliyor, yani S08'in düzeltmesi konteyner beklemeden korunuyor.

## CI'ın yakaladıkları — yedi test, üç ayrı kök

Bu ticket'ın testleri Docker ölüyken yazıldı ve **koşturulamadı**; ilk koşumları
CI'da oldu ve **yedisi düştü**. Aşağısı sebeplerin kaydı, çünkü üçü de ayrı
sınıf ve ikisi **kendi iddialarımın** yanlış olmasıydı.

### 1 · Vendor hata metni stdout'a kaydı — **bu ticket'ın kendi regresyonu**

S08 durum makinesini `cli_komut_isle`'e çıkarırken S06'nın
`cli_hata … >&2` **yönlendirmesi düştü**. `SshDeviceTransport`
`DeviceCommandResult.Error`'ı stderr'den besliyor, yani:

- `Error` boş kaldı,
- taşıma kendi sentetik cümlesine düştü (`'…' komutu 127 koduyla döndü`),
- ve S06'nın ikinci kabul kriteri — *vendor'ın kendi metninin ürüne
ulaşması* — **sessizce geçersiz oldu.**

Çıkış kodu `127` yanıltıcı okundu: *"komut bulunamadı"* değil, dağıtıcının
**kendi** `exit 127`'si. Kaynaktan izlenebilir bir olguydu.

**Neden hiçbir bekçi görmedi — asıl ders bu.** Birim testlerinin `Bash`
yardımcısı **yalnızca stdout okuyup stderr'i atıyordu**. İki akış onun için tek
bir akıştı, yani metnin *varlığını* sınayan testler geçmeye devam etti —
sınadıkları şey **ürünün okuduğu şey değildi**. Kapı vardı ve **yanlış yere
bakıyordu**; §7'nin *"sessizce atlayan bekçi"* sınıfının bir akrabası.

Yardımcı artık stderr'i ayrı döndürüyor ve yeni bekçi **çift taraflı** iddia
ediyor: metin stderr'de **var**, stdout'ta **yok**. Tek taraflı iddia aynı
körlüğü yeniden üretirdi. Kırmızı yandığı ölçüldü — CI'daki kusur birebir geri
konup üç vaka da düştü.

### 2 · Etkileşimli kabuk testleri boş dize okuyordu — **test hatası**

Dört testin hepsi `""` gördü, banner dâhil. Sebep container değil, **okuma
biçimi**: `ShellStream.Read()` **bloke olmayan** bir çağrı ve tamponda o an ne
varsa onu döndürüyor. Komut yazıldıktan hemen sonra sunucu daha cevap vermemiş
oluyor, `Read()` boş dönüyor ve döngü ilk turda kırılıyordu.

Yani testler container'ı, betikleri ya da imajı değil **kendi okuma hatalarını**
ölçüyordu — ve `Assert.Contains` boş bir dizede düştüğü için arıza *"öykünme
hiçbir şey basmıyor"* gibi okunuyordu.

Artık `Expect` ile bekleniyor. Bu **anlamsal** bir bekleme, sabit bir uyku
değil (*"şu metin gelene kadar"*), yani §6'nın *duvar saati* kuralı korunuyor:
bütçe bir ölçüt değil, oturum tıkandığında koşumu kilitlememek için bir tavan.

### 3 · Hedef kimliği ad alanlı geliyor — **iddia yanlıştı, ürün doğruydu**

`WebhookGeneratorDeliveryTests` çıplak `fw-ankara-01` bekliyordu; gelen
`bizigo/fw-ankara-01` ve `net/fw-ankara-01` oldu.

Eşleme GitHub'ın `$.repository.full_name` ve GitLab'ın
`$.project.path_with_namespace` alanlarını **kırpmadan** alıyor, ve gerçek
yüklerde o alanlar **zaten ad alanlı** (`bizigo/network-config`,
`net/fw-config`). Yani üreteç sağlayıcıya sadık ve çıplak ad bekleyen iddia,
**gerçek bir GitHub teslimatının da düşeceği** bir iddiaydı.

S07'nin yönü burada da geçerli: *gerçek yük ne diyorsa o.* Aynı desenin
üçüncü örneği — Jenkins idempotansı, `Bizigo.Api` referansı, ve şimdi bu.

### Ne öğrenildi

Üç kökün ikisi **testin kendisindeydi**, biri üründe. Ortak nokta: hiçbiri
Docker'sız görülemezdi *sanılmıştı* — oysa birincisi görülebilirdi ve bekçi
yanlış akışa baktığı için görülmedi. Docker'ın yokluğu ölçümü imkânsız
kılmadı; **ölçümün yanlış yere bakması** kıldı.

## Öykünmenin sustuğu yer — ve neden susması doğru

Bekçi yazılırken RouterOS düştü: `/export terse` sayfalama kapatma komutu
**taşımıyor** ve öykünme her vendor'da sayfalıyordu, yani MikroTik toplayıcısı
tanım gereği geçilemez hâldeydi.

İki okuma vardı ve ikisi de gerekçesiz seçilemezdi:

- RouterOS `/export terse` çıktısını sayfalamıyor → toplayıcı doğru.
- Sayfalıyor → aynı kusurun ikinci örneği.

**Hangisi olduğunu bilmiyoruz** ve bilmediğimiz bir vendor olgusunu taklit
etmek FS §11'in yasakladığı şey. Öykünme artık yalnızca **sayfalamayı kapatan
komutunu modellediğimiz** vendor'larda sayfalıyor (`fortinet`, `cisco`);
RouterOS'ta **susuyor**, ve sustuğu betikte yazılı.

Sessizliğin bir bedeli var: sayfalamayan bir vendor'da *"kesilmemiş çıktı"*
iddiası boş geçiyor. Bu yüzden asıl sözleşme ayrıca ve doğrudan iddia
ediliyor — *sayfalayan bir vendor'ın config okuyan komutu, sayfalamayı aynı
komutta kapatmak zorunda.*

**Açık kalem:** gerçek bir RouterOS cihazından alınacak tek bir `/export terse`
çıktısı bu soruyu kapatır.
