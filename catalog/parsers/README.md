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

## `specificity` — ne yazmalı (T39)

**Kısa cevap: vendor içinde dardan genele, ve bugün hiçbir şeyi belirlemiyor.**

Dispatcher `specificity`'yi yalnızca **kademe 3'te** kullanıyor: literal ön
filtreden geçen adayları sıralamak için, ve kural "ilk `ok` kazanır". Dolayısıyla
sıralama ancak **birden çok aday aynı satırı `ok` ayrıştırdığında** sonucu
değiştirir.

**Ölçüldü (87 altın örnek satırı, 8 parser):**

| | |
| --- | --- |
| Tek adaylı satır | **70** |
| İki adaylı satır | **17** |
| Vendor'lar arası aday üreten satır | **0** |
| Birden çok adayın `ok` döndüğü satır | **0** |

Yani bugün `specificity` **hiçbir satırın hangi parser'a düştüğünü
belirlemiyor**. Ön filtre vendor'ları zaten ayırıyor; aynı vendor'ın iki
parser'ı aday olduğunda da yalnızca biri `ok` dönüyor.

### Yazarken ne yapmalı

1. **Aynı vendor içinde dardan genele sırala.** Gövde pattern'i daha bağlı
(`^…$`), literalleri daha ayırt edici olan yüksek alır. Katalogdaki üç yorum bu
biçimde: *"Ağ parser'ından yüksek: gövde pattern'leri çok daha dar."*
2. **Vendor'lar arası karşılaştırma yapma** — koşmuyor. `cisco.asa.auth`'un 95,
`fortinet.fortigate.event`'in 90 olması bir sıralama kararı değil; ikisi hiçbir
satırda yarışmıyor.
3. **Mutlak değerin anlamı yok.** 95/85 ile 2/1 aynı şeyi söyler. Katalog 50–95
bandını kullanıyor, o bandı sürdürmek yeterli.
4. **Eşitlik serbest.** Aynı değer verildiğinde sıralama kimliğe göre alfabetik
(`ParserCatalog.BuildSnapshot`) — tekrarlanabilir ama anlamlı değil. Bugün
sonucu değiştirmediği için sorun değil; değiştirdiği gün bekçi kırmızı yanıyor.

### Bekçi

`SpecificityRelevanceTests` yukarıdaki iki ölçümü her koşumda tekrarlıyor.
Kırmızı yandığı gün **ölçüt gerçekten gerekli hâle gelmiş** demektir: iki aday
aynı satırı sahiplenmeye başlamış ve hangisinin kazanacağı artık gerçek bir
soru. O gün cevabı vermek, bugün uydurmaktan iyi.

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
