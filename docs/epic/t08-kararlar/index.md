---
kind: spec
title: "T08 — vendor kataloğunda alınan kararlar"
---

# T08 — vendor kataloğunda alınan kararlar

> **Bu belge geriye dönük yazıldı:** kaynağı kod, commit geçmişi ve F1
> kapanışı. Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada
> yazan gerekçeler kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen
> alternatifler kayıtta yok.

**Bu belge T08'in motor bulgularını tekrarlamıyor.** Onlar
[T08 → T05 motor geri beslemesi](../t08-motor-geri-beslemesi/index.md)'nde, on
madde hâlinde ve bulundukları anda yazılmış — yani bu belgenin aksine
**geriye dönük değil**. Burada yazan şey: o rapordan bugüne **ne kaldı**, ve
katalogun kendisi hakkında hangi kararlar kodda duruyor.

## 1 · Bugün ölçülen

`bizigo parser coverage catalog/parsers` — bu belge yazılırken koşturuldu:

| | |
| --- | --- |
| Vendor | 4 (cisco.asa, fortinet.fortigate, mikrotik.routeros, nginx.access) |
| Parser dosyası | **8** |
| Altın örnek satırı | 87 |
| Sonuç | **ok 86 · partial 1 · failed 0** |

Kapanışta 84/3/0'dı. Aradaki fark motor düzeltmelerinden geldi (`UNIX_AUTO`,
`convert`'in boş alan davranışı). Kalan tek `partial`, Cisco ASA'nın zaman
damgası taşımayan gerçek satırı — **cihaz kaynaklı ve kalıcı**.

## 2 · Katalogun taşıyıcı kararı: OCSF sınıfı ailesi başına bir parser

Ticket dört parser istiyordu, sekiz yazıldı. Bu bir sapma değil, formatın
kısıtının **ölçülmüş** bedeli.

Cisco ASA'nın `%ASA-6-605005` (oturum açma, OCSF 3002) ve `%ASA-6-302013`
(bağlantı, OCSF 4001) satırları **aynı zarfı** paylaşıyor; ayrım yalnızca mesaj
kodunda. `class_uid` sabit olduğu için tek parser'da toplamak, sınıfı yarı yarıya
yanlış yazmak olurdu.

Alternatif — `map`'in satır içeriğine göre dallanması — F1 §3'ün bilinçli olarak
dışarıda bıraktığı şey (K15: "her adım tek iş"). Yani karar formatı zorlamak
değil, **formatın kısıtına uymak** oldu.

**Bugün hâlâ görünen bedeli:** `ls catalog/parsers/*/*.yaml` → 8. Aynı zarf
grok'u ASA'da iki, FortiGate'te iki, RouterOS'ta iki kez tekrarlanıyor.
Katalog yüzlerce parser'a çıkarsa bu tekrar bakım yüküne dönüşür — motor
geri beslemesinin **#5** maddesi ve hâlâ açık.

## 3 · Geri beslemenin bugünkü durumu

Rapordaki on maddenin bugün nerede durduğu. Ayrıntı ve gerekçe orada; burada
yalnızca **durum**.

| # | Eksik | Bugün |
| --- | --- | --- |
| 1 | `date` alan birleştiremiyor | ✅ Gereksizleşti (#3 sayesinde), hile kaldırıldı |
| 2 | `timezone_field` sessizce düşüyor | ✅ Kapandı — `±HHmm`/`Z`, `_tz_unresolved` etiketi |
| 3 | `UNIX_NS` yok | ✅ Kapandı — `UNIX_NS`/`UNIX_US`/`UNIX_AUTO` |
| 4 | `match` doğruluk garantisi değil | ⚠️ Kısmen — katalog kuralı "kapı adımı" oldu, formatta yazılı değil. T19'un editör iskeleti bunu yorumla öğretiyor |
| 5 | `map` dallanamıyor | ❌ **Açık** — bedeli yukarıda, 8 dosya |
| 6 | `expect` "alan yok" diyemiyor | ✅ **T19'da kapandı** — düz `null`/`~` gerçek `null`'a dönüyor |
| 7 | `convert` boş alanda düşürüyor | ✅ Kapandı — alan yoksa adım başarılı |
| 8 | Üst sette ReDoS + lookaround | ⚠️ 8a kapandı (`pattern_definitions` ile geçersiz kılma); 8b **açık** — doğrusal motor gerçek katalogda neredeyse hiç devreye girmiyor |
| 9 | `severity_num` tek kolon iki ölçek | ✅ Kapandı — `0004` ölçek çevrimi |
| 10 | `matchTimeout` duvar saati ölçüyor | ❌ **Açık** — öneriler yazıldı, uygulanmadı |

### Geçici çözümlerin izi silindi — ve bu ölçülebilir

Rapor her geçici çözümü ilgili YAML'da `motor eksiği #N` notuyla işaretlemiş ve
aranacak komutu yazmıştı:

```sh
rg "motor eksiği #" catalog/parsers
```

**Bugün sıfır sonuç veriyor.** Yani motor düzeltildikçe katalogdaki hileler
gerçekten kaldırılmış; not bırakma alışkanlığı işe yaramış. Kalan iki açık madde
(#5, #10) katalogda geçici çözümle değil, **yapısal olarak** duruyor — biri
dosya sayısında, diğeri yük altındaki davranışta.

## 4 · Altın örneklerin gerçek olması bir karar

Ticket açıkça yazmıştı: örnekler gerçek cihaz çıktısı olmalı, elde uydurulmuş
değil — *"uydurulmuş örnek, motorun eksiğini değil kendi hayal gücümüzü test
eder."*

Bunun karşılığı ölçülebilir: on madde bulundu ve **hiçbiri** uydurulmuş bir
örnekle bulunamazdı. En açık örnekler:

- `user="${exploit_user_name"` — kapanmamış tırnak taşıyan **gerçek** bir
FortiGate satırı; kv tokenizer'ın tırnak farkındalığı ancak bununla sınandı.
- Aynı vendor'ın iki sürümü `eventtime`'ı **farklı ölçekte** yazıyor (saniye ve
nanosaniye). Sabit bir belirteç seçmek yetmiyordu; `UNIX_AUTO` bu yüzden var.
- Cisco ASA'nın zaman damgası taşımayan satırı — bugün de `partial` ve öyle
kalacak.

Bu karar T19'un editörüne kadar taşındı: ham arşivden gerçek satır çekme paneli
aynı gerekçeyle var ve ekranda o cümle yazıyor.

## 5 · Açıkta kalanlar

| Ne | Neden açık |
| --- | --- |
| #5 `extends:` | Parser'lar arası kalıtım motor işi; tasarımı tek başına bir tartışma. T19 kapsamına **alınmadı** ve gerekçesi kayıtlı |
| #10 `matchTimeout` | Öneriler yazıldı (ayrı statü, karantinanın orana bakması, doğrusal ifadede sonsuz timeout); hiçbiri uygulanmadı |
| #8b `bizigo-v1` temel seti | Lookaround'suz `IPV4`/`TIME` varyantları — set var (`catalog/patterns/bizigo-v1`) ama gerçek katalog hâlâ legacy setine dayanıyor |
| Kataloğun genişlemesi | PAN-OS, Juniper, F5, HAProxy F1'de bilerek dışarıda. F2'nin editörü (T19) ve F4'ün keşif senaryosu bunu ucuzlatacak — editör indi, keşif senaryosu inmedi |

## 6 · Bu ticket'ın gerçek çıktısı

Ticket notunun kendi cümlesi: *"Hiçbir eksik bulunmadıysa muhtemelen örnekler
yeterince gerçek değil."* On eksik bulundu, altısı kapandı, ikisi kısmen, ikisi
açık. Dört YAML dosyası değil, **motorun gerçek yükle karşılaşmış olması** —
ve o karşılaşmanın tutanağı bugün hâlâ okunuyor.
