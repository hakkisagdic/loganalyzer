---
kind: spec
title: "Kalan iş — kapanış durumu (2026-09-22)"
---

# Kalan iş raporu

Bu rapor 2026-09-05 tarihli ara durumun yerine geçer. O belgede açık görünen
T32, T47, T49, T63 ve M07 dâhil bütün ticket'lar ölçülüp kapandı. Ticket
dosyaları, yol haritası satırları ve story statüleri aynı durumu söylüyor.

Bu bir sürüm etiketi ya da yayın onayı değildir. Çalışma ağacı hâlâ commit
edilmemiş değişiklikler taşıyor; kullanıcı onayı olmadan commit veya push
yapılmadı.

---

## 1 · Faz faz durum

| Faz | Kapsam | Durum |
| --- | --- | --- |
| **F1** | Ingest, WAL, arşiv, parser, kimlik, API | **Kapandı** — T40 kurtarma ve T63 gerçek kesinti ölçümü dâhil |
| **F2** | BFF, ekranlar, alarm, change, container | **Kapandı** — gerçek OIDC/container E2E iki temada geçti; hazırlık ucu canlı ölçüldü |
| **F3** | Sigma, detection, kanıt, altın küme | **Kapandı** — T32 gerçek Python 3.13 maliyet koşumları kaydedildi |
| **F4** | Redaksiyon, model kapısı, plugin, kota, kalite | **Kapandı** — T47 gerçek yerel model tabanı kaydedildi |
| **F5** | Metrik, trace, topoloji | **Kapsam kararı kapandı** — topology-lite karşılandı; metric/trace gerekçeli muaf |
| **FS** | Cihaz simülatörleri ve cihazsız E2E | **Kapandı** — on ticket tamamlandı |
| **Kapasite** | Üreteç hızı ve varış defteri | **Bugünkü dilim kapandı** — B01/B02 tamamlandı |
| **MCP** | İki yüzey, iki taşıma, kimlik, araçlar, kaynaklar | **Kapandı** — M07 dâhil bütün ticket'lar tamamlandı |

---

## 2 · Açık ticket'lar

**Açık ticket yok.**

Bu cümle makinece okunur bir boş-küme işaretidir. Önceki bekçi en az bir açık
satır bekliyordu; bütün işler kapandığında doğru durumun kendisini hata
sayacaktı. Bekçi artık raporlanan açık kimliklerle gerçek `status != 2`
kümesini **iki yönde** karşılaştırıyor: açık ticket varken bu işaret geçemiyor,
işaret ile açık satırlar birlikte bulunamıyor ve başlık yanlışlıkla değişirse
kapı sessizce yeşil olmuyor.

---

## 3 · Son canlı ölçümler

| Alan | Koşum | Sonuç |
| --- | --- | --- |
| F2 UI container | Gerçek compose + Keycloak OIDC + tohumlanmış veri, light/dark | **2/2 geçti** |
| T62 BFF hazırlık | Bağımlılıklar sağlıklı / `redis-session` kapalı | `200` / **`503`**; eski kök uç kesintide `307` kaldığı için kör olduğu doğrulandı |
| T32 Sigma maliyeti | Python 3.13, 24/100/269 kural, iki koşum | Tam korpus `0,3962` ve `0,3818 ms/kural`; ölçekleme `1,067×` ve `1,013×` |
| T63 RustFS kesintisi | RustFS durdur, 12 gerçek OTLP kayıt, API/WAL yeniden başlat | 12 kabul; koşum segmentinde 12 WAL kaydı; aynı segmentte 1 doğrulanmış manifest ve `event_count=12/12`; reddedilen 0 |
| T47 canlı model | TinyLlama 1.1B, gerçek OpenAI-uyumlu sağlayıcı + cümle bağlayıcı | 16 üretildi, 4 tutuldu, 12 atıldı; `drop_ratio=0,750`; uydurma atıf `0/12` |
| MCP M07 | Birim sözleşmeleri + gerçek stdio ürün grafiği | **78 birim + 5 entegrasyon geçti**; fixture sıra bağımsız hâle getirildi |

T47 sayısı bir ürün eşiği veya model ailesi karşılaştırması değildir. Küçük
yerel modelin sabit sentetik korpustaki ilk canlı tabanıdır. Üretim adayı aynı
korpusla yeniden ölçülür; anlamsal yerindelik bugün insanın `Trivial` / `Sound`
incelemesindedir.

---

## 4 · Açık ticket olmayan kayıtlı sınırlar

Kapanmış bir faz, bütün dünya koşullarının kanıtlandığı anlamına gelmez. Aşağıdakiler
bilinçli sınır veya gelecekte yeni karar gerektiren genişlemelerdir:

- T63 süreç ve gerçek RustFS kesintisini ölçtü; **host/power kaybı** için
  dosya sistemi ve donanım dayanıklılığı ayrıca sınanmalıdır.
- B01/B02 yük üretme ve varış muhasebesini kurdu. B03–B05 ancak hedef donanım,
  `auto` sürdürülebilirlik ölçütü ve üreteç yerleşimi seçildiğinde yeni ticket
  olarak açılır; bugün verilmemiş bir ürün kararını tamamlanmış saymıyoruz.
- F5'te metric ve trace sağlayıcıları gerekçeli, kalıcı muaf; topoloji-lite
  envanter öznitelikleriyle sınırlı, genel ilişki grafiği değil.
- MCP/model sınır kapıları süreç içinden ters vekilin gerçek dış erişimini
  kanıtlayamaz; `127.0.0.1` dinleyen bir süreç dış vekille yayımlanabilir.
- Gerçek RouterOS `/export terse` örneği yok; simülatör bilmediği sayfalama
  davranışını gerçek cihaz olgusu diye taklit etmiyor.
- Canlı T47 korpusu sentetik ve üç paket; eşik koymak için etiketli gerçek
  koşum tabanı gerekir.

Bu maddeler mevcut ticket'ların eksik kabul kriterleri değil. Birinin kapsamı
ürün gereksinimine dönüşürse yeni ticket açılır ve bu rapor yeniden değişir.

---

## 5 · Kapanış kalite kapıları

| Kapı | Sonuç |
| --- | --- |
| Release derleme | **0 uyarı · 0 hata** |
| .NET birim | **1711 geçti · 5 atlandı · 0 düştü** |
| .NET entegrasyon / Testcontainers | **211 geçti · 12 atlandı · 0 düştü** |
| Parser kataloğu | lint temiz · **62/62** örnek geçti |
| UI | OpenAPI birebir · typecheck temiz · **520/520** test · üretim derlemesi geçti |
| Sidecar | **52/52** pytest |
| Sigma build | **205/205** pytest · kolon/çivi/SQL sürüklenme kapıları geçti |
| YAML / compose | tekrarlanan anahtar yok · `docker compose config --quiet` geçti |
| Gerçek UI container E2E | **2/2** tema koşumu geçti |
| T47 canlı model ölçümü | açık bayrakla **1/1** geçti; varsayılan pakette bilinçli atlanıyor |
| T63 ölçüm aracı | koşum-segment-manifest bağı için **5/5** saf Python 3.13 testi geçti |

Bağımsız kapanış incelemesi beş P2 bulgu üretti ve beşi de giderildi:
cümle sonu kısaltmasının sonraki atfı devralması, profil süresi aşınca hedef
EPS'in sulanması, kısa burst penceresinin uyunarak atlanması, T63'ün ilgisiz
manifestle yeşil yanabilmesi ve boş-küme işaretinin gerçek açık ticket
kümelerine bağlanmaması. İlk üç kusur ürün sınıflarına karşı kırmızı testle
yeniden üretildi; düzeltmelerden sonra ilgili paket **34/34** geçti. Statü
kapısı **13/13**, Wiki + statü hedefi **18/18** geçti; T63 ise yukarıdaki saf
kapının ardından gerçek RustFS ile yeniden ölçüldü.

Sidecar koşumu ayrıca gerçek bir bağımlılık kusuru buldu: `pySigma 1.5.0`ın
izin verdiği `pyparsing 3.3.3`, parantezli ek koşulları bozuk parse ağacında
bırakıp beş SQL dönüşümünü düşürüyordu (`47 geçti / 5 düştü`). Aynı beş test
`pyparsing 3.2.5` ile geçti; sürüm sidecar ve build-time hatta birlikte
sabitlendi ve sürüm-paritesi bekçisine eklendi. Son koşumlar yukarıdaki
`52/52` ve `205/205` sonuçlarıdır.

Commit/push yapılmadı; ikisi de ayrıca kullanıcı onayı gerektirir.
