# Parser kataloğu

YAML parser plugin'leri. Format: [F1 §3](../../README.md) — `apiVersion / metadata /
match / pipeline / map / tests`.

## Katalog (T08)

| Dizin | Parser | OCSF sınıfı | Altın örnek |
| --- | --- | --- | --- |
| [`cisco.asa/`](cisco.asa/) | `cisco.asa.network` | 4001 | 20 satır |
| | `cisco.asa.auth` | 3002 | 7 satır |
| [`fortinet.fortigate/`](fortinet.fortigate/) | `fortinet.fortigate.traffic` | 4001 | 13 satır |
| | `fortinet.fortigate.event` | 3002 | 9 satır |
| [`mikrotik.routeros/`](mikrotik.routeros/) | `mikrotik.routeros.firewall` | 4001 | 7 satır |
| | `mikrotik.routeros.system` | 3002 | 7 satır |
| [`nginx.access/`](nginx.access/) | `nginx.access.combined` | 4002 | 14 satır |
| | `nginx.access.json` | 4002 | 10 satır |

Dört vendor, sekiz parser. Bölünmenin sebebi tek: **`map` bloğu satır içeriğine
göre dallanamıyor** (F1 §3 — koşul/döngü yok) ve aynı vendor'ın farklı mesaj
aileleri farklı OCSF sınıflarına ait. Katalog kuralı bu yüzden **OCSF sınıfı
ailesi başına bir parser**. Her dizinin kendi `README.md`'si o vendor'a özel
kararları ve örnek dosyaların kaynağını anlatıyor.

## Altın örnekler

`<parser dizini>/samples/*.log`. **Gerçek cihaz çıktısı** — elde uydurulmuş
satır yok. Kaynak her dizinin README'sinde yazılı: vendor dokümantasyonu,
vendor'ın kendi grok/entegrasyon test verisi, ve kamuya açık üretim log
kümeleri.

Maskeleme kuralı: yönlendirilebilir genel IP'ler RFC 5737 belge aralıklarına
(`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`) **aynı biçim ve mümkün
olduğunca aynı uzunlukla** taşındı. Özel adresler, MAC'ler, arayüz adları, alan
sırası, tırnak kullanımı ve kuyruk metinleri korundu — yapı bozulursa örnek
motoru değil hayal gücümüzü test eder.

`#` ile başlayan satırlar ve boş satırlar kapsam ölçümüne girmiyor.

## CI kapıları

```sh
bizigo parser lint catalog/parsers        # şema + ReDoS taraması
bizigo parser test catalog/parsers        # gömülü `tests` bloğu
bizigo parser coverage catalog/parsers    # altın örneklerin ok/partial/failed oranı
```

Testsiz bir parser şema düzeyinde reddedilir — `tests` bloğu zorunludur.

`coverage`, `test`ten farklı bir soru soruyor: satırları **dispatcher'dan**
geçiriyor, yani `match.contains` ön filtresini ve "ilk `ok` kazanır" kuralını da
ölçüyor. Çıktıdaki `→` satırları hangi parser'ın kaç satırı kazandığını gösterir;
bir vendor'ın satırı başka vendor'ın parser'ına düşüyorsa orada görünür. Gömülü
testler bunu gösteremez, çünkü orada kataloğun geri kalanı yoktur.

Aynı kapılar birim test tarafında da tutuluyor:
[`tests/Bizigo.UnitTests/VendorCatalogTests.cs`](../../tests/Bizigo.UnitTests/VendorCatalogTests.cs).
