# T30 — Sigma pipeline prototipi

> ⚠️ **Bu dizindeki kod atılabilir.** T30'un teslim ettiği şey kod değil bir
> sayı: Sigma kuralı başına eşleme maliyeti. Kalıcı pipeline T31'in işi ve
> muhtemelen bunun şeklini almayacak. Korunacak olan `SONUCLAR.md`.

## Bölünme

Prototip ve ölçüm protokolü burada yazıldı; **canlı ClickHouse koşumu
birleştirmeyi yapan tarafta.** Bu dosyadaki sayıların bir kısmı statik olarak
ölçüldü ve işaretlendi; `runs`/`matches` sütunları koşum yapılmadan **boş**
kalır ve sıfır görünmeleri başarısızlık değildir.

## Kurulum

Backend `requires-python >=3.13` istiyor; sidecar imajı zaten `python:3.13-slim`.

```bash
cd prototypes/t30-sigma
python3.13 -m venv .venv
.venv/bin/pip install 'pySigma==1.5.0' 'pysigma-backend-clickhouse==1.1.1' 'PyYAML==6.0.3'
```

Sürümler `sidecar/requirements.txt` ile **birebir aynı** — ölçüm, üretimde
kurulacak olandan başka bir şeyi ölçmemeli.

## Koşum

```bash
# Statik: derleme, eşleme sayısı, tuzak tespiti. ClickHouse gerekmiyor.
.venv/bin/python measure.py --json sonuc-statik.json

# Canlı: T30'un kabul kriteri. `deploy/` compose'u ayakta olmalı.
.venv/bin/python measure.py \
  --clickhouse-url http://localhost:8123 \
  --clickhouse-user bizigo --clickhouse-password bizigo \
  --json sonuc-canli.json
```

### Ön kontrol — bunu atlayamazsınız

Canlı kip, ölçmeye başlamadan önce `events_ocsf`'e bakıyor ve **geçemezse
ölçüm hiç yapılmıyor** (çıkış kodu 3):

| Durum | Davranış |
| --- | --- |
| Sorgu hata veriyor | Reddediyor — görünüm yok ya da kimlik yanlış |
| Tablo boş | Reddediyor — statik kipi öneriyor |
| Veri var | Geçiyor, **vendor dağılımını** basıyor |

Sebep: boş bir görünüme karşı koşulan ölçüm her kural için `matches=false`
üretir ve o tablo "kapsam düşük" diye okunur. Sıfırı sonuç sanmak, bu ticket'ın
engellemek için var olduğu sessiz yanlış sonucun ölçüm aracındaki hâli.

Vendor dağılımı da hesabın parçası: yüklenmemiş bir vendor'ın kuralları
`no_data` işaretlenip **oranın paydasından düşülüyor**. 24 kuralın 6'sı verisizse
oran %38 değil %50 — ve bu iki sayı `SONUCLAR`'daki karar tablosunda iki farklı
dal.

### Aracın kendi testleri

Ölçüm bir kapsam kararı verecek; aracın sessizce yanlış sayması, protokolün
engellemek istediği hatayı aracın üretmesi olurdu.

```bash
python3.13 test_measure.py     # pySigma GEREKMİYOR
```

## Ölçülen üç kademe — ve neden üçü ayrı

| Kademe | Anlamı | Neden yetmez |
| --- | --- | --- |
| `compiled` | pySigma SQL üretti | Pipeline eşlemeyi atlasa da derleme başarılı olur |
| `runs` | ClickHouse SQL'i kabul etti | Kolonlar var demek; satır bulduğu anlamına gelmez |
| `matches` | En az bir satır döndü | **Kapsam kararının dayanağı bu** |

Önceki ölçüm kolon listesine karşı yapıldı ve sorgu hiç çalıştırılmadı. Asıl
tehlike derleme hatası değil: pipeline eşlemeyi atlarsa derleme yine başarılı
olur ve SQL **var olmayan bir kolona** referans verir. Üç kademe bu yüzden
birbirine karıştırılmıyor.

Dördüncü bir sayı daha var: `untouched` — çıktısı pipeline'sız hâliyle birebir
aynı kalan kurallar. Bunlar **eşlenmemiş** kurallardır ve kapsam kararında
sayılmamalıdır. Araştırmanın "0 kural" bulgusunun bizim şemamızdaki karşılığı.

## Örneklem

24 kural, `rules/` altında. Dört vendor × dört kategori:

| Vendor | Kural | Kategoriler |
| --- | --- | --- |
| FortiGate | 6 | firewall, network_connection, dns |
| Cisco ASA | 6 | firewall, network_connection |
| MikroTik RouterOS | 6 | firewall, network_connection, dns_query |
| nginx | 6 | firewall, network_connection, dns |

**Kurallar SigmaHQ'dan indirilmedi, bizim altın örneklerimize karşı yazıldı.**
Gerekçe kısayol değil kabul kriteri: ticket "altın örneklerimizle sına — kural
gerçekten eşleşiyor mu, yanlış pozitif var mı" diyor. SigmaHQ'dan çekilen bir
kuralın bizim verimizde karşılığı olmayabilir ve o zaman `matches=0` çıkması
eşlemenin değil örneklemin kusuru olurdu. Alan adları ve operatörler SigmaHQ
taxonomy'sinden (`src_ip`, `dstport`, `|contains`, `|startswith`, `|gte`).

Bedeli açık: bu örneklem SigmaHQ'nun gerçek dağılımını temsil etmiyor. 269
kuralın maliyetini buradan **doğrudan** çarpmak yanlış olur; `SONUCLAR.md`
bunu nasıl ölçekleyeceğini yazıyor.

## Bilinen dört tuzak ve prototipteki karşılıkları

| Tuzak | Karşılık |
| --- | --- |
| Pipeline noktalı yol üretiyor (`dst_endpoint.ip`), görünüm düzleştirilmiş (`dst_endpoint_ip`) | `FIELD_MAP` doğrudan düzleştirilmiş ada eşliyor; nokta hiç doğmuyor |
| Backend `FROM logs` yazıyor | `SetStateTransformation("table", …)` + gerekirse `rewrite_table()`, ve **ikame raporlanıyor** |
| Tutarsız tırnaklama | Düzleştirilmiş adlar tırnak gerektirmiyor — sorun kaynağında kuruyor |
| `unmapped.X` erişimi | Bizde `Map(String, String)`; `unmapped['X']` gerekiyor, `unmapped_expression()` |

Beşinci bir tuzak prototip yazılırken çıktı: **`type_uid` bizde yok.**
`ocsf_pipeline` sınıf ayırıcısını `type_uid` ile ekliyor; K8 gereği kolona
yazılan tek OCSF alanı `class_uid` ve `activity_id`. `type_uid` şart koşan bir
pipeline bizde **derlenip koşmayan** SQL üretir — tam da T30'un aradığı tuzak
sınıfı.

## Statik olarak ölçülenler

Bunlar pySigma gerektirmeden, dosyadan sayıldı:

| Ölçü | Değer |
| --- | --- |
| Eşlenen alan sayısı | **28** |
| `unmapped`'e düşen alan | **9** |
| Pipeline'ın anlamlı satırı (yorum hariç) | **111** |
| 24 kurala bölünürse | **4,62 satır/kural** |

⚠️ Bu son sayı bir **üst sınır değil alt sınır**: paydadaki 24, *eşlenen* değil
*örneklemdeki* kural sayısı. Canlı koşumdan sonra payda `matches` olacak ve
sayı yükselecek. `SONUCLAR.md` ikisini ayrı yazıyor.
