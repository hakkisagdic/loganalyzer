---
title: "T08 → T05: gerçek vendor logunun motorda açtığı yerler"
kind: spec
---

# T08 → T05: gerçek vendor logunun motorda açtığı yerler

T08'in asıl çıktısı dört YAML dosyası değil, **motorun gerçek yükle**
**karşılaşmış olması** (ticket notu). Bu belge o karşılaşmanın tutanağı.

Katalog yazılırken kullanılan gerçek çıktı: FortiOS 6.2/6.4/7.2/7.4, Cisco ASA
(13 mesaj kodu), MikroTik RouterOS 6.x/7.x, nginx `combined` ve JSON access log.
87 altın örnek satırı, 62 gömülü test.

Durum, T08 tamamlandığı andaki motoru (T05 + T06) anlatır.

## Özet

`f38e63c` (T05) üç maddeyi kapattı; katalog `origin/main` ile birleştirilip
geçici çözümler kaldırıldı.

| # | Eksik | Sonucu | Durum |
| --- | --- | --- | --- |
| 1 | `date` adımı alan birleştiremiyor | FortiGate zamanı ancak hileyle çözülüyor | Ertelendi (F2) — ama #3 sayesinde hile **gereksizleşti**, kaldırıldı |
| 2 | `timezone_field` sayısal ofset tanımıyor, **sessizce** düşüyor | Saatlerce kaymış zaman damgası, uyarısız | ✅ **Kapandı** — `±HHmm`/`±HH:mm`/`±HH`/`Z`; `_tz_unresolved` / `_tz_missing` etiketleri |
| 3 | `UNIX_NS` yok | Nanosaniye epoch sessizce 51345 yılına gidiyor | ✅ **Kapandı** — `UNIX_NS`/`UNIX_US`/`UNIX_AUTO`; ayrıca `UNIX`/`UNIX_MS`'in taşmada attığı istisna aralık kontrolüne alındı |
| 4 | `match` yalnızca dispatcher kademe 2'de çalışıyor | Yanlış parser `ok` dönebiliyor | Ertelendi (F2) — kapı adımları yerinde |
| 5 | `map` dallanamıyor | 4 vendor → 8 parser | Ertelendi (F2) — `extends:` iş kalemi |
| 6 | `expect` "alan yok" diyemiyor | Negatif alan testi yazılamıyor | Ertelendi (F2) |
| 7 | `convert` "hiç alan yok"u başarısızlık sayıyor | Doğru ayrışmış satır düşüyor | ✅ **Kapandı** — alan yoksa adım başarılı |
| 8 | Üst pattern setinde ReDoS + yaygın lookaround | Doğrusal motor pratikte devrede değil | Ayrı iş kalemi: lookaround'suz `bizigo-v1` temel seti |
| 9 | `severity_num` tek kolon, iki ölçek | OCSF ve OTel görünümleri çelişiyor | ✅ **Kapandı** — `db/clickhouse/0004` ölçek çevrimi |
| 10 | `matchTimeout` duvar saati ölçüyor | Yük altında doğrusal pattern bile düşüyor, satır sessizce `failed` | **Yeni** — aşağıda |

Kapsam raporunun temizlik öncesi/sonrası hâli:

|  | ok | partial | failed |
| --- | --- | --- | --- |
| Önce | 84 | 3 | 0 |
| Sonra | **86** | **1** | 0 |

Kalan tek `partial`, Cisco ASA'nın zaman damgası taşımayan gerçek satırı —
cihaz kaynaklı, kalıcı.

## 1. `date` adımı alan birleştiremiyor

FortiOS olayın zamanını **iki ayrı anahtara** yazıyor:

```
date=2020-04-23 time=12:17:48 devname="fw-01" ...
```

`DateStep.Field` tek bir alan adı; boru hattında `set`/`concat` adımı yok ve
`field` şablon kabul etmiyor. Kataloğun bugünkü çözümü, grok yakalamasını
aradaki `time=` metnini de içine alacak kadar genişletmek:

```yaml
- grok:
    patterns:
      - '^...(?<log_datetime>%{YEAR}-%{MONTHNUM2}-%{MONTHDAY} time=%{TIME})...'
- date:
    field: log_datetime
    formats: ["yyyy-MM-dd 'time='HH:mm:ss"]   # 'time=' sabit metin olarak tüketiliyor
```

Çalışıyor, ama katalog yazacak insana öğretilebilir bir şey değil.

**Öneri:** `date.field` şablon kabul etsin (`field: "{{ date }} {{ time }}"`) —
`TemplateRenderer` zaten var. Alternatif: küçük bir `set` adımı.

## 2. `timezone_field` sayısal ofset tanımıyor — ve sessizce düşüyor

Gerçek cihazlar saat dilimini IANA adıyla yazmıyor:

| Cihaz | Yazdığı |
| --- | --- |
| FortiGate | `tz="-0500"`, `tz="+0100"` |
| Cisco ASA | `EST` (iç içe zaman damgalı varyantta) |

`TimeZoneResolver` yalnızca `TimeZoneInfo.FindSystemTimeZoneById` deniyor.
Çözemezse `null` dönüyor ve `CompiledDateStep.ResolveZone` **hiçbir iz**
**bırakmadan** `default_timezone`'a düşüyor.

Bu yüzden katalogda `timezone_field: tz` **bilinçli olarak yazılmadı**. Yazılsaydı
FortiGate olayları 8 saat kaymış zaman damgasıyla yazılırdı ve ne `parse_status`
ne etiket ne de `Issues` bunu gösterirdi.

Listedeki en tehlikeli madde bu: diğerleri satırı düşürüyor ya da `partial`
yapıyor, bu sessizce yanlış veri üretiyor.

**Öneri:**

1. `±HHmm`, `±HH:mm`, `Z` biçimlerini çöz.
2. Çözülemeyen `timezone_field` değeri için **etiket bırak** (`_unknown_timezone`)
 — varsayılana düşmek meşru, sessizce düşmek değil.

## 3. `UNIX_NS` yok

```
FortiOS 7.x:  eventtime=1557513467369913239    (nanosaniye)
FortiOS 6.x:  eventtime=1554039772             (saniye)
```

Motorda `UNIX` ve `UNIX_MS` var. Nanosaniye değeri `UNIX_MS` ile okunursa
`DateTimeOffset.FromUnixTimeMilliseconds` 51345 yılına düşer — hata yok, uyarı
yok. Üstelik aynı vendor'ın iki sürümü aynı alanı iki farklı ölçekte yazdığı için
sabit bir belirteç seçmek de yetmiyor.

**Öneri:** `UNIX_NS` / `UNIX_US` belirteçleri, artı basamak sayısına bakarak
ölçeği seçen `UNIX_AUTO`. Makul üst sınır kontrolü (yıl > 9999 ise reddet) tek
başına bile bu sınıf hatayı görünür kılar.

## 4. `match` bir doğruluk garantisi değil

`match.contains` yalnızca dispatcher'ın **2. kademesinde** çalışıyor. İki yol onu
tamamen atlıyor:

- **Kademe 1 — envanter bağı.** `source_id → parser_id` tanımlıysa satır doğrudan
parser'a gidiyor; ön filtre görülmüyor. F1 §4.2 hedefi trafiğin >%95'inin bu
yoldan geçmesi — yani `match` üretimde neredeyse hiç çalışmıyor.
- **Gömülü `tests` bloğu.** `ParserTestRunner` parser'ı doğrudan çağırıyor.

Somut sonuç: `fortinet.fortigate.traffic` parser'ı bir `type="event"` satırını
`ok` ayrıştırdı. Boru hattında ayırt edici hiçbir şey yoktu; ayrım sadece
`match.contains` içindeydi.

Kataloğun çözümü, her parser'ın boru hattını bir **kapı adımıyla** başlatmak:

```yaml
- grok:
    field: message
    patterns: ['type="(?<log_type>traffic|utm)"']
```

**Öneri:** ikisinden biri —
(a) formatta açıkça yaz: "`match` performans içindir; ayırt ediciliği `pipeline`'a
koy", ya da
(b) kademe 1'de de `match.contains` doğrulansın (bir literal bile tutmuyorsa
bound-miss say). (b) daha güvenli ama `bound_ratio` metriğinin anlamını değiştirir.

## 5. `map` dallanamıyor

F1 §3'ün bilinçli kararı (koşul/döngü yok). T08'de bedeli ölçülebilir hale geldi:
**4 vendor, 8 parser.**

Cisco ASA'nın `%ASA-6-605005` (oturum açma, OCSF 3002) ve `%ASA-6-302013`
(bağlantı, OCSF 4001) satırları **aynı zarfı** paylaşıyor; ayrım yalnızca mesaj
kodunda. `class_uid` sabit olduğu için tek parser'da toplamak sınıfı yarı yarıya
yanlış yazmak olurdu.

Katalog kuralı bu yüzden **OCSF sınıfı ailesi başına bir parser** oldu. Karar
doğru, ama aynı zarf grok'u ASA'da iki, FortiGate'te iki, RouterOS'ta iki kez
tekrarlanıyor. Katalog yüzlerce parser'a çıkınca bu tekrar bakım yüküne dönüşür.

**Öneri (F2, acil değil):** parser'lar arası zarf paylaşımı — `extends:` ya da
paylaşılan `pipeline` parçası. Koşul eklemekten daha ucuz ve deklaratifliği
bozmuyor.

## 6. `expect` "bu alan hiç olmamalı" diyemiyor

`ParserYamlLoader.ReadExpectedValue` düz skaleri okurken YAML `null`/`~`
değerini tanımıyor; `"null"` metni olarak geçiriyor.

RouterOS kullanıcı adı olmadan da başarısız oturum kaydı yazıyor:

```
system,error,critical login failure for user from 2001:470:1:c84::24 via ssh
```

Doğrulanmak istenen tam olarak `core.user_name`'in **atanmamış** olması —
`TemplateRenderer`'ın çözülemeyen şablonu yazmama davranışı burada kritik. Test
bunu ifade edemediği için yorumla açıklandı.

**Öneri:** YAML `null`/`~` gerçek `null`'a dönsün. `ValuesMatch` zaten
`expected is null && actual is null` durumunu doğru ele alıyor.

## 7. `convert`, hiç alan bulamazsa satırı düşürüyor

`CompiledConvertStep`: listedeki alanların **hiçbiri** yoksa `false` dönüyor,
`on_failure: fail` varsayılanıyla satır `failed`.

Çok şekilli bir parser'da bu tuzak. Cisco ASA 733100 satırında ne port ne bayt
var:

```
%ASA-4-733100: [192.168.2.2] drop rate-1 exceeded. Current burst rate is 0 ...
```

Satır tamamen doğru ayrıştığı halde `convert` yüzünden düştü. Kataloğun geçici
çözümü, `convert` listesine desteklenen **her gövde şeklinden** en az bir alan
koymak — yeni bir mesaj kodu eklendiğinde sessizce kırılır.

**Öneri:** eksik alanı atlamak varsayılan olsun (dönüştürülecek bir şey yoksa
adım başarılıdır), ya da `require: any | all` seçeneği eklensin.

## 8. Üst pattern setinin durumu

İki ayrı bulgu, ikisi de `bizigo parser lint` sayesinde görüldü.

**8a — ReDoS.** Logstash `firewalls` setindeki `CISCO_REASON` son alternatifi
`(?:%{WORD}\s*)*` yazıyor: iç içe sınırsız niceleyici, `(a+)+` ailesi. Linter
bunu GROK001 **hatası** olarak yakaladı ve CI'yi kırdı — yani ReDoS linter'ı
gerçek bir vendor pattern'inde işini yaptı.

Katalog `pattern_definitions` ile parser içinde geçersiz kıldı; pattern dosyasına
dokunulmadı (`catalog/patterns/README.md` kuralı). Bu geçersiz kılma mekanizması
tam olarak beklendiği gibi çalıştı.

**8b — doğrusal motor pratikte devrede değil.** Kataloğun tamamında **15 pattern**
GROK003 uyarısı verdi: `NonBacktracking` ile derlenemeyip geri izlemeli motora
düştüler. Sebep tek tek bizim pattern'lerimiz değil, Logstash'in temel
tanımları — `IPV4` `(?<![0-9])`, `TIME` `(?![0-9])` kullanıyor ve bunlara
dokunan her ifade geri izlemeye düşüyor.

Ağ cihazı logunda IP geçmeyen pattern neredeyse yok. Yani F1 §4.1'in "önce
doğrusal motoru dene" kademesi gerçek bir katalogda **neredeyse hiç** devreye
girmiyor; fiili savunma 50 ms `matchTimeout` + karantina.

**Öneri:** karar yanlış değil ama gerekçesi güncellenmeli. Ek olarak
düşünülebilir: lookaround'suz `IPV4`/`TIME` varyantları içeren kendi temel
setimiz (`bizigo-v1`), doğrusal motorun gerçekten kullanılabildiği bir taban
sağlar.

## 9. `severity_num` tek kolon, iki ölçek

`db/clickhouse/0003_ocsf_otel_views.sql` aynı kolonu iki farklı sözlükle okuyor:

```sql
severity_num AS severity_id      -- events_ocsf   → OCSF: 0..6
severity_num AS SeverityNumber   -- events_otel   → OTel: 1..24
```

İkisi aynı şey değil. Katalog **OCSF ölçeğini** yazıyor
(`catalog/mappings/ocsf_severity.yaml`), çünkü `ocsf_class_uid` ve
`ocsf_activity_id` zaten OCSF. Sonuç: `events_otel.SeverityNumber` bugün yanlış.

**Öneri:** OTel görünümü `severity_num`'u OCSF→OTel eşlemesiyle türetsin, ya da
kolonun hangi ölçek olduğu şemada yazılı olsun.

## 10. `matchTimeout` duvar saati ölçüyor — yük altında satır sessizce düşüyor

Temizlik turunda ortaya çıktı, ilk raporda yoktu.

`GrokCompilerOptions.MatchTimeout` 50 ms ve `Regex.Match` bunu **duvar saati**
olarak uyguluyor. CPU baskısı altında geçen süre pattern'in karmaşıklığından
bağımsız; işlem zaman dilimi alamadığında da sayaç işliyor.

Somut gözlem (makine swap %89'dayken, tek oturumda):

- Aynı ikili, aynı 87 satırlık örnek kümesi, iki ardışık `parser coverage`
koşumu: biri `ok 83 / failed 1`, diğeri `ok 84 / failed 0`.
- Düşen satırın parser'ı tek başına çağrıldığında sorunsuz ayrışıyor.
- `parser test` bir koşumda şu hatayı verdi:
`adım 'grok': pattern zaman aşımına uğradı: logdesc="(?<logdesc>Admin login successful|...)"`
— bu pattern düz bir literal alternasyonu, lookaround yok, `NonBacktracking`
ile derleniyor ve girdi uzunluğunda doğrusal. 50 ms'i aşması imkânsız;
aşan şey işlemin bekleme süresi.
- `dotnet test` bir koşumda 4 test düşürdü (`GrokCompilerTests`,
`MaskCatalogTests`, `DispatcherTests`, `VendorCatalogTests`), ardışık koşumda
301/301 geçti.

Neden önemli:

1. **Sonuç `failed`** — yani "motor meşguldü" ile "bu satır bu parser'a uymuyor"
 ayırt edilemiyor. Dispatcher açısından satır hiçbir parser'a uymamış gibi
 görünüyor ve keşif kuyruğuna düşüyor.
2. **Karantina yanlış tetiklenebilir.** F1 §4.1 kademe 3, sürekli timeout veren
 parser'ı karantinaya alıyor. Yük altında sağlıklı bir parser karantinaya
 girip sahibine uyarı gidebilir — arıza pattern'de değil, makinede.
3. **CI kapısı kararsız.** `parser coverage` kapısı `failed > 0` ise kırıyor;
 yüklü bir runner'da bu rastgele kırılma demek.

Öneri:

- Zaman aşımını `TimedOut` sonucundan **ayrı** bir statüyle taşı (`engine_busy`
gibi) ya da en azından satırı `failed` yerine `partial` + etiketle işaretle;
`ParseResult.TimedOut` alanı zaten var ama `Status`'a `Failed` olarak yansıyor.
- Karantina kararı ardışık timeout sayısına değil, **başarılı eşleşmelere oranına**
baksın; yük altında oran bozulmaz, bozuk pattern'de bozulur.
- `NonBacktracking` ile derlenen pattern'lerde zaman aşımına hiç gerek yok —
doğrusal zaman garantisi zaten var. `CompiledGrok.IsLinearTime` doğruysa
`Regex.InfiniteMatchTimeout` kullanılabilir; bu, madde 8'deki `bizigo-v1`
temel setinin değerini de artırır (lookaround'suz set = timeout'suz set).

## Motorun iyi çalıştığı yerler

Bunlar da kayda değer — gerçek logda sınandılar ve geçtiler.

- **kv tokenizer'ın tırnak farkındalığı.** FortiGate `msg="URL belongs to a denied category in policy"` ve `user="${exploit_user_name"` (şablon enjeksiyonu
denemesi taşıyan gerçek bir satır) kusursuz ayrıştı. Naif `Split(' ')` ikisini
de bozardı.
- **`pattern_definitions` ile üst set geçersiz kılma.** Madde 8a'nın çözümü;
pattern kütüphanesini veri olarak tutma kararının karşılığını verdiği yer.
- **Ordinal tablo araması.** Gerçek vendor çıktısı aynı anlamı `Deny`/`deny`/
`denied`/`Denied` diye dört ayrı yazımla basıyor. Normalizasyona güvenilseydi
`tr-TR` altında sessizce ıskalanırdı; her yazımı satır olarak yazmak sıkıcı ama
doğru.
- **Çözülemeyen şablonun atanmaması.** RouterOS firewall kaydı kuralın sonucunu
içermiyor; `core.action` boş kalıyor. Boş string yazılsaydı "action=''" diye
sorgulanabilir sahte bir değer oluşurdu.
- **`on_failure: tag` → `partial` + etiket.** Zaman damgası olmayan gerçek
satırlar (FortiOS 7.4, ASA relay çıktısı) böylece kaybolmuyor, ama "bu satırın
zamanı cihazdan gelmedi" bilgisi de kayboluyor değil.
- **`json` → `grok` alan zincirlemesi.** nginx `$request` tek metin olarak
yazılıyor; `json` adımından sonra `grok`'un aynı sözlükteki bir **alan**
üzerinde çalışabilmesi formatın en işe yarayan yanı.

## Katalogda bırakılan izler

Her geçici çözüm, ilgili YAML'da "T08 raporu, motor eksiği #N" notuyla işaretli.
Motor düzeltildiğinde aranacak yer orası:

```sh
rg "motor eksiği #" catalog/parsers
```
