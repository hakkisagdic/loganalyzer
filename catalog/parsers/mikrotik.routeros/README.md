# MikroTik RouterOS

| Parser | OCSF sınıfı | Kapsam |
| --- | --- | --- |
| `mikrotik.routeros.firewall` (`firewall.yaml`) | 4001 Network Activity | `firewall` topic'i — `log` işaretli kuralın eşleştiği paket kaydı |
| `mikrotik.routeros.system` (`system.yaml`) | 3002 Authentication | `system,error,critical` ve `system,info,account` — oturum açma/kapatma ve başarısız denemeler |

## RouterOS kuralın SONUCUNU yazmıyor

Firewall kaydı `accept`/`drop` bilgisi içermiyor; yalnızca "şu paket, `log`
işaretli bir kuralla eşleşti" diyor. Bu yüzden:

* `core.action` **doldurulmuyor.** `accept` ya da `drop` yazmak uydurma olurdu.
* `ocsf.activity_id` sabit `6` (Traffic) — Open/Close/Deny seçilemiyor.

Kullanıcı kurala `log-prefix` verdiyse o metin `fields.fw_prefix` alanında
duruyor (örn. `R2`). Serbest metin olduğu için otomatik yorumlanmıyor; kuruma
özel bir eşleme tablosu F2'de eklenebilir.

## Zarf değişkenleri

RouterOS uzak syslog'a iki zaman damgası biçimiyle çıkıyor ve ikisi de tek grok
pattern'inde karşılanıyor:

```
<132>Oct 19 14:18:28 192.168.85.1 firewall,info forward: ...      RFC3164
2022-04-12T02:51:22.573113-06:00 officeswitch.lan system,... ...  RFC5424
```

`date` adımının biçim sırası **`SYSLOG` önce, `ISO8601` sonra**: `ISO8601` yolu
`DateTimeOffset.TryParse` kullanıyor ve `Oct 19 14:18:28` gibi yılsız bir damgayı
da yutuyor. Sıra ters olsaydı yıl sessizce bozulurdu.

Cihazın kendi `/log print` biçimi (`11:42:32 dhcp,info ...`,
`Mar/19/2021 15:52:33 firewall,info ...`) **kapsam dışı**: tarih taşımıyor ve
uzak syslog'a bu biçimde çıkmıyor.

## Örnek dosyalar

`samples/firewall.log` (7 satır) · `samples/system.log` (7 satır)

Mesaj gövdeleri MikroTik'in kendi dokümantasyonundan (RouterOS "Log" sayfası,
"Syslog with Elasticsearch" grok listesi) ve gerçek cihaz dökümlerinden birebir
alındı. RFC3164 zarfı (`<PRI>`, zaman damgası, host) gövdenin etrafına **eklendi**:
dokümantasyon yerel `/log print` çıktısını gösteriyor, aynı satır uzak syslog'a
bu zarfla çıkıyor ve platformun gördüğü şekil budur.

Genel IP'ler RFC 5737 belge aralıklarına taşındı; MAC adresleri, arayüz adları ve
paket uzunlukları olduğu gibi bırakıldı.

Örnek kümesindeki zorluklar:

* `out:ether1_Spectrum WAN` — arayüz adında boşluk
* `R2 forward:` — zincir adından önce kullanıcı öneki
* `..., NAT 203.0.113.7:53722 ->(...), len 78` — NAT kuyruğu
* `login failure for user from 2001:470:1:c84::24 via ssh` — kullanıcı adı yok, kaynak IPv6
