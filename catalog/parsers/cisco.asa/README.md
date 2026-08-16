# Cisco ASA

| Parser | OCSF sınıfı | Kapsam |
| --- | --- | --- |
| `cisco.asa.network` (`network.yaml`) | 4001 Network Activity | 106001, 106023, 106100, 302013–302016, 302020, 302021, 305011, 305012, 710001–710006, 733100 |
| `cisco.asa.auth` (`auth.yaml`) | 3002 Authentication | 605004, 605005, 611101, 611102, 611103, 113004, 113005 |

**Neden iki parser?** `map` bloğu satır içeriğine göre dallanamıyor (F1 §3:
koşul/döngü yok). Kimlik doğrulama mesajları farklı bir OCSF sınıfına ait; tek
parser'da toplamak `class_uid`'i yarı yarıya yanlış yazmak olurdu. Katalog kuralı
bu yüzden **OCSF sınıfı ailesi başına bir parser**.

## Zarf değişkenleri

Aynı cihazın syslog çıktısı dört farklı zarfla geliyor; ikisi ayrı grok
pattern'i gerektiriyor:

```
Oct 10 2018 12:34:56 localhost CiscoASA[999]: %ASA-6-302013: ...   host + program
<165>Jan 11 2023 13:34:06: %ASA-5-111007: ...                      host yok
May  5 17:51:17 dev01: %ASA-4-106023: ...                          yıl yok
<166>10.1.1.1 %ASA-6-302020: ...                                   zaman damgası yok
```

Son şekilde olay zamanı yok: `date` adımı `_asa_no_timestamp` etiketi bırakıp
devam ediyor ve satır `partial` kalıyor. T07'nin normalizer'ı bu durumda alım
zamanına düşüyor — yanlış tarih yazmaktan iyi.

**Desteklenmeyen zarf:** iç içe zaman damgalı varyant
(`Feb 3 10:07:51 host : Feb 03 10:07:50 EST: %ASA-4-733100: ...`) — bazı syslog
relay'lerinin ürettiği bu şekil örnek dosyada yok, katalogda da yok.

## Pattern kaynağı

Gövde pattern'leri `catalog/patterns/legacy/firewalls` (Logstash seti, Apache-2.0)
içinden **veri olarak** geliyor. İki yerel istisna `network.yaml` içindeki
`pattern_definitions` bloğunda:

* `CISCO_REASON` — üst tanım `(?:%{WORD}\s*)*` içeriyor ve bizim ReDoS linter'ımız
  bunu GROK001 **hatası** olarak yakalıyor (haklı olarak: `(a+)+` ailesi). Pattern
  dosyasına dokunmadan burada geçersiz kılındı.
* `ASA_XLATE` — üst setteki `CISCOFW305011` 305012'nin `duration` kuyruğunu
  görmüyor ve çevrilmiş portu `%{DATA}` ile bitirdiği için hep boş bırakıyor.

## Örnek dosyalar

`samples/network.log` (20 satır) · `samples/auth.log` (7 satır)

Satırlar Cisco ASA'nın gerçek çıktısı; kaynak, Elastic'in `cisco_asa`
entegrasyonunun boru hattı test verisi (`test-asa.log`,
`test-additional-messages.log`) ve Cisco'nun syslog mesaj kılavuzundaki
örneklerdir. Yapı korunarak maskeleme yapıldı: yönlendirilebilir genel IP'ler
RFC 5737 belge aralıklarına (`203.0.113.0/24`, `198.51.100.0/24`) taşındı, özel
adresler ve kullanıcı adları olduğu gibi bırakıldı.

Bir satırda üst veri kümesinin maskeleme artığı vardı (`net:1192.168.2.2` —
geçersiz oktet); düzeltildi. Bunu `bizigo parser coverage` yakaladı, gömülü
testler değil.
