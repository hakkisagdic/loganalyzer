# nginx access log

| Parser | OCSF sınıfı | Kapsam |
| --- | --- | --- |
| `nginx.access.combined` (`combined.yaml`) | 4002 HTTP Activity | `combined` biçimi; sanal sunucu önekli ve `$request_time` ekli türevleriyle |
| `nginx.access.json` (`access-json.yaml`) | 4002 HTTP Activity | `log_format ... escape=json` — motorun `json` adımını doğrulayan parser |

## Neden iki parser?

Aynı vendor, aynı olay, iki tamamen farklı serileştirme. Tek parser'da toplamak
için boru hattının "önce `json` dene, tutmazsa `grok` dene" diyebilmesi gerekirdi
— motorda dallanma yok ve `json` adımı `on_failure: continue` ile atlansa bile
sonuç `partial` olurdu.

İki parser bunu **dispatcher'ın işine** çeviriyor: `match.contains` literalleri
(`"remote_user"` / `HTTP/1.1"`) kesişmiyor, "ilk `ok` kazanır" kuralı doğru
parser'ı seçiyor ve iki taraf da `ok` dönüyor. Format burada eksik değil, doğru
kullanım şekli bu.

## JSON parser'da iki adımlı ayrıştırma

nginx `$request` değişkenini tek metin olarak yazıyor
(`"GET /downloads/product_1 HTTP/1.1"`). `json` adımı onu bölmüyor; ikinci bir
`grok` adımı `request` **alanı** üzerinde çalışıp metot/yol/sürüme ayırıyor.
Adımların alan üzerinde zincirlenebilmesi formatın işe yarayan yanı.

## Örnek dosyalar

`samples/combined.log` (14 satır) · `samples/access-json.log` (10 satır)

* `combined.log` — Elastic'in `nginx` entegrasyonunun boru hattı test verisinden;
  gerçek nginx çıktısı.
* `access-json.log` — [`elastic/examples`](https://github.com/elastic/examples)
  deposundaki `nginx_json_logs` veri kümesinden (Apache-2.0); gerçek üretim
  trafiği.

İstemci IP'leri RFC 5737 belge aralıklarına **aynı uzunlukta** taşındı
(`93.180.71.3` → `203.0.113.3`); zaman damgaları, yollar, durum kodları, bayt
sayıları ve user-agent dizeleri olduğu gibi.

## Bilinen sınırlar

* `core.outcome`, `catalog/mappings/http_status_outcome.yaml` tablosundaki durum
  kodları için doluyor. Motor sayısal **aralık** araması tanımıyor, bu yüzden
  tabloda kod kod yazılı. Listede olmayan bir kod gelirse `outcome` boş kalıyor —
  yanlış değer yazmaktan iyi.
* `core.user_name`, kimliksiz istekte nginx'in yazdığı `-` değerini taşıyor.
  Boşa çevirmek cihazın söylediğini değiştirmek olurdu.
