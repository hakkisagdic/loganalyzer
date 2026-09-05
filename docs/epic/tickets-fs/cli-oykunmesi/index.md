---
title: "S06 — N3: CLI öykünmesi"
kind: ticket
status: 2
---

# S06 — N3: CLI öykünmesi

**Bağımlılık:** S03 · **Sonraki:** — (FS-b)

## Amaç

Etkileşimli kabuk: prompt, sayfalama, vendor hata mesajları.

S03'ün sunucusu komuta cevap veriyor; bu ticket onu **cihaz gibi** konuşturuyor.
Fark önemsiz görünüyor ama toplayıcının en kırılgan yolu burada: gerçek bir ASA
`--More--` basar ve toplayıcı onu görmezse çıktının yarısını alır — **hatasız**.

## Kapsam

### İçinde

- Prompt (`hostname#`, `hostname>`), enable moduna geçiş.
- **Sayfalama**: `--More--` ve `terminal length 0` ile kapatılması.
- Vendor'ın kendi hata mesajları — `% Invalid input detected`,
  `command parse error` gibi. Bizim ürettiğimiz genel bir hata değil.

### Dışında

- Vendor sürüm farkları. FortiGate 7.2 ile 7.4 aynı komuta farklı cevap
  veriyor olabilir; simülatör **bizim yazdığımız** biçimi üretir, yani
  toplayıcıyı sınar, **vendor'ı değil**. Bir müşteri cihazından alınan tek bir
  gerçek çıktı, on senaryodan çok şey söyler (FS §11).

## Kabul kriterleri

- Toplayıcı **sayfalama açıkken de** doğru çıktı alıyor — ya da **alamadığı
  yazılı**.

  İkinci şıkkın kabul kriterinde durması bilinçli: bugün toplayıcının o yolu
  taşıyıp taşımadığı bilinmiyor. "Çalışıyor" diye yazıp sonra çalışmadığını
  bulmak, bu depoda en pahalı hata sınıfı.
- Vendor hata mesajı `DeviceCommandResult`'ta **ayırt edilebilir** — genel bir
  başarısızlığa düşmüyor. "Komut yanlış" ile "cihaz cevap vermedi" farklı
  şeyler ve ikisi tek değere inerse teşhis kaybolur.

## Sonuç — birinci kriter **ikinci şıkla** kapandı

> Toplayıcı sayfalama açıkken doğru çıktı **almıyor**. Ve almadığını gösteren
> hiçbir belirti üretmiyor.

Kabul kriteri bu şıkkı bir çıkış değil bir **yükümlülük** olarak yazmıştı;
aşağısı o yükümlülüğün ödenmiş hâli.

### Ölçülen

`SIM_PAGING=daima` ile koşan bir N3 container'ında, `FortiGateCollector`'ın
komutlarıyla yapılan çekim:

| Alan | Değer |
| --- | --- |
| `DeviceCommandResult.Ok` | **`true`** |
| `DeviceCommandResult.Error` | **boş** |
| `DeviceCommandResult.Failure` | **`None`** |
| Alınan config | **yarım** — ve içinde `--More--` imleci |

Ürün tarafında hiçbir şey yanlış gitmiş görünmüyor. §7'nin sınıfı: hata yok,
sayaç yok, belirti yok.

Kaybın büyüklüğü ayrıca kayda değer. Yarım config, fark motoruna **silinmiş
yüzlerce satır** gibi görünüyor — yani sessiz kayıp sessiz kalmıyor, *yanlış
bir alarma* dönüşüyor. Üstüne `--More--` satırı normalize ediciye bir config
satırı olarak giriyor.

### Mekanizma — ve neden bu bir "öykünme bozuk" bulgusu değil

`SshDeviceTransport` her komut için `CreateCommand` çağırıyor: **her komut ayrı
bir exec kanalı**, yani gerçek bir cihazda da ayrı bir oturum. Toplayıcının
ilk komutu (`config system console / set output standard / end`,
`terminal pager 0`) **oturuma ait bir ayar** yazıyor ve o kanal kapanınca ayar
ölüyor. İkinci kanal varsayılan ayarlarla açılıyor.

Yani hazırlık komutu **yanlış değil**; gönderilme **biçimi** yanlış. İki test
bu ayrımı çiviliyor:

- `Sayfalama_kapatma_komutu_ikinci_exec_kanalina_tasinmiyor` — hazırlık komutu
gönderilen çekim ile hiç gönderilmeyen çekim **aynı** uzunlukta çıktı veriyor.
- `Etkilesimli_oturumda_sayfalama_kapatma_calisiyor` — **aynı oturumda**
gönderildiğinde sayfalama gerçekten kapanıyor.

İkincisi olmasaydı bulgu *"öykünme sayfalamayı kapatmayı bilmiyor"* diye de
okunabilirdi ve okuyan kişi simülatörün peşine düşerdi — S04'ün *yanlış yüzey*
dersinin aynısı.

### Ne yapılmadı, bilerek

**Ürün düzeltilmedi.** Düzeltmesi iki yoldan biri: (a) bütün komutları tek bir
exec kanalında birleştirmek, (b) kabuk kanalına geçmek. İkisi de
`SshDeviceTransport`'un çekim modelini değiştiriyor, ikisinin de kendi kabul
kriterleri var (komut sınırlarının ayrı ayrı raporlanması (a)'da kayboluyor;
(b) prompt beklemeyi ürüne sokuyor), ve bu ticket'ın kapsamı *"toplayıcı doğru
koşuyor mu"* sorusunu **cevaplamak** — cevabı beğenmezsek diye ayrı bir kalem
açmak (S06 kapsamı: kabuk davranışını öykünmek).

Kapsamı kendi başıma genişletmek yerine bulguyu kaydediyorum; kalemi
koordinatör açar.

### Sayfalamanın exec kanalındaki varsayılanı bir **model**, ölçüm değil

`SIM_PAGING` varsayılanı `etkilesimli`: kabuk oturumunda sayfalıyor, exec
kanalında sayfalamıyor. Gerekçe, exec kanalında PTY olmaması ve cihazların
çoğunun PTY yokluğunda sayfalamayı uygulamaması — ama bu **ölçülmüş bir vendor
olgusu değil**, modellenmiş bir varsayım. FS §11'in *"vendor sürüm farkları"*
kalemine giriyor ve bir müşteri cihazından alınacak tek bir gerçek çıktı bunu
ya doğrular ya çürütür.

Varsayılanın böyle olması S03'ün testlerini de olduğu gibi bırakıyor: onlar
sayfalamayan bir cihazı sınıyorlardı ve sınamaya devam ediyorlar.

## Yan bulgu — senaryo seçimi hiç çalışmamış

Ticket'ın kapsamında değildi; öykünmenin oturum ayarlarına ihtiyaç duyması
sırasında çıktı.

`SIM_SCENARIO` compose'da ayarlanıyor ve yorumu *"fark testleri bunu değiştirip
container'ı yeniden başlatıyor"* diyor. **Hiçbir zaman çalışmamış:** OpenSSH
oturum çocuğuna temiz bir ortam kuruyor ve `SIM_*` gibi keyfi değişkenler
sshd'nin ortamında dursa bile `ForceCommand`'a ulaşmıyor. S03 bunu profil için
biliyordu (`/sim-profil` dosyasına yazıyordu); senaryo ortamdan okunmaya devam
ediyordu.

Sonuç yine aynı sınıf: dağıtıcı **her koşumda baseline** veriyor, hiçbir şey
şikâyet etmiyor, ve *"fark yok"* sonucu doğru sanılıyor. Hiçbir test
`SIM_SCENARIO` ayarlamadığı için de bugüne kadar görünmedi.

Açılış artık bütün ayarları `/sim-ayarlar` dosyasına yazıyor ve bir bekçi
(`SshSimulatorCliTests.Dagiticinin_okudugu_her_ayar_acilista_yaziliyor`)
dağıtıcının okuduğu her `SIM_*` değişkeninin orada yazıldığını **Docker'sız**
sınıyor.

## Neden bu ticket ertelenebilir ama silinemez

FS-b'de. FS-a (S01–S05) ürünün gerçek cihaz olmadan koşmasını sağlıyor; bu
ticket **toplayıcının doğru koştuğunu** sağlıyor. İkisi ayrı sorular.

Silinemez çünkü bugün cevabı olmayan bir soruyu açık tutuyor: toplayıcı
sayfalamayı biliyor mu? Ticket kapanmadan cevap **yok**, ve yokluğu kayıtlı.
