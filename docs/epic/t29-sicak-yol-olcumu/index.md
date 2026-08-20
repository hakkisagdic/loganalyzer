---
kind: spec
title: "T29 — signature_hash sözleşmesi ve sıcak yol maliyeti ölçümü"
---

# T29 — `signature_hash` sözleşmesi ve sıcak yol maliyeti

Bu belge iki şey taşıyor: **geri alınamaz bir sözleşme** (hash neyin üstünden
alınıyor) ve **koşturulmayı bekleyen bir ölçüm** (K35 sıcak yolu ne kadar
pahalılaştırıyor).

Kod `t29-signature-hash` dalında, `main`'den `48d8d1c` ile dallandı. Ölçüm
canlı bir makinede koordinatör tarafından koşturulacak; sonuç aşağıdaki tabloya
yazılacak ve K35 kararı ona göre teyit edilecek.

## 1 · Sözleşme — değiştirilirse geçmiş eşleşmez

`events.signature_hash` **maskelenmiş metnin kimliğidir**. Tanım tek yerde:
`Bizigo.Parsing.Grok.SignatureHash.Of`.

| Soru | Karar | Bunu değiştirmenin bedeli |
| --- | --- | --- |
| Neyin hash'i? | **Maskelenmiş metin** (`MaskCatalog.Signature` çıktısı) | Ham satır olsaydı her olay benzersiz hash alırdı; ilk-görülen her satırda ateşlerdi |
| Vendor / host / `source_id` dahil mi? | **Hayır** | Dahil olsaydı "yayılma sırası" sinyali çalışmazdı — o sinyal "aynı şey kaç cihazda" diye soruyor |
| Algoritma | **XXH64**, seed 0, UTF-8 baytlar | ClickHouse `xxHash64()` ile birebir; ayrışırsa veritabanına karşı doğrulama imkânı kaybolur |
| Kolon tipi | `UInt64`, `LowCardinality` **değil** | İmza kardinalitesi yüksek; LowCardinality sözlüğü her granülde büyür ve kazanç negatife döner |
| "İmza yok" | `0` (ayrılmış değer), `Nullable` değil | Null her satıra bir bayt ve her korelasyona bir `IS NOT NULL` eklerdi |
| Maske sözlüğü sürümü hash'e giriyor mu? | **Hayır** — §2 | Girseydi sözlüğün her güncellemesi **bütün** geçmiş hash'leri geçersiz kılardı |

Kararların hepsi teste sabitlenmiş (`SignatureHashTests`), çünkü yanlış
hesaplanmış bir hash **hiçbir yerde hata vermez**: istisna atmaz, sorgu
düşürmez, log yazmaz. Yalnızca RCA'nın iki sinyalini sessizce bozar ve bu aylar
sonra, yanlış bir kök neden raporunda görünür.

Altın vektör: `XXH64(UTF-8("<IPV4>")) = 14733834131172344067` —
ClickHouse'ta `SELECT xxHash64('<IPV4>')` aynısını veriyor.

### `template_id` ile ilişkisi

İkisi **farklı iş yapıyor**, biri diğerinin yerine geçmiyor.

| | `template_id` | `signature_hash` |
| --- | --- | --- |
| Kaynak | Sidecar (Drain3), önbellek üzerinden | Sıcak yol, yazma anında |
| Doluluk | `failed` olaylar + başarılıların %1'i | **Her olay** |
| İmzanın ilk görülüşünde | **Boş** (tanım gereği) | Dolu |
| İşi | İnsan-okunur kümeleme, F4'ün grok taslağı | İlk-görülen imza, hacim sapması |

## 2 · Sözlük değişimi — bilinen ve yazılı davranış

Maske sözlüğü değişirse **etkilenen** satırların maskelenmiş metni değişir,
dolayısıyla hash'i de değişir. Bu bir arıza değil; hash zaten o metnin
kimliği.

Sürümü hash girdisine **koymama** kararının gerekçesi karşılaştırmalı:

| | Sürüm hash'e girseydi | Girmiyor (seçilen) |
| --- | --- | --- |
| Sözlük güncellendiğinde | **Bütün** geçmiş hash'ler geçersiz | Yalnızca etkilenen maskelerin imzaları kayar |
| İlk-görülen sinyali | O gün **her** satır için ateşler | Sınırlı, bir seferlik dalga |
| Etkilenmemiş imzalar | Kopar | Süreklilik korunur |

Bedeli — sınırlı bir ilk-görülen dalgası — kabul edildi. Kazara olmaması için
`MaskCatalog.Version` bir bekçi testiyle sabitlendi
(`SignatureHashTests.Maske_sozlugu_surumu_sabit`): sürümü değiştiren kişi o
testi de değiştirmek zorunda, yani kayma bilerek yapılıyor.

`ReplayDiff` de imza değişimini raporluyor. Sessiz kalsaydı replay sonrası
ilk-görülen dalgasının sebebi hiçbir yerde yazılı olmazdı.

## 3 · Ölçüm — hangi sayı, hangi karşılaştırmayla

**Soru:** maskeleme her olayda koşunca sıcak yolun maliyeti ne kadar artıyor?

**Mutlak bir bütçe yok — bilerek.** F1'in en pahalı dersi tam buydu: duvar saati
bütçesi ölçmek istediğin şeyi değil, makinenin o anki hızını ölçer. Üretilen tek
anlamlı çıktı, **aynı süreçte aynı satırlar üzerinde** alınmış bir tabana
**oran**. Test hiçbir eşik iddia etmiyor: bekçi değil, ölçüm.

### Kurulum

| | |
| --- | --- |
| Girdi | Katalogdaki **gerçek** vendor satırları (`catalog/parsers/*/samples/*.log`) — sentetik satır maskeleme maliyetini istediği yere çeker |
| Dağıtım | Envanter bağı (1. kademe); her satırın parser'ı kurulumda bir kez bulunuyor |
| Örnekleme | `SampleRate = 0.01` — bugünün gerçek profili |
| Tur | 40.000 olay × 5 tur, **en küçük tur** raporlanıyor (girişim ölçümü yalnızca yukarı çeker) |
| Isınma | 5.000 olay — `RegexOptions.Compiled` kod üretimi + JIT |

### Dört arm

| Arm | Ne koşuyor |
| --- | --- |
| **A · yalnız ayrıştırma** | `dispatcher.Dispatch` |
| **B · öncesi** | Ayrıştırma + **%1 örneklemeli** etiketleme (K35 öncesi kod yolu) |
| **C · sonrası** | Ayrıştırma + **her olayda** imza + etiketleme (üretimin `ParsingSink`'i) |
| **· yalnız imza** | `masks.Compute` — maskeleme + hash |

B arm'ı üretimde artık yok, o yüzden ölçümde yeniden kuruluyor (dört satır,
`DiscoveryAnnotator`'ın `48d8d1c`'deki hâliyle birebir). Ölçümün en zayıf
halkası bu — ama alternatifi iki ayrı commit'i **iki ayrı süreçte** koşturup
karşılaştırmaktı, ki F1'in dersi tam olarak onun geçersiz olduğunu söylüyor.

### Koşturma — `-c Release` şart

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
BIZIGO_HOTPATH_BENCH=1 dotnet test tests/Bizigo.UnitTests -c Release \
  --filter FullyQualifiedName~HotPathCostMeasurement \
  -l "console;verbosity=detailed"
```

Rapor ayrıca `$TMPDIR/t29-hotpath.log`'a yazılıyor — xunit konsolu yutarsa sayı
yine de duruyor. **Docker, canlı sidecar, Python venv gerekmiyor:** ölçülen şey
saf CPU işi. Ölçümü venv'e bağlamak, hiç koşulmamasının en kolay yolu olurdu.

`-c Release` bir detay değil: Debug ölçümü **tek yönde** yanıltıyor. Ayrıştırma
bizim kodumuz ve Debug'da orantısız yavaşlıyor, maskeleme ise BCL regex olduğu
için etkilenmiyor — ölçüldü, imza maliyeti iki derlemede de **aynı**
(5.94 → 5.93 µs). Yani Debug tabanı şişirip `C/B` oranını **olduğundan küçük**
gösteriyor, K35'i hak etmediği kadar ucuz gösteriyor.

### Sonuç

Aşağıdaki satırlar bir **doğrulama koşusu**: aracın çalıştığını göstermek için
koşuldu, beş ajanın paylaştığı yüklü bir makinede. Mutlak sayılar bu yüzden
bağlayıcı değil — **oran** ise aynı süreçte alındığı için anlamlı ve iki
derlemede de tutarlı çıktı. Temiz makinedeki koşu koordinatörde.

Girdi: katalogdaki 87 gerçek vendor satırı · 40.000 olay × 5 tur · Release.

| Arm | ns/olay | olay/sn |
| --- | --- | --- |
| A · yalnız ayrıştırma | 14.773 | 67.692 |
| B · öncesi (%1 örnekleme) | 14.874 | 67.233 |
| C · sonrası (her olayda imza) | 21.655 | 46.178 |
| · yalnız imza (maskeleme + hash) | 5.926 | 168.760 |

| Türetilen | Değer |
| --- | --- |
| **Sıcak yol oranı `C/B`** | **1,46×** |
| Mutlak artış `C−B` | 6.781 ns/olay (~6,8 µs) |
| İmza payı `(C−B)/C` | %31,3 |
| Debug karşılaştırması | `C/B` = 1,40× — Debug oranı düşürüyor, §yukarı |

**Karar için okunuşu:** ticket'ın kendi eşiği *"olay başına maliyeti iki katına
çıkarıyorsa K35 yeniden değerlendirilmeli"*. Ölçülen 1,46× bu eşiğin **altında**
ve marjı var. Çekirdek başına iş hacmi ~67k olay/sn'den ~46k'ya düşüyor.

İki şey ayrıca not edilmeli:

1. **İmzanın mutlak maliyeti 5,9 µs/olay** ve bu ayrıştırmanın %40'ı — küçük
   değil. Ucuzlatılacaksa yer belli: 12 maskenin 4'ü hâlâ geri izlemeli motorda
   (`IPV6`, `IPV4`, `BASE16NUM`, `NUMBER`, lookaround yüzünden). Ama onlar
   Python sidecar ile birebir aynı kalmak zorunda (K14), yani tek taraflı
   yeniden yazılamazlar.
2. **Oran ayrıştırma ucuzladıkça büyür.** Parser motoru hızlanırsa `C/B`
   yükselir; sayı bir kez ölçülüp unutulacak bir sabit değil. Ölçüm testi
   repoda duruyor ve tek komutla tekrar koşuyor.

## 4 · Kapsam sınırı — raporun söylemesi gereken

16 KB'ı aşan satırın `signature_hash`'i **boş** kalıyor ve
`MaskCatalog.SkippedTooLong` sayıyor. Bu satırlar RCA'nın ilk-görülen
sinyalinde **görünmüyor** — kabul edilebilir, ama rapor bunu söylemek zorunda.
Sayaç tam olarak o cümlenin kurulabilmesi için var.

Geçmiş satırlar `0` kalıyor: `ALTER ADD COLUMN` yalnızca meta veri değiştiriyor.
Geçmişi doldurmanın tek doğru yolu replay — ham arşivden yeniden ayrıştırma
aynı sözlüğü koşturuyor.

## 5 · Bekçiler — kırmızı yanabildiği ölçüldü

Üçü de hata geri konularak sınandı, sonra geri alındı.

| Kırılan | Kırmızı yanan |
| --- | --- |
| Hash ham satırdan alınıyor | `SignatureHashTests` — 5 iddia |
| `ParsingSink` imzayı yazmıyor | `SignatureHotPathTests` — 7'de 5 |
| `ReplayDiff` imzayı karşılaştırmıyor | `ReplayDiffTests.Imza_degismesi_fark_olarak_raporlaniyor` |

Entegrasyon tarafında (yazıldı, koşturulmadı — faz sonu):
`SignatureHashStorageTests` .NET'in hash'ini ClickHouse'un **kendi**
`xxHash64()`'üne karşı her golden örnekte doğruluyor, `UInt64`'ün üst
yarısındaki bir değerin gidiş-dönüşte bozulmadığını gösteriyor, ve
"ilk-görülen imza"nın saf SQL ile cevaplandığını canlı tabloda kanıtlıyor.
