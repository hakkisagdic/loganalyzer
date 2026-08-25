# Sigma kural yazım kılavuzu

Bu dizindeki kurallar derleme zamanında ClickHouse SQL'ine çevriliyor
(`tools/sigma-build`). Aşağıdaki şartların her biri **yaşanmış bir hatadan**
doğdu; gerekçesi olmayan madde yok.

Kuralların kaynağı `ruleset.json` çivisinde yazılı ve bugün **SigmaHQ değil**:
24 kural T30 örnekleminden terfi ettirildi.

---

## Üç şart — **her iddia için**, yalnızca kural için değil

Üçü de aynı kök soruya bakıyor: **iddia edilen şey, verinin gerçekten taşıdığı
şey mi?** Varsayılırsa kural derlenir, koşar, hiçbir sayaç artmaz ve "çalışıyor"
görünür.

Şartlar **iki yerde** geçerli ve bu ölçüldü:

| İddia | Nerede | Yanlış olduğunda |
| --- | --- | --- |
| Kuralın kendisi | `catalog/sigma/rules/*.yml` | Kural sessizce hiçbir şey yakalamaz |
| Beyanın gerekçesi | `catalog/sigma/expectations.json` | Kapı **yanlış sebeple** yeşil ya da kırmızı yanar |

İkinci satır sonradan eklendi çünkü **bu kılavuzu yazan kişi, beyan yazarken
3. şartı kendi çiğnedi**: `asa_acl_hit` için *"örnekte `Deny` satırları da
mevcut"* diye gerekçe yazdı — doğru, alakasız, ve kesişim sıfır. Aynı tuzak,
aynı turda, aynı elden.

Yani şart bir kontrol listesi maddesi değil bir **ilke**: iki yüklemi ayrı ayrı
görüp "desen var" demek, iddiayı kim yazarsa yazsın aynı hata.

### 1 · Her sabit dizge, o vendor'ın örneğinde **geçtiği görülerek**

```bash
grep -n 'Reset' catalog/parsers/cisco.asa/samples/network.log
```

Vendor'ın sözlüğü bizim varsayımımız değil. Ölçülen üç örnek:

| Kural | Aranan | Vendor'ın yazdığı |
| --- | --- | --- |
| `asa_teardown_rst` | `RST` | `Reset-I` / `Reset-O` |
| `routeros_forward_new` | `action` | `fw_chain` |
| `fortigate_user_auth_fail` | `failure` | `failed` (kolona `failure` olarak eşleniyor) |

Birincisi en pahalıydı: `RST` örneklerde yalnızca **`first` ve `burst`**
sözcüklerinin içine denk geliyordu. Kural "eşleşiyor" görünüyordu ve ölçüm aracı
onu `present` kutusuna koymuştu — **yanlış sebeple eşleşen bir kural, hiç
eşleşmeyenden tehlikeli.**

### 2 · **ve** gittiği kolonun o değeri **tutabildiği görülerek**

Alan adının var olması yetmiyor; kolonun **sözlüğü** de tutmalı.

`nginx_5xx_burst` bunu gösterdi: kural `status|startswith: '5'` arıyor, `status`
kolonu `outcome`'dan geliyor ve `catalog/mappings/http_status_outcome.yaml` HTTP
kodunu `success`/`failure`'a çeviriyor. **Kolonda hiçbir zaman sayı durmuyor**,
yani örneklemde 5xx olsa bile kural asla eşleşemez.

Bakılacak yer: `catalog/mappings/` ve `db/clickhouse/0003_ocsf_otel_views.sql`.

Aynı şart tip için de geçerli — `fortigate_high_port_scan` `proto: 6` yazıyor,
kolon `LowCardinality(String)`. ClickHouse reddediyor (Kapı 2 yakalıyor).

### 3 · **ve** istenen alanların o vendor'da **aynı satırda dolabildiği görülerek**

Bir kural iki alanı `AND` ile bağladığında, ikisinin **ayrı ayrı** dolu olması
yetmez; **aynı olayda** birlikte dolmaları gerekiyor.

Kısıtın kaynağı parser **sayısı** değil, parser'ların **neyi böldüğü** — ölçüldü,
dört vendor'ın dördünde de iki parser var ama biri hiç çift üretmiyor:

| Vendor | Kalıcı çift | Parser'lar neyi bölüyor |
| --- | --- | --- |
| MikroTik | **12** | konu: sistem ↔ güvenlik duvarı |
| Cisco | **4** | konu: kimlik ↔ ağ |
| Fortinet | **3** | konu: olay ↔ trafik |
| NGINX | **0** | **biçim**: aynı erişim logu, iki yazım |

nginx'in iki parser'ı **aynı konuyu** iki biçimde anlatıyor, yani aynı kolonları
dolduruyorlar ve köprü var. Diğer üçü **konuyu** bölüyor.

Yani sorulacak soru *"vendor'ın kaç parser'ı var"* değil: **parser'lar biçim mi
bölüyor, konu mu?** Konu bölünmüşse iki alanı `AND` ile bağlamadan önce
kesişimi ölçün.

19 çiftin 19'u **kalıcı** — hiçbiri örnek dosya eklenerek kapanmaz.

Ölçülen üç örnek, üçü de aynı şekilde kaçtı:

| Kural | İlk yarım | İkinci yarım | Kesişim |
| --- | --- | --- | --- |
| `routeros_dhcp_offer` | `proto UDP` 4 satır | `dstport 68` | **0** |
| `fortigate_user_auth_fail` | `status="failed"` 4 satır | `user="admin"` 2 satır | **0** |
| `nginx_large_upload` | `POST` | `/upload` | **0** (ikisi de yok) |

⚠️ Kapsam ölçüm aracı yüklemleri **tek tek** arıyor, kural onları aynı olayda
istiyor. Aracın `present` kutusu bu yüzden *"yüklemleri ayrı ayrı geçiyor"*
demek, *"kural eşleşebilir"* demek değil.

**Kontrol yolu:** iki yüklemi tek `grep` zincirinde ara.

```bash
grep 'status="failed"' catalog/parsers/fortinet.fortigate/samples/event.log \
  | grep -c 'user="admin"'
```

`logsource.category` taşımayan bir kural bu tuzağa daha açık: parser ayrımı
kaybolduğu için iki farklı olay ailesinden alan istemesi kolaylaşıyor.

**Beyan yazarken de aynı zincir.** Ölçülen iki düşüş:

| Beyan | Yazılan gerekçe | Ölçülen |
| --- | --- | --- |
| `asa_acl_hit` | "örnekte `Deny` satırları da mevcut" | tek `access-list` satırı **`permitted`** — kesişim 0 |
| `fortigate_high_port_scan` | "tip kusuru var" | `proto=6` 8 satır, hiçbirinde port ≥30000 — kesişim 0, **ve** tip kusuru ayrı bir sebep |

İkincisi bir şeyi daha gösteriyor: bir kalemin **kaç sebeple** kapalı olduğunu
yazmak, kapatan kişinin yanlış zafer ilan etmesini engelliyor. Tip kusuru
düzeltilse kural yine eşleşmezdi.

---

## Yazdıktan sonra

```bash
cd tools/sigma-build
python -m sigma_build.ruleset --refresh   # çivi
python -m sigma_build.compile --write     # üretilen SQL
python -m sigma_build.compile --check     # sürüklenme kapısı
```

Çivi yenilenmezse CI kırmızı yanar — bu bir kez oldu ve bir CI turu ile bir push
turu yedi. `compile --write` artık bayat çiviyle **yazmıyor**.

Beklenti (`catalog/sigma/expectations.json`) yazarken gerekçe **örnek
dosyasından** gelsin, ClickHouse sayısından değil. Sayıdan yazılan bir gerekçe
bugünkü verinin fotoğrafı olur; `asa_teardown_rst` tam olarak öyle
kutsanabilirdi.

## Tekrarlanan YAML anahtarı

```yaml
    message|contains: 'Teardown'
    message|contains: 'RST'      # ← YAML sonuncuyu alır, ilki SESSİZCE düşer
```

AND isteniyorsa `|all` kullanın; **düz bir liste Sigma'da OR'dur.**

```yaml
    message|contains|all:
      - 'Teardown'
      - 'Reset'
```

CI'da `yamllint`in `key-duplicates` kuralı bu dizini tarıyor ve bu kusuru
gerçekten yakaladı.
