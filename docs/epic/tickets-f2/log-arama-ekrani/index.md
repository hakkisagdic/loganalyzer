---
title: "T15 — Log arama ekranı"
kind: ticket
status: 2
---

# T15 — Log arama ekranı

**Bağımlılık:** T14 · **Sonraki:** T16
**Yöneten ölçümler:** [F1 kapanışı](../../f1-kapanis/index.md) — kısa sorgu eşiği ve keyset kısıtı

## Amaç

Ürünün en çok kullanılacak ekranı. F1'de ölçülen iki kısıt bu ekranın tasarımını
doğrudan belirliyor.

## Kapsam

### İçinde

- Zaman aralığı, kaynak, vendor, `parse_status`, severity, proto/action
filtreleri — hepsi `POST /v1/events/search`'ün desteklediği alanlar.
- Tam metin arama kutusu.
- **Keyset sayfalama** — sonsuz kaydırma ya da "sonraki" düğmesi; offset yok.
- Sonuç listesinde `time_source` görünür: cihazın yazdığı zaman mı, bizim
gördüğümüz mü. F1'de eklendi ve RCA'nın korelasyon penceresi buna bağlı.
- Kaydedilmiş arama (T21'in alarm kuralları buna bağlanacak).

### Dışında

- Olay detayı ve ham görünüm — T16.
- Grafik/histogram — F2'de değil.

## Kabul kriterleri

- **Kısa sorgu uyarısı.** Tam metin indeksi ~10-11 karakterden sonra seçici;
altındaki sorgular 1M satırda tam tarama yapıyor. Kutu ya minimum uzunluk
dayatıyor ya kullanıcıyı açıkça uyarıyor. Sessizce kabul etmek yasak.
- **Kaynak filtresi teşvik ediliyor.** Keyset ancak `owner_group` + `source_id`
verildiğinde sabit süreli: filtresiz derin sayfa 1M satır okuyor, kaynak
filtresiyle 57k. Ekran bunu kullanıcıya dayatmıyor ama varsayılan olarak
yönlendiriyor.
- İki farklı gruptaki kullanıcı aynı sorguda **farklı** sonuç görüyor; başka
grubun olayı hiçbir filtre kombinasyonuyla görünmüyor.
- Derin sayfalama gerçek veriyle sınanıyor — sayfa 50'de ilk sayfayla
karşılaştırılabilir süre.

## Notlar

Ölçülen sayılar (1M satır):

| Sorgu şekli | Sayfa 1 | Derin sayfa |
| --- | --- | --- |
| Filtresiz | 377k satır | 1M satır |
| `owner_group` | 155k | 286k |
| `owner_group` + `source_id` | 57k | 57k |

Kısa sorgu eşiği alfabeden **bağımsız**: `kullanıcı` (9 karakter) da atlamıyor,
`用户登录失败，请检查凭据` (12) atlıyor. Yani bu bir Türkçe/CJK sorunu değil,
uzunluk sorunu.

## Sevk edilen

**Rota: `/olaylar`** — ölçütlerin tamamı sorgu dizesinde. T21'in alarm ekranı
buraya derin bağlantı verebilir; parametre adları
`ui/src/lib/events/criteria.ts` içindeki `PARAM` sabitinde tek yerde duruyor:

| Parametre | Anlamı |
| --- | --- |
| `q` | tam metin (≥ 11 karakter) |
| `source_id` · `owner_group` · `vendor` | daraltma |
| `parse_status` | tekrarlanabilir (`ok`/`partial`/`failed`) |
| `severity_min` | 1–6, `severity_num > n-1` olarak gidiyor |
| `proto` · `action` | alan filtresi |
| `from` · `to` | zaman aralığı (boşsa son 24 saat) |
| `limit` | 50/100/200/500 |
| `after_ts` + `after_id` | keyset imleci; **ikisi birlikte** |
| `force=1` | kısa sorguda tam taramaya açık onay |

Ekranın tamamı sunucu bileşeni ve veri sunucuda çekiliyor: tarayıcı
`Bizigo.Api`'yi hiç görmüyor, erişim token'ı sayfaya hiç geçmiyor. Filtre formu
düz bir `GET` formu, "Sonraki sayfa" bir bağlantı — JavaScript kapalıyken de
çalışıyor.

**Kısa sorgu:** 11 karakterden kısa tam metin sorgusu **koşulmuyor**; ekran
sebebi ölçülen sayılarla söylüyor ve "Yine de ara (tam tarama)" açık bir eylem
olarak duruyor. Uzunluk kod noktası sayılıyor, UTF-16 birimi değil.

**Kaynak filtresi:** açılır liste kapsamdaki envanterden (`GET /v1/sources`)
besleniyor; kaynak seçilmediğinde ilk sayfada yönlendirme, sayfalamaya
geçildiğinde daha sert bir uyarı çıkıyor.

### Açık kalan: kayıtlı arama sunucuda değil

Kayıtlı aramalar `localStorage`'da. Bir arama tümüyle URL'den ibaret olduğu için
paylaşılabiliyor, ama kayıtların kendisi **cihaza bağlı** ve **T21'in alarm**
**kuralları bunlara bağlanamaz**: sunucuda duran bir kural tarayıcıdaki bir
girdiye referans veremez. Sunucu tarafı kayıtlı arama, kontrol düzleminde bir
tablo + uç demek; bu ticket EF göçü eklemediği için kapsam dışı bırakıldı.
