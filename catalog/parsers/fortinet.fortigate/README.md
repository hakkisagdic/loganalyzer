# Fortinet FortiGate

| Parser | OCSF sınıfı | Kapsam |
| --- | --- | --- |
| `fortinet.fortigate.traffic` (`traffic.yaml`) | 4001 Network Activity | `type="traffic"` (forward/local/multicast/sniffer) ve `type="utm"` (webfilter/ips/virus/app-ctrl/dns/ssl) |
| `fortinet.fortigate.event` (`event.yaml`) | 3002 Authentication | `type="event"` içinde yalnızca kimlik doğrulama: `Admin login successful/failed`, `Authentication success/failure/logon` |

`type="event"` altındaki diğer aileler (SD-WAN, FortiSwitch, IPsec, REST API,
FortiExtender…) **kapsam dışı**: her birinin OCSF sınıfı farklı ve `map` bloğu
dallanamıyor. F1'in hedefi motoru doğrulamak; kataloğun tamamı F2'nin editörüyle
gelecek.

## Zaman damgası — dikkat

FortiOS aynı olayın zamanını üç ayrı biçimde yazıyor ve **üçü de motorda
doğrudan kullanılamıyor**:

| Alan | Gerçek değer | Durum |
| --- | --- | --- |
| `date=` + `time=` | `2020-04-23` + `12:17:48` | İki AYRI alan. `date` adımı tek alan okuyor ve alan birleştirme yok. Geçici çözüm: grok yakalaması `date=` ile `time=` arasındaki metni de içine alıyor, biçim dizesi `"yyyy-MM-dd 'time='HH:mm:ss"` onu sabit metin olarak tüketiyor. |
| `tz="-0500"` | Sayısal UTC ofseti | `TimeZoneResolver` yalnızca IANA adı çözüyor. `timezone_field: tz` yazılsaydı **sessizce** `default_timezone`'a düşerdi. Bilerek yazılmadı. |
| `eventtime=1557513467369913239` | Epoch **nanosaniye** (7.x) veya saniye (6.x) | Motorda `UNIX` ve `UNIX_MS` var, `UNIX_NS` yok. Nanosaniye değeri `UNIX_MS` ile 51345 yılına düşer. Kullanılmıyor. |

Sonuç: olay zamanı `date`/`time` çiftinden, `default_timezone: Europe/Istanbul`
varsayımıyla çözülüyor. Bazı FortiOS 7.4 satırlarında `date`/`time` **hiç yok**;
o satırlar `_fortigate_no_local_time` etiketiyle `partial` kalıyor.

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
