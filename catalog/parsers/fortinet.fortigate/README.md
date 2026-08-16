# Fortinet FortiGate

| Parser | OCSF sınıfı | Kapsam |
| --- | --- | --- |
| `fortinet.fortigate.traffic` (`traffic.yaml`) | 4001 Network Activity | `type="traffic"` (forward/local/multicast/sniffer) ve `type="utm"` (webfilter/ips/virus/app-ctrl/dns/ssl) |
| `fortinet.fortigate.event` (`event.yaml`) | 3002 Authentication | `type="event"` içinde yalnızca kimlik doğrulama: `Admin login successful/failed`, `Authentication success/failure/logon` |

`type="event"` altındaki diğer aileler (SD-WAN, FortiSwitch, IPsec, REST API,
FortiExtender…) **kapsam dışı**: her birinin OCSF sınıfı farklı ve `map` bloğu
dallanamıyor. F1'in hedefi motoru doğrulamak; kataloğun tamamı F2'nin editörüyle
gelecek.

## Zaman damgası

FortiOS aynı olayın zamanını üç ayrı biçimde yazıyor:

| Alan | Gerçek değer | Kullanım |
| --- | --- | --- |
| `eventtime=1557513467369913239` | Epoch, **nanosaniye** (7.x) veya saniye (6.x) | **Kullanılan kaynak bu.** `UNIX_AUTO` ölçeği basamak sayısından çıkarıyor. Mutlak epoch olduğu için saat dilimi tahmini gerekmiyor. |
| `date=` + `time=` | `2020-04-23` + `12:17:48` | Cihazın YEREL saati; hangi dilimde olduğunu satır söylemiyor. `fields` içinde duruyor, olay zamanı için kullanılmıyor. |
| `tz="-0500"` | Sayısal UTC ofseti | `fields` içinde duruyor. `timezone_field` artık sayısal ofseti çözüyor, ama `eventtime` varken gerekmiyor. |

Örneklerdeki 22 satırın **22'sinde** `eventtime` var (4'ü saniye, 18'i
nanosaniye). Yazmayan bir satır gelirse `_fortigate_no_eventtime` etiketiyle
`partial` kalır — sessizce yanlış zaman yazmak yerine görünür bir eksiklik.

Bu bölüm eskiden üç maddelik bir "hiçbiri kullanılamıyor" listesiydi. `UNIX_NS`/
`UNIX_AUTO` ve `timezone_field` ofset desteği geldikten sonra hem `date`/`time`
birleştirme hilesi hem de `Europe/Istanbul` varsayımı kalktı. İki FortiOS 7.4
satırı da `partial`den `ok`'a döndü.

## Ayırt edicilik

`match.contains` dispatcher'ın yalnızca 2. kademesinde çalışıyor — envantere
bağlı kaynak (kademe 1) ön filtreyi hiç görmüyor, gömülü `tests` bloğu da
parser'ı doğrudan çağırıyor. Bu yüzden her iki parser'ın boru hattı bir **kapı
adımıyla** başlıyor (`type="(traffic|utm)"` / `logdesc="..."`). Kapı olmasaydı
FortiGate'in her satırı her iki parser'dan da `ok` dönerdi.

## Örnek dosyalar

`samples/traffic.log` (13 satır) · `samples/event.log` (9 satır)

Satırlar FortiOS 6.2/6.4/7.2/7.4 gerçek çıktısı; kaynak, Fortinet'in log mesaj
referansındaki örnekler ve Elastic'in `fortinet_fortigate` entegrasyonunun boru
hattı test verisidir. Genel IP'ler RFC 5737 belge aralıklarına taşındı; alan
sayısı, sıra, tırnak kullanımı ve alan uzunlukları korundu.

Örnek kümesi bilinçli olarak "çirkin" satırlar içeriyor:

* `<185> date=...` — PRI'den sonra boşluk
* `<185>eventtime=...` — `date`/`time` alanı hiç yok
* `user="${exploit_user_name"` — şablon enjeksiyonu denemesi taşıyan kullanıcı adı
* `msg="URL belongs to a denied category in policy"` — tırnak içinde boşluk
