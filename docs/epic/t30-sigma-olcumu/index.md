---
title: "T30 — Sigma eşleme maliyeti: protokol ve ilk sayılar"
kind: spec
---

# T30 — Sigma eşleme maliyeti

Bu belge iki şey taşıyor: **ölçüm protokolü** (nasıl sayılacağı) ve
**statik ölçümler** (canlı ClickHouse gerektirmeden alınanlar). Canlı koşumun
sayıları geldiğinde "Doldurulacak" bölümü kapanacak ve kapsam kararı
gerekçelenmiş olacak.

Prototip: `prototypes/t30-sigma/`. **Kod atılabilir**; korunacak olan bu belge.

## Neden bu ölçüm

Önceki tur ölçtü: `SigmaHQ/pySigma-pipeline-ocsf` SigmaHQ kataloğunun %80'ine
dokunuyor ama bizim `events_ocsf` görünümümüze karşı **0 kural** olduğu gibi
çalışıyor. Yani kendi pipeline'ımızı yazmak zorundayız; açık olan tek şey
**ne kadar** yazacağımız.

<user_quoted_section>Önceki ölçüm kolon listesine karşıydı ve sorgu hiç çalıştırılmadı. Asıltehlike derleme hatası değil: pipeline eşlemeyi atlarsa derleme yine başarılıolur ve SQL var olmayan bir kolona referans verir.</user_quoted_section>

## Protokol: dört sayı, birbirine karıştırılmıyor

| Sayı | Anlamı | Neden tek başına yetmez |
| --- | --- | --- |
| `compiled` | pySigma SQL üretti | Pipeline eşlemeyi atlasa da derleme başarılı olur |
| `runs` | ClickHouse SQL'i kabul etti | Kolonlar var demek; satır bulduğu anlamına gelmez |
| `matches` | En az bir satır döndü | **Kapsam kararının dayanağı** |
| `untouched` | Çıktı pipeline'sız hâliyle birebir aynı | Eşlenmemiş kural; kapsamda **sayılmaz** |

`untouched`, araştırmadaki "0 kural" bulgusunun bizim şemamızdaki ölçülebilir
karşılığı. Bir kural `compiled` olup `runs` olmayabilir, `runs` olup `matches`
olmayabilir; üçünü tek sayıya indirmek, tam da önceki ölçümün yanıldığı yer.

### Ölçüm birimi

**Kural başına eşleme satırı** = pipeline'ın anlamlı satır sayısı ÷ `matches`.

Payda `matches`, `compiled` değil — eşlenmiş sayılmayan bir kural maliyeti
bölüşmüyor. Yorum satırları paydada yok: ölçülen şey bakım yükü değil,
yazılması gereken eşleme miktarı.

### Ölçekleme uyarısı

Örneklem SigmaHQ'dan indirilmedi, **bizim altın örneklerimize karşı yazıldı** —
çünkü kabul kriteri "altın örneklerimizle sına" diyor ve SigmaHQ'dan çekilen bir
kuralın bizim verimizde karşılığı olmayabilir; o zaman `matches=0` eşlemenin
değil örneklemin kusuru olurdu.

Bedeli: bu örneklem SigmaHQ'nun gerçek dağılımını temsil etmiyor. **269 kuralın
maliyetini doğrudan çarparak bulmak yanlış olur.** Doğru ölçekleme:

1. Örneklemden **alan başına** maliyet çıkar (28 alan → kaç satır).
2. SigmaHQ'nun hedef kategorilerindeki **ayrık alan kümesini** say.
3. Çarpım o küme üzerinden yapılsın, kural sayısı üzerinden değil.

Gerekçe: eşleme maliyeti kural sayısıyla değil **ayrık alan sayısıyla** büyüyor.
Yüz kural aynı on alanı kullanıyorsa maliyet on alanlıktır.

## Statik ölçümler

pySigma gerektirmeden, prototip dosyasından sayıldı:

| Ölçü | Değer |
| --- | --- |
| Örneklem | **24 kural** (4 vendor × 4 kategori) |
| Eşlenen alan | **28** |
| `unmapped` Map'ine düşen alan | **9** |
| Pipeline'ın anlamlı satırı | **111** |
| Örneklem başına | **4,62 satır/kural** |

⚠️ Son satır bir **alt sınır**: paydası *eşlenen* değil *örneklemdeki* kural
sayısı. Canlı koşumdan sonra payda `matches` olacak ve sayı yükselecek.

## Tuzaklar

Dördü araştırmadan biliniyordu; **beşincisi prototip yazılırken çıktı.**

| # | Tuzak | Durum |
| --- | --- | --- |
| 1 | Pipeline noktalı yol üretiyor (`dst_endpoint.ip`), görünüm düzleştirilmiş (`dst_endpoint_ip`) | Çözüldü — `FIELD_MAP` doğrudan düzleştirilmiş ada eşliyor, nokta hiç doğmuyor |
| 2 | Backend `FROM logs` yazıyor | Çözüldü — durum değişkeni; okunmazsa metin ikamesi ve **ikame raporlanıyor** |
| 3 | Tutarsız tırnaklama | Kaynağında kurudu — düzleştirilmiş ad tırnak gerektirmiyor |
| 4 | `unmapped.X` erişimi | Çözüldü — bizde `Map(String, String)`, `unmapped['X']` gerekiyor |
| 5 | **`type_uid` bizde yok** | **Yeni.** `ocsf_pipeline` sınıf ayırıcısını `type_uid` ile ekliyor; K8 gereği yazılan tek OCSF kolonu `class_uid` + `activity_id`. `type_uid` şart koşan bir pipeline **derlenip koşmayan** SQL üretir |

Beşincisi T30'un aradığı tuzak sınıfının tam örneği: derleme başarılı, SQL
yanlış, ve hiçbir şey kırmızı yanmıyor.

`unmapped`'e düşen 9 alan ayrı bir maliyet kalemi: Map erişimi ClickHouse'ta
çalışıyor ama **indekslenmiyor**. Yani bedeli doğruluk değil hız — ve bu, F3'te
hangi alanların kolona terfi etmesi gerektiğinin listesi.

## Birinci koşum — **geçersiz**, ve nedeni ölçümün kendi dersi

Koşum yapıldı ve `%0` eşleşme verdi. **O sıfır kullanılamaz.**

```
Ön kontrol: events_ocsf 1000001 satır
  Cisco             0     Fortinet   1000000     MikroTik  0     nginx  0

Örneklem 24 · Ölçülebilir 6 · Derlendi 24 · ClickHouse kabul etti 14
Satır döndürdü 0 · Eşleşme oranı %0 · Pipeline'a dokunmadı 0
Eşleme satırı 111 (28 alan) · Kural başına süre 1,5 ms · unmapped kullanan 0
```

Tablodaki 1.000.001 satır **altın örnek değildi**: önceki bir turdan kalan
tek-vendor'lu sentetik benchmark verisi. Ön kontrol "tablo boş mu" diye sordu,
cevap hayırdı, geçirdi.

<user_quoted_section>"Boş değil" ile "doğru veri" aynı şey değil — ve aradaki boşluk, ön kontrolünengellemek için yazıldığı hatanın tam olarak bir kademe deriniydi.</user_quoted_section>

Kanıtı çıktının kendisinde: Fortinet'in 1.000.000 satırı varken **Fortinet
kuralları bile hiçbir şey döndürmedi**. Gerçek FortiGate altın örnekleri olsaydı
en az birkaçı eşleşirdi.

**Ön kontrol düzeltildi:** artık bir yokluk kanıtı değil **varlık kanıtı**
arıyor — her vendor'ın altın örnek dosyasındaki en uzun satırdan türetilen 60
karakterlik bir sonda `raw_data` içinde gerçekten duruyor mu. Hiçbiri
bulunmazsa ölçüm **yapılmıyor**.

### Ama iki sayı veriden bağımsız ve şimdiden geçerli

| Sayı | Değer | Anlamı |
| --- | --- | --- |
| `compiled` | **24 / 24** | pySigma hepsini çevirdi; derleme bir sorun değil |
| `runs` | **14 / 24** | **On kural, ClickHouse'un reddettiği SQL üretti** |
| `untouched` | **0** | Pipeline her kurala dokunuyor — eşleme atlanmıyor |
| `unmapped` kullanan | **0** | Hiçbir kural Map erişimine düşmedi |

Bunlar satır sayısına bakmıyor: bir kolonun var olup olmaması tabloda kaç kayıt
olduğuna bağlı değil.

**Bu, aşağıdaki 2. geçersizleme kontrolünü tetikliyor** — yani kapsam kararına
geçmeden önce eşlemenin kendisi ele alınmalı. On kural var olmayan kolonlara
gidiyor ve bu kapsam daraltılarak çözülmez: daraltılan kapsamdaki kurallar da
aynı kolonlara gidiyor.

`untouched = 0` ile `unmapped = 0` birlikte okununca resim netleşiyor: sorun
eşlemenin **atlanması** değil, ürettiği adların bizde **olmaması**. Beşinci
tuzağın (`type_uid`) canlı karşılığı gibi duruyor — ama **doğrulanmadı**;
reddedilen kolon adları gelmeden bu bir hipotez.

Araç artık reddedilen kolonları da özetliyor (ClickHouse'un üç ayrı hata
cümlesini de okuyarak), dolayısıyla bir sonraki koşumda hem sayı hem **sebep**
gelecek. O liste T31'in eşleme tablosunun ilk taslağı olacak.

## Doldurulacak — altın örnekler yüklendikten sonra

Koşum: `prototypes/t30-sigma/README.md` içindeki komut. Ön kontrol raporunda
her vendor için `altın örnek bulundu` yazmıyorsa ölçüm yine geçersizdir.

- [ ] Reddedilen **kolon adları** — okuma sırasında ilk bakılacak şey
- [ ] `matches` ve `match_ratio` (payda: `measurable`)
- [ ] Kural başına gerçek eşleme satırı (payda = `matches`)
- [ ] Kural başına derleme süresi ve 269 kuralın toplam maliyeti
- [ ] En az bir kuralın canlı ClickHouse'ta doğru sonuç verdiği (kabul kriteri)
- [ ] Tablo adı ikamesi gerekti mi — gerektiyse T31 bunu kaynağında çözmeli
- [ ] Yanlış pozitif var mı: eşleşen satırlar gerçekten o kuralın aradığı mı

⚠️ Birinci koşumun `runs = 14` sayısı **devralınmayacak**, yeniden okunacak.
Kolonların varlığı veriye bağlı değil, ama o 14 kural sentetik veriye karşı
koştu ve hangi kuralların hangi sebeple düştüğü değişebilir.

## Ön kontrol — ölçüm aracının kendi bekçisi

Koşum, ölçmeye başlamadan önce `events_ocsf`'e bakıyor ve **geçemezse ölçüm
hiç yapılmıyor** (çıkış kodu 3). Üç durum ayrı ayrı raporlanıyor, çünkü
üçünün cevabı farklı:

| Durum | Anlamı | Cevap |
| --- | --- | --- |
| Sorgu hata verdi | Görünüm yok ya da kimlik yanlış | Kurulum sorunu; ölçüm yapılamaz |
| Satır sayısı sıfır | Altın örnekler yüklenmemiş | Ölçüm **yapılmamalı** — statik kip önerilir |
| Satır var | Ölçülebilir | Vendor dağılımı da raporlanıyor |

Sebep protokolün kendisi: boş bir görünüme karşı koşulan ölçüm her kural için
`matches=false` üretir ve o tablo "kapsam düşük" diye okunur. **Sıfırı sonuç
sanmak, T30'un engellemek için var olduğu sessiz yanlış sonucun ölçüm aracının
kendisindeki hâli.**

### Verisi olmayan kural oranın paydasından düşülüyor

Vendor dağılımı yalnızca bilgi değil, hesabın parçası. Bir vendor yüklenmemişse
o vendor'ın kuralları `no_data` işaretleniyor ve **paydadan çıkarılıyor**.

Fark kozmetik değil: 24 kuralın 6'sının vendor'ı yüklü değilse payda 24 iken
oran **%38**, doğru payda 18 iken **%50** — ve bu iki sayı aşağıdaki tabloda
**iki farklı dal**. Ölçüm aracının testi (`test_measure.py`) tam olarak bunu
sabitliyor.

## Kapsam önerisi — karar kuralı

Karar canlı sayılarla verilecek ama **hangi sayının hangi kararı verdiği
şimdiden bağlı**, ki sonuç geldikten sonra gerekçe uydurulmasın.

**Karar değişkeni:** `match_ratio` = `matches` ÷ `measurable`
(`measurable` = örneklem − `no_data`).

| `match_ratio` | Kapsam | Gerekçe |
| --- | --- | --- |
| **≥ %70** | Dört vendor, dört kategori | Eşleme maliyeti **alan** başına ödendiği için vendor eklemek ucuz; kural eklemek yeni alan getirmiyorsa bedava |
| **%40 – %70** | Yalnızca `firewall` + `network_connection` | DNS kategorileri `unmapped`'e en çok düşen taraf; indekssiz Map erişimi onları en pahalı hâle getiriyor |
| **< %40** | Tek vendor (FortiGate) | Altın örneği en zengin olan; T31 önce `unmapped` alanlarını kolona terfi etsin, kapsam ondan sonra genişlesin |

### Kararı geçersiz kılacak üç bulgu

Tablo bir varsayıma dayanıyor: **maliyet ayrık alan sayısıyla büyür, kural
sayısıyla değil.** Aşağıdakilerden biri çıkarsa tablo düşer ve öneri yeniden
yazılmalı — düzeltilmeli değil, **yeniden**:

1. **`untouched` yüksekse** (örneğin > %30): pipeline kuralların üçte birine hiç
   dokunmuyor demektir. O zaman sorun kapsam değil eşlemenin kendisi ve oran
   yanıltıcı — düşük `match_ratio` "vendor'ı çıkar" değil "eşlemeyi düzelt" der.
2. **`compiled` yüksek ama `runs` düşükse**: SQL var olmayan kolonlara referans
   veriyor. Bu, kapsamı daraltarak çözülmez; T31 kolon adlarını düzeltmeden
   hiçbir kapsam çalışmaz.
3. **Alan başına maliyet kural başına maliyetle birlikte büyüyorsa**: varsayım
   yanlış demektir, yani her kural yeni alan getiriyor. O hâlde 269 kuralın
   maliyeti doğrusal ve tablodaki üç dal da fazla iyimser.

Bu üç kontrol koşum çıktısından doğrudan okunuyor; ayrı bir analiz gerekmiyor.
