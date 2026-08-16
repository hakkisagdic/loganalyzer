# Eşleme tabloları

`map` bloğundaki `{ from: <alan>, table: <tablo> }` ifadelerinin çözüldüğü yer.
Dosya adı tablo adıdır: `ocsf_network_activity.yaml` → `table: ocsf_network_activity`.

Tablolar **veri**dir (F1 §5): "türetme kuralları YAML `map` bloğunda ve merkezî
eşleme tablolarında durur — kodda değil". Motor bir tabloyu tanımıyorsa bu bir
**şema hatasıdır**, çalışma anı sürprizi değil: `ParserCompiler` bilinmeyen tabloyu
`parser lint` sırasında bildirir.

Arama **ordinal**dir; büyük/küçük harf normalizasyonu yapılmaz. Sebebi F1 §2.4:
`tr-TR` kültüründe `ToLower()` `I → ı` yapar ve eşleme sessizce ıskalar. Cihazın
bastığı değer neyse tabloda o yazar.

Bu dizindeki içeriğin **sahibi T07**'dir (normalizasyon). T05 yalnızca mekanizmayı
kurar; buradaki dosyalar tam katalog değil, mekanizmanın çalıştığını gösteren
başlangıç setidir.

## Tablolar

| Tablo | Ne üretir | Anahtar |
| --- | --- | --- |
| `ocsf_network_activity` | `ocsf.activity_id` (class 4001) | Cihazın `action` sözcüğü |
| `ocsf_authentication_activity` | `ocsf.activity_id` (class 3002) | Cihazın eylem sözcüğü/öbeği |
| `ocsf_http_activity` | `ocsf.activity_id` (class 4002) | HTTP metodu |
| `ocsf_severity` | `core.severity_num` | syslog severity rakamı **veya** seviye kelimesi |
| `ip_proto_name` | `core.proto` | IANA protokol numarası **veya** protokol adı |
| `auth_outcome` | `core.outcome` | Cihazın sonuç sözcüğü |
| `http_status_outcome` | `core.outcome` | HTTP durum kodu |

`ocsf_severity`, `ip_proto_name`, `ocsf_http_activity`, `ocsf_authentication_activity`,
`auth_outcome` ve `http_status_outcome` **T08**'de eklendi; `ocsf_network_activity`
gerçek FortiGate ve Cisco ASA çıktısında görülen eylem sözcükleriyle genişletildi.

İki uyarı, ikisi de T08'in gerçek vendor logunda çarptığı duvar:

* **Aralık araması yok.** `http_status_outcome` her durum kodunu tek tek
  listeliyor; motor "400–599 arası" diyemiyor.
* **`severity_num` iki farklı ölçeğe besleniyor.** `db/clickhouse/0003` bu kolonu
  OCSF görünümünde `severity_id` (0–6), OTel görünümünde `SeverityNumber` (1–24)
  olarak okuyor. Katalog OCSF ölçeğini yazıyor; OTel görünümünün ayrı bir
  türetmeye ihtiyacı var.
